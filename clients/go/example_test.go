package analytics_test

import (
	"context"
	"time"

	analytics "github.com/vapolia/analytics-clients/clients/go"
)

// A runtime module may create one client at startup and closes it at shutdown. 
// The install id and the device context come from the client app.
func Example() {
	client, err := analytics.New(analytics.Options{
		Endpoint: "https://analytics.example.com",
		Source:   "<sourceName>",
	})
	if err != nil {
		return
	}
	defer func() {
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_ = client.Close(ctx)
	}()

	device := analytics.Device{
		Platform:    "android",
		Country:     "FR",
		DeviceClass: "phone",
		Build:       "1042",
	}

	// The batch context is what the app reported about the installation, whitelisted per source.
	session := client.
		For("11111111-0000-0000-0000-000011111111", device).
		WithContext(map[string]any{"plan": "free"})
	session.Track("online_create", map[string]any{"mode": "duel"})
	session.Track("game_end", map[string]any{"result": "win", "moves": 34})
}
