// Package analytics is a Go client for the Vapolia audience-measurement collector
// (POST {endpoint}/{source}). It is written for server-side callers so it batches in the background and never blocks the caller.
//
// Two rules this package cannot enforce and the caller must hold up: pass through the install id
// the device generated, never one derived from a user id or a session, and set Device.Country from
// the device's region setting, never from the client address.
package analytics

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

// Logger is what the client reports transport failures to.
type Logger interface {
	Warn(format string, v ...any)
	Error(format string, v ...any)
}

// Options configures a Client. Only IngestionUrl is required.
//
// The shape mirrors the .NET client, which is this repository's reference: the few options a caller
// actually sets sit here, the rest in Advanced.
type Options struct {
	// IngestionUrl is the collector's ingestion URL — "https://baseUrl/sourceName". The last path
	// segment is the source: the app's name, and the Postgres schema it maps to.
	IngestionUrl string

	// Token is the credential sent as "Authorization: Bearer" — a server token for server-to-server
	// analytics, or a build token for device-to-server analytics. The collector tells the two apart
	// from the token itself, not from how it arrived, so one field covers both roles.
	Token string

	// Enabled toggles collection. False makes New return a client that accepts and drops everything.
	// It is a pointer so the zero Options still means enabled.
	Enabled *bool

	// ExcludedCountries are ISO 3166-1 alpha-2 countries excluded from the collection. Should be a
	// copy of the exclusion list of the collector.
	ExcludedCountries []string

	// Context is what is true of the installation for a whole batch, asked for again on every event.
	// Every key must be on the source's context whitelist. Optional.
	Context func() map[string]any

	// Advanced holds the uncommon options.
	Advanced AdvancedOptions
}

// AdvancedOptions are the uncommon options. The defaults are the ones every client in this
// repository ships.
type AdvancedOptions struct {
	// FlushInterval is how long events are buffered before they go out. Default 30s.
	FlushInterval time.Duration

	// MaxEventsPerWindow caps events accepted per RateWindow. Zero — the default here — disables it:
	// one server process speaks for every visitor, so a per-process ceiling would throttle a whole
	// site. The mobile clients default to 30.
	MaxEventsPerWindow int

	// RateWindow is the window MaxEventsPerWindow counts in. Fixed, not sliding. Default 60s.
	RateWindow time.Duration

	// BatchSize is how many events of one group are sent in one request. Default and maximum 100,
	// the collector's own per-batch ceiling.
	BatchSize int

	// QueueCapacity is how many events may wait for the background sender. Beyond it Track drops
	// rather than blocks. Default 4000.
	QueueCapacity int

	// MaxAttempts is how many times one request is tried before its events are dropped. Default 3.
	MaxAttempts int

	// HTTPClient is what the sender posts with; its Timeout is this client's RequestTimeout.
	// Defaults to a client with a 10s timeout.
	HTTPClient *http.Client

	// Logger receives transport failures. Optional.
	Logger Logger

	// OnError is called on every loss, next to the log: (err, reason, permanent). Optional.
	OnError func(err error, reason string, permanent bool)

	// Now defaults to time.Now. Tests set it.
	Now func() time.Time
}

// Stats is a snapshot of what the client did since it was created.
type Stats struct {
	Accepted uint64 // queued by Track
	Rejected uint64 // refused by Track: bad install id, excluded country, unusable event name
	Dropped  uint64 // accepted then lost: full queue, or a request that failed every attempt
	Sent     uint64 // events the collector answered 2xx for
	Requests uint64 // requests issued, retries included
}

// Client queues events and sends them in the background. Safe for concurrent use.
type Client struct {
	opts     Options
	adv      AdvancedOptions
	endpoint string
	source   string
	excluded map[string]bool

	enabled bool

	queue    chan pending
	flushReq chan chan struct{}
	stop     chan struct{}
	stopOnce sync.Once
	done     chan struct{}

	// The client's own ceiling, counted in a fixed window. Disabled by default here: one server
	// process speaks for every visitor, so a per-process ceiling would throttle a whole site.
	rateMu      sync.Mutex
	windowStart time.Time
	windowCount int

	accepted, rejected, dropped, sent, requests atomic.Uint64
}

type batchKey struct {
	installID string
	device    Device
	// context is the canonical JSON of the batch context. A string, so the key stays comparable and
	// two identical contexts group into one request.
	context string
}

type pending struct {
	key   batchKey
	event wireEvent
}

// ErrClosed is returned by Flush after Close.
var ErrClosed = errors.New("analytics: client closed")

// New starts a client and its background sender. Close it to flush what is pending.
func New(opts Options) (*Client, error) {
	url := strings.TrimRight(strings.TrimSpace(opts.IngestionUrl), "/")
	if url == "" {
		return nil, errors.New("analytics: IngestionUrl is required")
	}
	// The app's name is the URL's last segment, exactly as in the .NET client.
	source := url[strings.LastIndex(url, "/")+1:]
	if source == "" || !strings.Contains(url, "://") {
		return nil, errors.New("analytics: IngestionUrl must be https://baseUrl/sourceName")
	}

	adv := opts.Advanced
	if adv.HTTPClient == nil {
		adv.HTTPClient = &http.Client{Timeout: 10 * time.Second}
	}
	if adv.QueueCapacity <= 0 {
		adv.QueueCapacity = 4000
	}
	if adv.BatchSize <= 0 || adv.BatchSize > MaxEventsPerBatch {
		adv.BatchSize = MaxEventsPerBatch
	}
	if adv.FlushInterval <= 0 {
		adv.FlushInterval = 30 * time.Second
	}
	if adv.RateWindow <= 0 {
		adv.RateWindow = 60 * time.Second
	}
	if adv.MaxAttempts <= 0 {
		adv.MaxAttempts = 3
	}
	if adv.Now == nil {
		adv.Now = time.Now
	}

	excluded := make(map[string]bool, len(opts.ExcludedCountries))
	for _, country := range opts.ExcludedCountries {
		excluded[strings.ToUpper(strings.TrimSpace(country))] = true
	}

	c := &Client{
		opts:     opts,
		excluded: excluded,
		enabled:  opts.Enabled == nil || *opts.Enabled,
		adv:      adv,
		endpoint: url,
		source:   source,
		queue:    make(chan pending, adv.QueueCapacity),
		flushReq: make(chan chan struct{}),
		stop:     make(chan struct{}),
		done:     make(chan struct{}),
	}

	go c.run()
	return c, nil
}

// Track queues one event. It never blocks and returns no error; use Stats to see what was refused
// or lost. installID must be the UUID the device generated, and name must be on the source's
// whitelist, which this package cannot know.
func (c *Client) Track(installID string, device Device, name string, props map[string]any) {
	c.TrackContext(installID, device, nil, name, props)
}

// TrackContext is Track with the batch context the app reported: what is true of the installation
// rather than of one event. Every key must be on the source's context whitelist.
func (c *Client) TrackContext(installID string, device Device, context map[string]any, name string, props map[string]any) {
	c.track(installID, device, context, nil, name, props)
}

// track is the one path every Track goes through. tz is the UTC offset of the caller's own user, when the caller knows it.
func (c *Client) track(installID string, device Device, context map[string]any, tz *int, name string, props map[string]any) {
	if !c.enabled {
		return
	}

	if !c.withinRate() {
		c.dropped.Add(1)
		c.report(nil, fmt.Sprintf("dropping %q: the rate window is saturated", name), false)
		return
	}

	id, ok := cleanInstallID(installID)
	if !ok {
		c.rejected.Add(1)
		c.report(nil, fmt.Sprintf("refusing %q: unusable install id", name), true)
		return
	}

	eventName := text(name, MaxValueLength)
	if eventName == "" {
		c.rejected.Add(1)
		c.report(nil, "refusing an event with an unusable name", true)
		return
	}

	cleanDevice, ok := device.clean(c.excluded)
	if !ok { // excluded country
		c.rejected.Add(1)
		c.report(nil, fmt.Sprintf("refusing %q: excluded country", name), true)
		return
	}

	// The caller's own context wins; Options.Context is the fallback for a caller that has one
	// answer for the whole process.
	if context == nil && c.opts.Context != nil {
		context = c.opts.Context()
	}

	item := pending{
		key: batchKey{installID: id, device: cleanDevice, context: encodeContext(context)},
		event: wireEvent{
			Name:  eventName,
			Ts:    c.adv.Now().UTC(),
			Props: cleanProps(props),
			Tz:    cleanTz(tz),
		},
	}

	select {
	case c.queue <- item:
		c.accepted.Add(1)
	default:
		// Full queue: the collector is unreachable, or slower than we emit.
		c.dropped.Add(1)
		c.report(nil, fmt.Sprintf("dropping %q: the queue is full", name), false)
	}
}

// withinRate is the client's own ceiling, counted in a fixed window: once it is full everything is
// dropped until the window ends, rather than queued for a collector that would refuse the whole
// address. Zero — the default here — disables it.
func (c *Client) withinRate() bool {
	if c.adv.MaxEventsPerWindow <= 0 {
		return true
	}

	c.rateMu.Lock()
	defer c.rateMu.Unlock()

	now := c.adv.Now()
	if now.Sub(c.windowStart) >= c.adv.RateWindow {
		c.windowStart = now
		c.windowCount = 0
	}
	if c.windowCount >= c.adv.MaxEventsPerWindow {
		return false
	}

	c.windowCount++
	return true
}

// Session binds an installation, its device and its batch context so call sites only carry the event.
type Session struct {
	client    *Client
	installID string
	device    Device
	context   map[string]any
	tz        *int
}

// For returns a Session for one installation. The device context is captured as of this call.
func (c *Client) For(installID string, device Device) Session {
	return Session{client: c, installID: installID, device: device}
}

// WithContext returns the same session carrying a batch context.
func (s Session) WithContext(context map[string]any) Session {
	s.context = context
	return s
}

// WithTimeZone returns the same session carrying the user's UTC offset, in minutes east of UTC, as
// the caller's app or page reported it. Outside the real range of offsets it is dropped.
func (s Session) WithTimeZone(minutes int) Session {
	s.tz = &minutes
	return s
}

// Track queues one event for the session's installation.
func (s Session) Track(name string, props map[string]any) {
	if s.client == nil {
		return
	}
	s.client.track(s.installID, s.device, s.context, s.tz, name, props)
}

// Flush sends everything queued so far and waits for it. It returns when the sender is done, when
// ctx expires, or ErrClosed if the client is already closed.
func (c *Client) Flush(ctx context.Context) error {
	ack := make(chan struct{})
	select {
	case c.flushReq <- ack:
	case <-c.done:
		return ErrClosed
	case <-ctx.Done():
		return ctx.Err()
	}

	select {
	case <-ack:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}

// Close stops the sender after one last flush. It returns ctx's error if that flush outlives ctx;
// the sender stops either way. Calling it twice is safe.
func (c *Client) Close(ctx context.Context) error {
	c.stopOnce.Do(func() { close(c.stop) })

	select {
	case <-c.done:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}

// Stats returns a snapshot of the counters.
func (c *Client) Stats() Stats {
	return Stats{
		Accepted: c.accepted.Load(),
		Rejected: c.rejected.Load(),
		Dropped:  c.dropped.Load(),
		Sent:     c.sent.Load(),
		Requests: c.requests.Load(),
	}
}

// run owns every buffer, so nothing here needs a lock. Sending happens on this goroutine too: the
// queue is the buffer, and a single sender keeps one installation's requests in order.
func (c *Client) run() {
	defer close(c.done)

	buffers := make(map[batchKey][]wireEvent)
	ticker := time.NewTicker(c.adv.FlushInterval)
	defer ticker.Stop()

	for {
		select {
		case item := <-c.queue:
			c.buffer(buffers, item)

		case ack := <-c.flushReq:
			c.drain(buffers)
			c.flushAll(buffers)
			close(ack)

		case <-ticker.C:
			c.flushAll(buffers)

		case <-c.stop:
			c.drain(buffers)
			c.flushAll(buffers)
			return
		}
	}
}

// drain moves everything Track already accepted into the buffers, so a Flush or a Close covers it.
func (c *Client) drain(buffers map[batchKey][]wireEvent) {
	for {
		select {
		case item := <-c.queue:
			c.buffer(buffers, item)
		default:
			return
		}
	}
}

func (c *Client) buffer(buffers map[batchKey][]wireEvent, item pending) {
	events := append(buffers[item.key], item.event)
	if len(events) >= c.adv.BatchSize {
		delete(buffers, item.key)
		c.send(item.key, events)
		return
	}
	buffers[item.key] = events
}

func (c *Client) flushAll(buffers map[batchKey][]wireEvent) {
	for key, events := range buffers {
		delete(buffers, key)
		c.send(key, events)
	}
}

func (c *Client) warnf(err error, format string, v ...any) {
	reason := fmt.Sprintf(format, v...)
	if c.adv.Logger != nil {
		c.adv.Logger.Warn("analytics: %s: %v", reason, err)
	}
	if c.adv.OnError != nil {
		c.adv.OnError(err, reason, false)
	}
}

// report is the loss path that has no error of its own: a refusal, a full queue, a saturated
// window. It reaches the log and, when the caller asked for one, OnError.
func (c *Client) report(err error, reason string, permanent bool) {
	if c.adv.Logger != nil {
		if permanent {
			c.adv.Logger.Error("analytics: %s", reason)
		} else {
			c.adv.Logger.Warn("analytics: %s", reason)
		}
	}
	if c.adv.OnError != nil {
		c.adv.OnError(err, reason, permanent)
	}
}

func (c *Client) errorf(err error, format string, v ...any) {
	reason := fmt.Sprintf(format, v...)
	if c.adv.Logger != nil {
		c.adv.Logger.Error("analytics: %s: %v", reason, err)
	}
	if c.adv.OnError != nil {
		c.adv.OnError(err, reason, true)
	}
}
