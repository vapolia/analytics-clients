package analytics

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"
)

const installID = "11111111-0000-0000-0000-000011111111"

// collector records what the client posted.
type collector struct {
	mu       sync.Mutex
	batches  []wireBatch
	statuses []int // consumed in order; 204 once exhausted
	header   http.Header
}

func (c *collector) handler(w http.ResponseWriter, r *http.Request) {
	body, _ := io.ReadAll(r.Body)

	var batch wireBatch
	_ = json.Unmarshal(body, &batch)

	c.mu.Lock()
	c.batches = append(c.batches, batch)
	c.header = r.Header.Clone()
	status := http.StatusNoContent
	if len(c.statuses) > 0 {
		status, c.statuses = c.statuses[0], c.statuses[1:]
	}
	c.mu.Unlock()

	w.WriteHeader(status)
}

func (c *collector) recorded() []wireBatch {
	c.mu.Lock()
	defer c.mu.Unlock()
	return append([]wireBatch(nil), c.batches...)
}

func newTestClient(t *testing.T, opts Options) (*Client, *collector) {
	t.Helper()

	col := &collector{}
	server := httptest.NewServer(http.HandlerFunc(col.handler))
	t.Cleanup(server.Close)

	opts.Endpoint = server.URL
	if opts.Source == "" {
		opts.Source = "<sourceName>"
	}
	if opts.FlushInterval == 0 {
		opts.FlushInterval = time.Hour // tests flush explicitly
	}

	client, err := New(opts)
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	t.Cleanup(func() {
		_ = client.Close(context.Background())
	})

	return client, col
}

func flush(t *testing.T, c *Client) {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if err := c.Flush(ctx); err != nil {
		t.Fatalf("Flush: %v", err)
	}
}

func TestSendsBatchWithGlobalProperties(t *testing.T) {
	client, col := newTestClient(t, Options{})

	device := Device{Platform: "Android", Country: "fr", DeviceClass: "phone"}
	context := map[string]any{"plan": "premium"}
	client.TrackContext(installID, device, context, "app_open", nil)
	client.TrackContext(installID, device, context, "game_start", map[string]any{"mode": "solo", "moves": 12})
	flush(t, client)

	batches := col.recorded()
	if len(batches) != 1 {
		t.Fatalf("got %d batches, want 1", len(batches))
	}

	b := batches[0]
	if b.InstallID != installID {
		t.Errorf("installId = %q", b.InstallID)
	}
	if b.Platform != "android" || b.Country != "FR" {
		t.Errorf("platform/country not normalized: %q %q", b.Platform, b.Country)
	}
	if got := string(b.Context); got != `{"plan":"premium"}` {
		t.Errorf("context = %s", got)
	}
	if len(b.Events) != 2 {
		t.Fatalf("got %d events, want 2", len(b.Events))
	}
	if got := b.Events[1].Props["moves"]; got != float64(12) {
		t.Errorf("moves = %v (%T), want 12", got, got)
	}
	if b.Events[0].Ts.IsZero() {
		t.Error("event has no timestamp")
	}
}

func TestGroupsByInstallAndDevice(t *testing.T) {
	client, col := newTestClient(t, Options{})

	client.Track(installID, Device{Platform: "ios"}, "app_open", nil)
	client.Track(installID, Device{Platform: "ios"}, "game_start", nil)
	client.Track(installID, Device{Platform: "ios", Build: "42"}, "app_open", nil)
	client.Track("22222222-0000-0000-0000-000022222222", Device{Platform: "ios"}, "app_open", nil)
	flush(t, client)

	batches := col.recorded()
	if len(batches) != 3 {
		t.Fatalf("got %d batches, want 3 (one per install+device)", len(batches))
	}
}

func TestSendsWhenBatchIsFull(t *testing.T) {
	client, col := newTestClient(t, Options{BatchSize: 5})

	for range 5 {
		client.Track(installID, Device{}, "app_open", nil)
	}

	deadline := time.Now().Add(2 * time.Second)
	for len(col.recorded()) == 0 && time.Now().Before(deadline) {
		time.Sleep(5 * time.Millisecond)
	}

	batches := col.recorded()
	if len(batches) != 1 || len(batches[0].Events) != 5 {
		t.Fatalf("expected one full batch of 5, got %+v", batches)
	}
}

func TestRejectsUnusableInput(t *testing.T) {
	client, col := newTestClient(t, Options{ExcludedCountries: []string{"kr"}})

	cases := []struct {
		name      string
		installID string
		device    Device
		event     string
	}{
		{"empty install id", "", Device{}, "app_open"},
		{"non-uuid install id", "not-a-uuid", Device{}, "app_open"},
		{"nil uuid", "00000000-0000-0000-0000-000000000000", Device{}, "app_open"},
		{"excluded country", installID, Device{Country: "KR"}, "app_open"},
		{"excluded country, lowercase", installID, Device{Country: "kr"}, "app_open"},
		{"blank event name", installID, Device{}, "   "},
	}

	for _, tc := range cases {
		client.Track(tc.installID, tc.device, tc.event, nil)
	}
	flush(t, client)

	if got := col.recorded(); len(got) != 0 {
		t.Fatalf("expected nothing sent, got %+v", got)
	}
	if stats := client.Stats(); stats.Rejected != uint64(len(cases)) {
		t.Errorf("rejected = %d, want %d", stats.Rejected, len(cases))
	}
}

func TestDropsNonScalarAndOversizedProps(t *testing.T) {
	client, col := newTestClient(t, Options{})

	props := map[string]any{
		"mode":   strings.Repeat("x", MaxValueLength+10),
		"nested": map[string]any{"a": 1},
		"list":   []int{1, 2},
		"ok":     true,
	}
	for i := range MaxPropsPerEvent + 5 {
		props["extra"+string(rune('a'+i))] = i
	}

	client.Track(installID, Device{}, "game_end", props)
	flush(t, client)

	batches := col.recorded()
	if len(batches) != 1 {
		t.Fatalf("got %d batches, want 1", len(batches))
	}

	kept := batches[0].Events[0].Props
	if len(kept) > MaxPropsPerEvent {
		t.Errorf("kept %d props, want at most %d", len(kept), MaxPropsPerEvent)
	}
	if _, found := kept["nested"]; found {
		t.Error("kept an object property")
	}
	if _, found := kept["list"]; found {
		t.Error("kept an array property")
	}
	if mode, found := kept["mode"]; found {
		if s, _ := mode.(string); len([]rune(s)) != MaxValueLength {
			t.Errorf("mode length = %d, want %d", len([]rune(s)), MaxValueLength)
		}
	}
}

func TestRetriesThenGivesUp(t *testing.T) {
	client, col := newTestClient(t, Options{MaxAttempts: 3})
	col.statuses = []int{http.StatusInternalServerError, http.StatusInternalServerError, http.StatusInternalServerError}

	client.Track(installID, Device{}, "app_open", nil)
	flush(t, client)

	if got := len(col.recorded()); got != 3 {
		t.Errorf("collector saw %d requests, want 3", got)
	}
	if stats := client.Stats(); stats.Dropped != 1 || stats.Sent != 0 {
		t.Errorf("stats = %+v, want 1 dropped, 0 sent", stats)
	}
}

func TestRetrySucceeds(t *testing.T) {
	client, col := newTestClient(t, Options{})
	col.statuses = []int{http.StatusInternalServerError}

	client.Track(installID, Device{}, "app_open", nil)
	flush(t, client)

	if stats := client.Stats(); stats.Sent != 1 || stats.Dropped != 0 {
		t.Errorf("stats = %+v, want 1 sent, 0 dropped", stats)
	}
}

func TestUnknownSourceIsNotRetried(t *testing.T) {
	client, col := newTestClient(t, Options{Source: "nope"})
	col.statuses = []int{http.StatusNotFound}

	client.Track(installID, Device{}, "app_open", nil)
	flush(t, client)

	if got := len(col.recorded()); got != 1 {
		t.Errorf("collector saw %d requests, want 1 (404 is permanent)", got)
	}
}

func TestCloseFlushesPendingEvents(t *testing.T) {
	client, col := newTestClient(t, Options{})

	client.Track(installID, Device{}, "app_open", nil)
	if err := client.Close(context.Background()); err != nil {
		t.Fatalf("Close: %v", err)
	}

	if got := col.recorded(); len(got) != 1 {
		t.Fatalf("got %d batches after Close, want 1", len(got))
	}
	if err := client.Flush(context.Background()); err != ErrClosed {
		t.Errorf("Flush after Close = %v, want ErrClosed", err)
	}
	if err := client.Close(context.Background()); err != nil {
		t.Errorf("second Close: %v", err)
	}
}

func TestDropsStaleEvents(t *testing.T) {
	now := time.Now().UTC()
	client, col := newTestClient(t, Options{Now: func() time.Time { return now }})

	client.Track(installID, Device{}, "app_open", nil)
	now = now.Add(MaxEventAge + time.Hour)
	flush(t, client)

	if got := col.recorded(); len(got) != 0 {
		t.Fatalf("expected nothing sent, got %+v", got)
	}
	if stats := client.Stats(); stats.Dropped != 1 {
		t.Errorf("dropped = %d, want 1", stats.Dropped)
	}
}

func TestSessionTracks(t *testing.T) {
	client, col := newTestClient(t, Options{})

	session := client.For(installID, Device{Platform: "web"})
	session.Track("qr_reveal", map[string]any{"page": "home"})
	flush(t, client)

	batches := col.recorded()
	if len(batches) != 1 || batches[0].Platform != "web" {
		t.Fatalf("unexpected batches: %+v", batches)
	}
}

func TestQueueOverflowDropsInsteadOfBlocking(t *testing.T) {
	// A queue of one and no sender running yet: the point is that Track returns either way.
	client, _ := newTestClient(t, Options{QueueSize: 1})

	for range 200 {
		client.Track(installID, Device{}, "app_open", nil)
	}

	stats := client.Stats()
	if stats.Accepted+stats.Dropped != 200 {
		t.Errorf("accepted+dropped = %d, want 200", stats.Accepted+stats.Dropped)
	}
}

func TestNewValidatesOptions(t *testing.T) {
	if _, err := New(Options{Source: "<sourceName>"}); err == nil {
		t.Error("expected an error without Endpoint")
	}
	if _, err := New(Options{Endpoint: "https://analytics.example.com"}); err == nil {
		t.Error("expected an error without Source")
	}
}

func TestPostsJSONContentType(t *testing.T) {
	client, col := newTestClient(t, Options{})

	client.Track(installID, Device{}, "app_open", nil)
	flush(t, client)

	col.mu.Lock()
	defer col.mu.Unlock()
	if got := col.header.Get("Content-Type"); got != "application/json" {
		t.Errorf("Content-Type = %q", got)
	}
}

func TestSessionSendsTheTimeZoneOfItsUser(t *testing.T) {
	client, col := newTestClient(t, Options{})

	client.For(installID, Device{Platform: "web"}).WithTimeZone(120).Track("app_open", nil)
	client.Track(installID, Device{Platform: "web"}, "game_end", nil)
	flush(t, client)

	withTz, withoutTz := 0, 0
	for _, b := range col.recorded() {
		for _, e := range b.Events {
			switch {
			case e.Tz == nil:
				withoutTz++
			case *e.Tz == 120:
				withTz++
			}
		}
	}
	if withTz != 1 || withoutTz != 1 {
		t.Errorf("events with tz 120 = %d, without tz = %d, want 1 and 1", withTz, withoutTz)
	}
}

func TestAnOffsetOutsideTheRealRangeIsDropped(t *testing.T) {
	client, col := newTestClient(t, Options{})

	// Seconds sent for minutes.
	client.For(installID, Device{Platform: "web"}).WithTimeZone(7200).Track("app_open", nil)
	flush(t, client)

	for _, b := range col.recorded() {
		for _, e := range b.Events {
			if e.Tz != nil {
				t.Errorf("tz = %d, want none", *e.Tz)
			}
		}
	}
}
