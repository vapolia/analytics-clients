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

// Options configures a Client. Only Endpoint and Source are required.
type Options struct {
	// Endpoint is the collector's base URL, e.g. "https://analytics.example.com".
	Endpoint string

	// Source is the app's name: the URL segment, and the Postgres schema it maps to.
	Source string

	// HTTPClient defaults to a client with a 10s timeout.
	HTTPClient *http.Client

	// QueueSize is how many events may wait for the background sender. Beyond it Track drops rather
	// than blocks. Default 4096.
	QueueSize int

	// BatchSize is how many events of one (install, device) group are sent in one request.
	// Default and maximum 100, the collector's own per-batch ceiling.
	BatchSize int

	// FlushInterval is how often pending events are sent even when no batch is full. Default 30s.
	FlushInterval time.Duration

	// APIKey is a server token issued by the collector's admin service, sent as
	// "Authorization: Bearer". It raises this caller's rate-limit ceiling and names its own os and
	// build, which the collector stores in place of any device's.
	APIKey string

	// ExcludedCountries are ISO 3166-1 alpha-2 countries not measured at all: nothing is sent for a
	// device whose region is one of them. Copy the source's excludedCountries.
	ExcludedCountries []string

	// MaxAttempts is how many times one request is tried before its events are dropped. Default 3.
	MaxAttempts int

	// Logger receives transport failures. Optional.
	Logger Logger

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
	endpoint string
	excluded map[string]bool

	queue    chan pending
	flushReq chan chan struct{}
	stop     chan struct{}
	stopOnce sync.Once
	done     chan struct{}

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
	if strings.TrimSpace(opts.Endpoint) == "" {
		return nil, errors.New("analytics: Endpoint is required")
	}
	if strings.TrimSpace(opts.Source) == "" {
		return nil, errors.New("analytics: Source is required")
	}

	if opts.HTTPClient == nil {
		opts.HTTPClient = &http.Client{Timeout: 10 * time.Second}
	}
	if opts.QueueSize <= 0 {
		opts.QueueSize = 4096
	}
	if opts.BatchSize <= 0 || opts.BatchSize > MaxEventsPerBatch {
		opts.BatchSize = MaxEventsPerBatch
	}
	if opts.FlushInterval <= 0 {
		opts.FlushInterval = 30 * time.Second
	}
	if opts.MaxAttempts <= 0 {
		opts.MaxAttempts = 3
	}
	if opts.Now == nil {
		opts.Now = time.Now
	}

	excluded := make(map[string]bool, len(opts.ExcludedCountries))
	for _, country := range opts.ExcludedCountries {
		excluded[strings.ToUpper(strings.TrimSpace(country))] = true
	}

	c := &Client{
		opts:     opts,
		excluded: excluded,
		endpoint: strings.TrimRight(opts.Endpoint, "/") + "/" + strings.Trim(opts.Source, "/"),
		queue:    make(chan pending, opts.QueueSize),
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
	id, ok := cleanInstallID(installID)
	if !ok {
		c.rejected.Add(1)
		return
	}

	eventName := text(name, MaxValueLength)
	if eventName == "" {
		c.rejected.Add(1)
		return
	}

	cleanDevice, ok := device.clean(c.excluded)
	if !ok { // excluded country
		c.rejected.Add(1)
		return
	}

	item := pending{
		key: batchKey{installID: id, device: cleanDevice, context: encodeContext(context)},
		event: wireEvent{
			Name:  eventName,
			Ts:    c.opts.Now().UTC(),
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
	}
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
	ticker := time.NewTicker(c.opts.FlushInterval)
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
	if len(events) >= c.opts.BatchSize {
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
	if c.opts.Logger == nil {
		return
	}
	c.opts.Logger.Warn("analytics: %s: %v", fmt.Sprintf(format, v...), err)
}

func (c *Client) errorf(err error, format string, v ...any) {
	if c.opts.Logger == nil {
		return
	}
	c.opts.Logger.Error("analytics: %s: %v", fmt.Sprintf(format, v...), err)
}
