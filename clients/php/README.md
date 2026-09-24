# PHP client

This document is for developers who measure a server-rendered PHP site with the collector (`POST {endpoint}/{source}`).
The rules every client follows are in the [contract](https://github.com/vapolia/analytics-clients/blob/main/clients/README.md).

## Summary

```php
require_once __DIR__ . '/Analytics.php';   // or Composer: vapolia/analytics

$analytics = new \Vapolia\Analytics\Analytics([
    'ingestionUrl' => 'https://analytics.example.com/<sourceName>',
    'token' => getenv('ANALYTICS_SERVER_TOKEN'),
]);

// on each page, before any output
$analytics->track('page_view', ['page' => 'home']);

// the consent banner, while consentAnswer() is null, and the settings switch
$analytics->setOptedOut(false);   // accept
$analytics->setOptedOut(true);    // refuse
```

1. Create one `Analytics` per request, before any output.
2. Call `track` from the controllers. The events leave once the request ends, after the response has gone to the browser.
3. Show the consent banner while `consentAnswer()` is null, and call `setOptedOut` from its buttons.
4. Declare every event name, property key and context key on the collector's source.

PHP 7.4+ with the `curl` extension. No dependency, no Composer required: `src/Analytics.php` is the whole client.

## What is stored in the browser

The events are emitted by the server. No script is added to the pages.

| | |
|---|---|
| **Install id** | A first-party cookie (`_vau`, HttpOnly, Secure, SameSite=Lax) that the server sets once. It expires after 13 months and is never extended. |
| **Country** | The region of the first `Accept-Language` tag, never an IP geolocation. |
| **Opposition** | A second cookie, `_vau_off`: `1` opposed, `0` accepted, absent unanswered. |

A cookie can only be written before the output starts. A visitor seen for the first time after the output started is measured from the next request.
A CLI script or a cron job has no visitor and is not measured.

## Consent

One site serves every country, so the regime is decided per visitor from the `Accept-Language` tag.
While the visitor has not answered, `isOptedOut()` reads, in this order:

1. `web.requiresPriorConsentForLocale`, a function of the BCP-47 tag (null when the header has none).
2. `requiresPriorConsent`, a boolean for every visitor.
3. `Analytics::localeRequiresPriorConsent($tag)`, the built-in list from [OBLIGATIONS.md](https://github.com/vapolia/analytics-clients/blob/main/clients/OBLIGATIONS.md).

**The built-in default must not be taken as a legal basis: the choice stays yours, and so does the liability for it.**

Until the visitor accepts under a prior-consent regime, no cookie is written and nothing is sent.
Accepting writes `_vau_off=0`, which outranks the regime on the next requests.
Refusing writes `_vau_off=1`, deletes `_vau` and drops the visitor's queued events.

```php
if ($analytics->consentAnswer() === null)
    $data['showAnalyticsBanner'] = true;

// the banner posts to a controller
public function consent()
{
    $this->analytics->setOptedOut($this->input->post('accept') !== '1');
    redirect($this->input->post('back'));
}
```

A site that already runs a cookie banner (tarteaucitron, for example) calls `setOptedOut` from the request that carries its choice.

## CodeIgniter 3

Load the client once per request, as a library:

```php
// application/libraries/Analytics_client.php
require_once APPPATH . 'third_party/vapolia-analytics/Analytics.php';

class Analytics_client extends \Vapolia\Analytics\Analytics
{
    public function __construct()
    {
        parent::__construct([
            'ingestionUrl' => 'https://analytics.example.com/mysite',
            'token' => getenv('ANALYTICS_SERVER_TOKEN'),
            'isDebugBuild' => ENVIRONMENT !== 'production',
        ]);
    }
}

// in a controller
$this->load->library('analytics_client', null, 'analytics');
$this->analytics->track('page_view', ['page' => 'route_home']);
```

CodeIgniter buffers the output until the controller returns, so the cookie can be written from any controller or view.

## API

| | |
|---|---|
| `new Analytics(array $options)` | One per request. |
| `track(string $name, array $props = [])` | Props are scalars: string (≤64 chars), int, finite float, bool. Max 12 per event. Never throws. |
| `flush()` | Sends what is queued, now. Called on its own when the request ends. |
| `setOptedOut(bool)` / `isOptedOut()` | The right of opposition, stored in `_vau_off`. Call `setOptedOut` before any output. |
| `consentAnswer(): ?bool` | True accepted, false refused, null not answered yet. |
| `getInstallId(): ?string` | The visitor's id, created when missing. Null when opted out or outside a request. |
| `getCountry(): ?string` | The region read from `Accept-Language`. |
| `getStats(): array` | `accepted` / `rejected` / `dropped` / `sent` / `requests`. |
| `Analytics::localeRequiresPriorConsent(?string $tag)` | The default regime for a BCP-47 tag. |

## Options

The names are the ones of the other clients. Durations are in milliseconds.

| Group | Option | Default |
|---|---|---|
| Main | `ingestionUrl` (`https://baseUrl/sourceName`) | required |
| Main | `token` | none |
| Main | `isDebugBuild` | `false`. True measures nothing and writes no cookie. |
| Main | `requiresPriorConsent` | null, from the locale |
| Main | `excludedCountries` | empty |
| Main | `context` | a function returning the batch context, read for every event |
| `advanced` | `batchSize` | 100 |
| `advanced` | `queueCapacity` | 4000 |
| `advanced` | `maxAttempts` | 3 |
| `advanced` | `requestTimeoutMs` | 10 000 |
| `advanced` | `installIdLifetimeMs` | 390 days |
| `advanced` | `optOutLifetimeMs` | 390 days |
| `advanced` | `logger` | `function (string $level, string $message)`, default `error_log` |
| `advanced` | `onError` | `function (?Throwable $error, string $reason, bool $permanent)`, called on every loss |
| `advanced` | `sendOnShutdown` | `true`. False leaves the send to your own `flush()` call. |
| `web` | `cookieName` | `_vau` |
| `web` | `optOutCookieName` | `_vau_off` |
| `web` | `requiresPriorConsentForLocale` | none |
| `web` | `timeZoneOffset` | none. A function returning the visitor's offset in minutes east of UTC, for example from a cookie set by a script. |

A server speaks for every visitor, so this client has no per-client rate window. Pass a server `token`: the collector then throttles per token instead of per IP address.

## Sending

- Events wait in memory for the rest of the request. At shutdown the client calls `fastcgi_finish_request()` when PHP-FPM provides it, then sends. The visitor does not wait for the collector.
- One request per install id, country and context, at most 100 events each.
- **5xx, network error, 429**: retried up to `maxAttempts`, honouring `Retry-After` up to 5 s, then dropped.
- **404 / 401 / other 4xx**: dropped at once.
- Nothing is kept on disk: a collector outage loses the events of the requests it spans.

## Build and test

```bash
php tests/run.php
```

The tests are plain PHP, with no Composer and no PHPUnit. They run on PHP 7.4 and 8.x in CI.
