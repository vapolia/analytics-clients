# Go client

A dependency-free Go client for the collector (`POST {endpoint}/{source}`), written for server-side callers.

## Summary

```go
client, _ := analytics.New(analytics.Options{IngestionUrl: "https://analytics.example.com/<sourceName>"})
defer client.Close(ctx)

session := client.
	For(installID, analytics.Device{Country: "FR"}).
	WithContext(map[string]any{"plan": "free"}) // what the app reported about the installation
session.Track("game_end", map[string]any{"result": "win", "moves": 34})
```

- `Track` never blocks and never returns an error. Events are grouped per (install id, device,
  context), batched, and sent by one background goroutine.
- `installID` is the UUID **the device generated**.
- Event names and property keys must be on the source's whitelist in
  the collector's configuration; anything else is dropped, silently, by
  the collector.
- `Close(ctx)` flushes what is pending. Without it, in-flight events are lost.

## Install

```bash
go get github.com/vapolia/analytics-clients/clients/go
```

Go 1.24+, standard library only

An RPC is the usual entry point: the app sends its own install id and device context in the payload,
and the module forwards them. The server has no device context of its own — do not fabricate one.

`first_open` and `app_open` belong to the app, which alone knows an installation's first run and its
returns to the foreground. This client has no lifecycle and no stored identity: it sends what the app
tells it to. The right of opposition belongs to the app as well — this client stores nothing to
oppose, so an opposed installation simply stops calling the RPC.

## API

| | |
|---|---|
| `New(Options) (*Client, error)` | Starts the client and its sender. |
| `Track(installID string, device Device, name string, props map[string]any)` | Queues one event. Drops on a full queue rather than blocking. |
| `For(installID, device) Session` → `Session.Track(name, props)` | Same, with the install and device bound once. |
| `Flush(ctx) error` | Sends what is queued and waits for it. |
| `Close(ctx) error` | One last flush, then stops the sender. Idempotent. |
| `Stats() Stats` | Accepted / Rejected / Dropped / Sent / Requests counters. |

`Options` mirrors the .NET client, which is this repository's reference: `IngestionUrl`
(`https://baseUrl/sourceName`, required), `Token`, `Enabled`, `ExcludedCountries`, `Context` — and
the rest under `Advanced`:

```go
client, _ := analytics.New(analytics.Options{
	IngestionUrl: "https://analytics.example.com/myapp",
	Token:        os.Getenv("ANALYTICS_TOKEN"),
	Advanced:     analytics.AdvancedOptions{QueueCapacity: 4000, Logger: myLogger},
})
```

`Advanced`: `FlushInterval` (30s), `MaxEventsPerWindow` (**0 here**, disabled — one server process
speaks for every visitor, so a per-process ceiling would throttle a whole site; the mobile clients
default to 30), `RateWindow` (60s), `BatchSize` (100, the collector's ceiling), `QueueCapacity`
(4000), `MaxAttempts` (3), `HTTPClient` (10s timeout — its `Timeout` is this client's
`RequestTimeout`), `Logger`, `OnError` (`(err, reason, permanent)`, called on every loss next to the
log), `Now`.

There is no `SpoolPath`, `SpoolCapacity`, `InstallIdLifetime`, `OptOutLifetime` or `SeedInstallId`
here: a server has no installation of its own, and no process death worth spooling across. The names
that *are* here are the ones the other clients use.

`Device` is `Country` alone — the region setting the app reported, never derived from the client
address. The platform, the build, the OS version, the device class and the store come from the
`Authorization` token; see the [contract](https://github.com/vapolia/analytics-clients/blob/main/clients/README.md#payload). It stays comparable, so it can key
the batching.

What is true of the *installation* — a plan, a finished tutorial, an install-age bucket — is the batch
context: `Session.WithContext(map[string]any{...})`, or `Client.TrackContext(...)`. Its keys are
whitelisted per source under `context:`, exactly like property keys, and it is part of the batch key:
two contexts never share a request. The server has none of this of its own — the app sends it in the
RPC payload, like the install id and the device.

A server knows no time zone of its users either: `Session.WithTimeZone(minutes)` sends the offset its app or page reported, in minutes east of UTC, with each event. An offset outside UTC−12:00..UTC+14:00 is dropped.

## Failure behaviour

Measurement must never fail a game action, so nothing here surfaces an error to the caller:

- **Full queue** — the newest event is dropped, `Track` returns. Counted in `Stats().Dropped`.
- **5xx, network error, 429** — retried up to `MaxAttempts`, honouring `Retry-After`, then dropped.
  Backoff is capped at 5s because the sender shares the goroutine that drains the queue.
- **404 / 4xx** — permanent (unknown source, malformed payload); dropped without a retry and logged
  through `Logger`.
- **Events older than 7 days** — dropped before being sent; the collector refuses them anyway.

Rate limiting is per client address, and a game server is one address for all its players: the
collector's default is 60 requests per minute per pod. Batching is what keeps this within reach —
100 events per request, one request per (install, device) group — so prefer a larger `FlushInterval`
over a smaller one, and raise `RateLimit:PermitsPerWindow` if a server legitimately needs more.

## What this client cannot check for you

- `installID` must be the device's own rotated random id. A user id, a session id, or a hash
  of either would create the join to a person that the whole design avoids — see [OBLIGATIONS.md](https://github.com/vapolia/analytics-clients/blob/main/clients/OBLIGATIONS.md).
- `Country` is the device's region setting. Never geolocate the client address.
- `excludedCountries` is empty by default: pass the list that applies to your app. What is in it is
  refused here as well as by the collector — see [OBLIGATIONS.md](https://github.com/vapolia/analytics-clients/blob/main/clients/OBLIGATIONS.md).

## Tests

```bash
cd clients/go && go test ./...
```

## Versioning

There is no registry: the module proxy reads the repository, so a release is a tag and there is no
workflow. The module lives in a subdirectory, so Go requires the tag to carry that path — with the
module path of `go.mod` as it stands:

```bash
git tag clients/go/v1.0.1 && git push origin clients/go/v1.0.1
```

A pre-release is the same thing with a suffix. `@latest` skips pre-releases, so a consumer names it:

```bash
go get github.com/vapolia/analytics-clients/clients/go@v1.1.0-beta.1
```

The repository is public, which the module proxy requires: a tag is distributed as soon as it is
pushed, with no gate.
