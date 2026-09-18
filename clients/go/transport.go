package analytics

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strconv"
	"strings"
	"time"
)

// The wire shape of POST /{source}. What describes the device sits on the batch, not on each event.
type wireBatch struct {
	InstallID string `json:"installId"`

	Build       string `json:"build,omitempty"`
	Platform    string `json:"platform,omitempty"`
	OsVersion   string `json:"osVersion,omitempty"`
	DeviceClass string `json:"deviceClass,omitempty"`
	Locale      string `json:"locale,omitempty"`
	Country     string `json:"country,omitempty"`
	Store       string `json:"store,omitempty"`

	// Context is already cleaned and canonical JSON. Raw so the batch key can stay a string.
	Context json.RawMessage `json:"context,omitempty"`

	Events []wireEvent `json:"events"`
}

type wireEvent struct {
	Name  string         `json:"name"`
	Ts    time.Time      `json:"ts"`
	Tz    *int           `json:"tz,omitempty"` // minutes east of UTC, apart from Ts which stays UTC
	Props map[string]any `json:"props,omitempty"`
}

// maxRetryDelay caps how long the sender sleeps before a retry. It runs on the goroutine that
// drains the queue, so a long sleep is paid in dropped events.
const maxRetryDelay = 5 * time.Second

// send posts one group and accounts for what happened. It never returns an error: retrying a few
// times, then giving up, is everything that can be done about a failure.
func (c *Client) send(key batchKey, events []wireEvent) {
	events = c.fresh(events)
	if len(events) == 0 {
		return
	}

	body, err := json.Marshal(wireBatch{
		InstallID:   key.installID,
		Build:       key.device.Build,
		Platform:    key.device.Platform,
		OsVersion:   key.device.OsVersion,
		DeviceClass: key.device.DeviceClass,
		Locale:      key.device.Locale,
		Country:     key.device.Country,
		Store:       key.device.Store,
		Context:     rawContext(key.context),
		Events:      events,
	})
	if err != nil {
		c.dropped.Add(uint64(len(events)))
		c.errorf(err, "encoding a batch of %d events", len(events))
		return
	}

	for attempt := 1; ; attempt++ {
		wait, err := c.post(body)
		if err == nil {
			c.sent.Add(uint64(len(events)))
			return
		}

		var perm permanentError
		if errors.As(err, &perm) || attempt >= c.opts.MaxAttempts {
			c.dropped.Add(uint64(len(events)))
			c.errorf(err, "dropping %d events after %d attempt(s)", len(events), attempt)
			return
		}

		c.warnf(err, "retrying %d events (attempt %d)", len(events), attempt)
		if !c.sleep(backoff(attempt, wait)) {
			// Closing: give the batch one last chance rather than sleeping through the shutdown.
			if _, err := c.post(body); err == nil {
				c.sent.Add(uint64(len(events)))
				return
			}
			c.dropped.Add(uint64(len(events)))
			return
		}
	}
}

// post issues one request. The returned duration is the server's Retry-After, when it sent one.
func (c *Client) post(body []byte) (time.Duration, error) {
	c.requests.Add(1)

	ctx := context.Background()
	if timeout := c.opts.HTTPClient.Timeout; timeout > 0 {
		var cancel context.CancelFunc
		ctx, cancel = context.WithTimeout(ctx, timeout)
		defer cancel()
	}

	req, err := http.NewRequestWithContext(ctx, http.MethodPost, c.endpoint, bytes.NewReader(body))
	if err != nil {
		return 0, permanentError{err}
	}
	req.Header.Set("Content-Type", "application/json")
	// Only ever a rate-limit ceiling; the collector accepts the batch either way.
	if c.opts.APIKey != "" {
		req.Header.Set("Authorization", "Bearer "+c.opts.APIKey)
	}

	resp, err := c.opts.HTTPClient.Do(req)
	if err != nil {
		return 0, err
	}
	defer func() {
		_ = resp.Body.Close()
	}()
	// The collector answers 204 with no body; read it out anyway so the connection is reused.
	_, _ = io.Copy(io.Discard, io.LimitReader(resp.Body, 4096))

	switch {
	case resp.StatusCode >= 200 && resp.StatusCode < 300:
		return 0, nil

	case resp.StatusCode == http.StatusTooManyRequests:
		return retryAfter(resp), fmt.Errorf("rate limited (429)")

	// 404 means this source is not configured there, 400 that the payload is not what it accepts.
	case resp.StatusCode == http.StatusNotFound:
		return 0, permanentError{fmt.Errorf("unknown source %q (404)", c.opts.Source)}
	case resp.StatusCode >= 400 && resp.StatusCode < 500:
		return 0, permanentError{fmt.Errorf("refused with %d", resp.StatusCode)}

	default:
		return 0, fmt.Errorf("collector answered %d", resp.StatusCode)
	}
}

// fresh drops what the collector would refuse on arrival.
func (c *Client) fresh(events []wireEvent) []wireEvent {
	cutoff := c.opts.Now().UTC().Add(-MaxEventAge)
	kept := events[:0]
	for _, e := range events {
		if e.Ts.After(cutoff) {
			kept = append(kept, e)
		} else {
			c.dropped.Add(1)
		}
	}
	return kept
}

// sleep waits, and reports false if the client was closed meanwhile.
func (c *Client) sleep(d time.Duration) bool {
	timer := time.NewTimer(d)
	defer timer.Stop()

	select {
	case <-timer.C:
		return true
	case <-c.stop:
		return false
	}
}

func backoff(attempt int, retryAfter time.Duration) time.Duration {
	d := retryAfter
	if d <= 0 {
		d = time.Duration(1<<uint(attempt-1)) * 250 * time.Millisecond
	}
	return min(d, maxRetryDelay)
}

func retryAfter(resp *http.Response) time.Duration {
	seconds, err := strconv.Atoi(strings.TrimSpace(resp.Header.Get("Retry-After")))
	if err != nil || seconds < 0 {
		return 0
	}
	return time.Duration(seconds) * time.Second
}

// permanentError marks a failure that a retry cannot fix.
type permanentError struct{ err error }

func (e permanentError) Error() string { return e.err.Error() }
func (e permanentError) Unwrap() error { return e.err }

// cleanInstallID accepts the canonical UUID form only, and refuses the nil UUID.
func cleanInstallID(value string) (string, bool) {
	id := strings.ToLower(strings.TrimSpace(value))
	if len(id) != 36 {
		return "", false
	}

	isNil := true
	for i, r := range id {
		switch i {
		case 8, 13, 18, 23:
			if r != '-' {
				return "", false
			}
		default:
			if !isHex(r) {
				return "", false
			}
			if r != '0' {
				isNil = false
			}
		}
	}

	if isNil {
		return "", false
	}
	return id, true
}

func isHex(r rune) bool {
	return (r >= '0' && r <= '9') || (r >= 'a' && r <= 'f')
}

// rawContext turns the canonical text the batch key carries into the JSON the payload embeds. An
// empty context is absent from the wire, which is what the collector reads as "none".
func rawContext(context string) json.RawMessage {
	if len(context) <= 2 {
		return nil
	}
	return json.RawMessage(context)
}
