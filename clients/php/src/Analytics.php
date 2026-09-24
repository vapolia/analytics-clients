<?php

declare(strict_types=1);

namespace Vapolia\Analytics;

/**
 * Server-side client for the Vapolia audience-measurement collector, for server-rendered PHP sites.
 * PHP 7.4+, curl, no dependency: `require_once` this file, or load it through Composer.
 *
 * The visitor is identified by a first-party HttpOnly cookie that this server sets. Events are
 * queued during the request and sent once it ends, after the response has gone to the browser.
 */
class Analytics
{
    public const VERSION = '1.0.0';

    // Limits mirrored from the collector, which applies them again on arrival.
    public const MAX_EVENTS_PER_BATCH = 100;
    public const MAX_PROPS_PER_EVENT = 12;
    public const MAX_CONTEXT_KEYS = 12;
    public const MAX_VALUE_LENGTH = 64;

    private const DAY_MS = 86400000;

    /**
     * The default answer for `requiresPriorConsent`: the EEA outside France, Italy, Spain and the
     * Netherlands, plus the United Kingdom. Same list as the other clients, from OBLIGATIONS.md.
     */
    public const PRIOR_CONSENT_COUNTRIES = [
        'AT', 'BE', 'BG', 'CY', 'CZ', 'DE', 'DK', 'EE', 'FI', 'GB', 'GR', 'HR', 'HU',
        'IE', 'IS', 'LI', 'LT', 'LU', 'LV', 'MT', 'NO', 'PL', 'PT', 'RO', 'SE', 'SI', 'SK',
    ];

    /** @var array<string, mixed> */
    private array $options;
    private bool $measuring;

    /** @var array<int, array{installId: string, country: ?string, context: array, event: array}> */
    private array $queue = [];
    private ?string $installId = null;
    private ?bool $answer = null;
    private bool $shutdownRegistered = false;
    private bool $warnedNoRequest = false;

    /** @var array<string, int> */
    private array $stats = ['accepted' => 0, 'rejected' => 0, 'dropped' => 0, 'sent' => 0, 'requests' => 0];

    /**
     * ```php
     * $analytics = new Analytics(['ingestionUrl' => 'https://analytics.example.com/mysite']);
     * $analytics->track('page_view', ['page' => 'home']);
     * ```
     *
     * @param array<string, mixed> $options See README.md for the full list.
     */
    public function __construct(array $options)
    {
        $advanced = $options['advanced'] ?? [];
        $web = $options['web'] ?? [];

        $this->options = [
            'ingestionUrl' => rtrim((string)($options['ingestionUrl'] ?? ''), '/'),
            'token' => (string)($options['token'] ?? ''),
            'isDebugBuild' => (bool)($options['isDebugBuild'] ?? false),
            'requiresPriorConsent' => $options['requiresPriorConsent'] ?? null,
            'excludedCountries' => array_map('strtoupper', $options['excludedCountries'] ?? []),
            'context' => $options['context'] ?? null,
            'batchSize' => min((int)($advanced['batchSize'] ?? 100), self::MAX_EVENTS_PER_BATCH),
            'queueCapacity' => (int)($advanced['queueCapacity'] ?? 4000),
            'maxAttempts' => max(1, (int)($advanced['maxAttempts'] ?? 3)),
            'requestTimeoutMs' => (int)($advanced['requestTimeoutMs'] ?? 10000),
            'installIdLifetimeMs' => (int)($advanced['installIdLifetimeMs'] ?? 390 * self::DAY_MS),
            'optOutLifetimeMs' => (int)($advanced['optOutLifetimeMs'] ?? 390 * self::DAY_MS),
            'logger' => $advanced['logger'] ?? null,
            'onError' => $advanced['onError'] ?? null,
            'sendOnShutdown' => (bool)($advanced['sendOnShutdown'] ?? true),
            'cookieName' => (string)($web['cookieName'] ?? '_vau'),
            'optOutCookieName' => (string)($web['optOutCookieName'] ?? '_vau_off'),
            'requiresPriorConsentForLocale' => $web['requiresPriorConsentForLocale'] ?? null,
            'timeZoneOffset' => $web['timeZoneOffset'] ?? null,
        ];

        $this->measuring = !$this->options['isDebugBuild']
            && preg_match('#^https?://[^/]+/.+#i', $this->options['ingestionUrl']) === 1;

        if (!$this->options['isDebugBuild'] && !$this->measuring)
            $this->log('error', 'ingestionUrl is required and must be an absolute URL ending with the source name. Nothing is measured.');
    }

    /**
     * Queues one event for the current visitor. Never throws and never blocks: the send happens
     * when the request ends, or on `flush()`. `$name` and every key of `$props` must be on the
     * source's whitelist.
     *
     * @param array<string, string|int|float|bool> $props
     */
    public function track(string $name, array $props = []): void
    {
        if (!$this->measuring)
            return;

        try {
            $installId = $this->getInstallId();
            if ($installId === null)
                return;

            $country = $this->getCountry();
            if ($country !== null && in_array($country, $this->options['excludedCountries'], true))
                return;

            $cleanName = self::text($name);
            if ($cleanName === null) {
                $this->stats['rejected']++;
                return;
            }

            if (count($this->queue) >= $this->options['queueCapacity']) {
                $this->stats['dropped']++;
                $this->reportLoss(null, 'queue full', true);
                return;
            }

            $event = ['name' => $cleanName, 'ts' => self::now()];
            $cleanProps = self::props($props, self::MAX_PROPS_PER_EVENT);
            if ($cleanProps !== [])
                $event['props'] = $cleanProps;
            $tz = $this->timeZoneOffset();
            if ($tz !== null)
                $event['tz'] = $tz;

            $context = is_callable($this->options['context']) ? ($this->options['context'])() : null;
            $this->queue[] = [
                'installId' => $installId,
                'country' => $country,
                'context' => self::props(is_array($context) ? $context : [], self::MAX_CONTEXT_KEYS),
                'event' => $event,
            ];
            $this->stats['accepted']++;
            $this->registerShutdown();
        } catch (\Throwable $e) {
            $this->reportLoss($e, 'track failed', true);
        }
    }

    /** Sends what is queued, now. Called on its own when the request ends. */
    public function flush(): void
    {
        if (!$this->measuring || $this->queue === [])
            return;

        $pending = $this->queue;
        $this->queue = [];

        foreach (self::batches($pending, $this->options['batchSize']) as [$body, $count])
            $this->send($body, $count);
    }

    /**
     * Whether this visitor has opposed the measurement. Before any answer it follows
     * `web.requiresPriorConsentForLocale`, then `requiresPriorConsent`, then the regime of the
     * `Accept-Language` tag.
     */
    public function isOptedOut(): bool
    {
        $answer = $this->consentAnswer();
        if ($answer !== null)
            return !$answer;

        $locale = $this->preferredLanguage();
        if (is_callable($this->options['requiresPriorConsentForLocale']))
            return (bool)($this->options['requiresPriorConsentForLocale'])($locale);

        return $this->options['requiresPriorConsent'] ?? self::localeRequiresPriorConsent($locale);
    }

    /** True accepted, false refused, null not answered yet: whether to show the consent banner. */
    public function consentAnswer(): ?bool
    {
        if ($this->answer !== null)
            return $this->answer;

        $cookie = $this->readCookie($this->options['optOutCookieName']);
        if ($cookie === '1')
            return false;
        if ($cookie === '0')
            return true;
        return null;
    }

    /**
     * The right of opposition, and the consent banner's two buttons. Call it before any output:
     * the answer is stored in a cookie. Opposing drops this visitor's queued events and deletes the
     * identity cookie.
     */
    public function setOptedOut(bool $value): void
    {
        $this->answer = !$value;

        if ($value) {
            $installId = $this->installId ?? self::installId($this->readCookie($this->options['cookieName']));
            $this->installId = null;
            if ($installId !== null)
                $this->queue = array_values(array_filter($this->queue, fn($item) => $item['installId'] !== $installId));
        }

        if ($this->headersSent()) {
            $this->log('warning', 'The opposition cookie cannot be written: output has already started. Call setOptedOut before any output.');
            return;
        }

        $expires = time() + intdiv($this->options['optOutLifetimeMs'], 1000);
        // "0", not a deletion: an acceptance has to outrank requiresPriorConsent on the next request.
        $this->writeCookie($this->options['optOutCookieName'], $value ? '1' : '0', $expires);
        if ($value)
            $this->writeCookie($this->options['cookieName'], '', 1);
    }

    /**
     * The visitor's id, from the `_vau` cookie, created when missing and the output has not started.
     * Null when opposed, not answered under a prior-consent regime, or outside an HTTP request.
     */
    public function getInstallId(): ?string
    {
        if ($this->installId !== null)
            return $this->installId;

        if (!$this->isHttpRequest()) {
            if (!$this->warnedNoRequest) {
                $this->warnedNoRequest = true;
                $this->log('warning', 'No HTTP request: only web requests are measured.');
            }
            return null;
        }

        // Checked first: an opposed visitor gets no identifier written at all.
        if ($this->isOptedOut())
            return null;

        $fromCookie = self::installId($this->readCookie($this->options['cookieName']));
        if ($fromCookie !== null)
            return $this->installId = $fromCookie;

        // A cookie can only be set before the output starts. The visitor is then measured on the
        // next request.
        if ($this->headersSent())
            return null;

        $issued = self::uuid();
        // Absolute, from this moment. Never extended on a later visit: the rotation is the browser
        // dropping the cookie.
        $this->writeCookie($this->options['cookieName'], $issued, time() + intdiv($this->options['installIdLifetimeMs'], 1000));
        return $this->installId = $issued;
    }

    /**
     * The visitor's country, the region of the preferred `Accept-Language` tag. Never a geolocation
     * of the IP. Null when the tag carries no region.
     */
    public function getCountry(): ?string
    {
        return self::splitBcp47($this->preferredLanguage())['region'];
    }

    /** @return array<string, int> `accepted`, `rejected`, `dropped`, `sent`, `requests`. */
    public function getStats(): array
    {
        return $this->stats;
    }

    /**
     * Whether `$locale`, a BCP-47 tag such as `fr-FR`, asks for consent before anything is stored.
     * True when the tag carries no region. `fr-CA` answers true and `en-CA` false, for Quebec.
     */
    public static function localeRequiresPriorConsent(?string $locale): bool
    {
        $parts = self::splitBcp47($locale);
        if ($parts['region'] === null)
            return true;
        if (in_array($parts['region'], self::PRIOR_CONSENT_COUNTRIES, true))
            return true;
        return $parts['region'] === 'CA' && strtolower((string)$parts['language']) === 'fr';
    }

    /**
     * The language and region subtags of a BCP-47 tag, null when absent. The region is the first
     * two-letter subtag after the language, which skips a script (`zh-Hant-TW`) and a UN M.49 code.
     *
     * @return array{language: ?string, region: ?string}
     */
    public static function splitBcp47(?string $locale): array
    {
        $parts = array_values(array_filter(explode('-', str_replace('_', '-', trim((string)$locale))), fn($p) => $p !== ''));
        if ($parts === [])
            return ['language' => null, 'region' => null];

        $length = strlen($parts[0]);
        $language = $length >= 2 && $length <= 3 ? $parts[0] : null;
        $region = null;
        foreach (array_slice($parts, 1) as $part) {
            if (preg_match('/^[A-Za-z]{2}$/', $part) === 1) {
                $region = strtoupper($part);
                break;
            }
        }

        return ['language' => $language, 'region' => $region];
    }

    // ---- The request: overridable so the tests run without a web server.

    protected function readCookie(string $name): ?string
    {
        $value = $_COOKIE[$name] ?? null;
        return is_string($value) ? $value : null;
    }

    protected function writeCookie(string $name, string $value, int $expires): void
    {
        setcookie($name, $value, [
            'expires' => $expires,
            'path' => '/',
            'secure' => true,
            'httponly' => true,
            'samesite' => 'Lax',
        ]);
    }

    protected function headersSent(): bool
    {
        return headers_sent();
    }

    protected function isHttpRequest(): bool
    {
        return PHP_SAPI !== 'cli' && PHP_SAPI !== 'phpdbg';
    }

    protected function acceptLanguage(): ?string
    {
        $value = $_SERVER['HTTP_ACCEPT_LANGUAGE'] ?? null;
        return is_string($value) ? $value : null;
    }

    /**
     * One POST. Returns the HTTP status (0 on a network error) and the `Retry-After` header in
     * seconds, null when absent.
     *
     * @return array{0: int, 1: ?int}
     */
    protected function post(string $body): array
    {
        $headers = ['Content-Type: application/json', 'User-Agent: vapolia-analytics-php/' . self::VERSION];
        if ($this->options['token'] !== '')
            $headers[] = 'Authorization: Bearer ' . $this->options['token'];

        $retryAfter = null;
        $curl = curl_init($this->options['ingestionUrl']);
        curl_setopt_array($curl, [
            CURLOPT_POST => true,
            CURLOPT_POSTFIELDS => $body,
            CURLOPT_HTTPHEADER => $headers,
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_TIMEOUT_MS => $this->options['requestTimeoutMs'],
            CURLOPT_CONNECTTIMEOUT_MS => min(3000, $this->options['requestTimeoutMs']),
            CURLOPT_HEADERFUNCTION => function ($curl, string $line) use (&$retryAfter): int {
                if (stripos($line, 'Retry-After:') === 0) {
                    $seconds = trim(substr($line, 12));
                    if (ctype_digit($seconds))
                        $retryAfter = (int)$seconds;
                }
                return strlen($line);
            },
        ]);

        $ok = curl_exec($curl);
        $status = $ok === false ? 0 : (int)curl_getinfo($curl, CURLINFO_RESPONSE_CODE);
        curl_close($curl);

        return [$status, $retryAfter];
    }

    /** Pauses between two attempts. Overridable so the tests do not wait. */
    protected function sleepMs(int $ms): void
    {
        usleep($ms * 1000);
    }

    // ---- Sending.

    private function send(string $body, int $count): void
    {
        for ($attempt = 1; ; $attempt++) {
            $this->stats['requests']++;
            [$status, $retryAfter] = $this->post($body);

            if ($status >= 200 && $status < 300) {
                $this->stats['sent'] += $count;
                return;
            }

            // 404 means this source is not configured there, 401 a revoked token, 400 a refused payload.
            $retryable = $status === 0 || $status === 429 || $status >= 500;
            $reason = $status === 0 ? 'network error' : "collector answered $status";
            if (!$retryable || $attempt >= $this->options['maxAttempts']) {
                $this->stats['dropped'] += $count;
                $this->reportLoss(null, $reason, !$retryable);
                return;
            }

            // A server drops rather than keeps: a long Retry-After is not worth holding a worker for.
            $waitMs = $retryAfter !== null ? $retryAfter * 1000 : 500 * $attempt;
            if ($waitMs > 5000) {
                $this->stats['dropped'] += $count;
                $this->reportLoss(null, "$reason, retry after {$waitMs} ms", false);
                return;
            }
            $this->sleepMs($waitMs);
        }
    }

    private function registerShutdown(): void
    {
        if ($this->shutdownRegistered || !$this->options['sendOnShutdown'])
            return;

        $this->shutdownRegistered = true;
        register_shutdown_function(function (): void {
            // The browser gets its response first: the send then costs the visitor nothing.
            if (function_exists('fastcgi_finish_request'))
                fastcgi_finish_request();
            $this->flush();
        });
    }

    /**
     * The request bodies, with their event count: one per install id, country and context, at most
     * `$batchSize` events each.
     *
     * @return array<int, array{0: string, 1: int}>
     */
    private static function batches(array $pending, int $batchSize): array
    {
        $groups = [];
        foreach ($pending as $item) {
            $key = json_encode([$item['installId'], $item['country'], $item['context']]);
            $groups[$key][] = $item;
        }

        $bodies = [];
        foreach ($groups as $items) {
            foreach (array_chunk($items, max(1, $batchSize)) as $chunk) {
                $first = $chunk[0];
                $batch = ['installId' => $first['installId']];
                if ($first['country'] !== null)
                    $batch['country'] = $first['country'];
                if ($first['context'] !== [])
                    $batch['context'] = $first['context'];
                $batch['events'] = array_map(fn($item) => $item['event'], $chunk);

                $body = json_encode($batch, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE | JSON_PRESERVE_ZERO_FRACTION);
                $bodies[] = [$body, count($chunk)];
            }
        }

        return $bodies;
    }

    // ---- Cleaning, mirrored from the collector's ceilings.

    /**
     * Trimmed, stripped of control characters, cut at 64 UTF-16 units without splitting a
     * character, null when blank or not valid UTF-8.
     */
    public static function text($value, int $maxLength = self::MAX_VALUE_LENGTH): ?string
    {
        if (!is_string($value) || preg_match('//u', $value) !== 1)
            return null;

        $cleaned = preg_replace('/[\x{0}-\x{1F}\x{7F}-\x{9F}]/u', '', $value);
        $cleaned = preg_replace('/^\s+|\s+$/u', '', (string)$cleaned);

        $kept = '';
        $units = 0;
        foreach (preg_split('//u', (string)$cleaned, -1, PREG_SPLIT_NO_EMPTY) as $char) {
            $size = strlen($char) === 4 ? 2 : 1;
            if ($units + $size > $maxLength)
                break;
            $kept .= $char;
            $units += $size;
        }

        $kept = preg_replace('/\s+$/u', '', $kept);
        return $kept === '' || $kept === null ? null : $kept;
    }

    /**
     * Scalars only, sorted by key and capped in count: what survives the cap does not depend on the
     * insertion order.
     *
     * @return array<string, string|int|float|bool>
     */
    public static function props(array $values, int $maxKeys): array
    {
        ksort($values, SORT_STRING);

        $kept = [];
        foreach ($values as $key => $value) {
            if (count($kept) === $maxKeys)
                break;

            $cleanKey = self::text((string)$key);
            if ($cleanKey === null)
                continue;

            if (is_string($value)) {
                $cleaned = self::text($value);
                if ($cleaned !== null)
                    $kept[$cleanKey] = $cleaned;
            } elseif (is_bool($value) || is_int($value)) {
                $kept[$cleanKey] = $value;
            } elseif (is_float($value) && is_finite($value)) {
                $kept[$cleanKey] = $value;
            }
        }

        return $kept;
    }

    /** The canonical lowercase UUID, never the nil one, or null. */
    public static function installId(?string $value): ?string
    {
        $id = strtolower(trim((string)$value));
        if (preg_match('/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/', $id) !== 1)
            return null;
        if (trim($id, '0-') === '')
            return null;
        return $id;
    }

    private static function uuid(): string
    {
        $bytes = random_bytes(16);
        $bytes[6] = chr((ord($bytes[6]) & 0x0f) | 0x40);
        $bytes[8] = chr((ord($bytes[8]) & 0x3f) | 0x80);
        return vsprintf('%s%s-%s-%s-%s-%s%s%s', str_split(bin2hex($bytes), 4));
    }

    private static function now(): string
    {
        $time = microtime(true);
        $seconds = (int)floor($time);
        return gmdate('Y-m-d\TH:i:s', $seconds) . sprintf('.%03dZ', (int)(($time - $seconds) * 1000));
    }

    private function preferredLanguage(): ?string
    {
        $header = $this->acceptLanguage();
        if ($header === null || trim($header) === '')
            return null;

        // "fr-FR,fr;q=0.9,en;q=0.8": the first tag is the preferred one.
        $first = trim(explode(';', explode(',', $header)[0])[0]);
        return $first === '' ? null : $first;
    }

    private function timeZoneOffset(): ?int
    {
        if (!is_callable($this->options['timeZoneOffset']))
            return null;

        $minutes = ($this->options['timeZoneOffset'])();
        return is_int($minutes) && abs($minutes) <= 14 * 60 ? $minutes : null;
    }

    private function reportLoss(?\Throwable $error, string $reason, bool $permanent): void
    {
        $this->log('warning', "events lost: $reason");
        if (is_callable($this->options['onError'])) {
            try {
                ($this->options['onError'])($error, $reason, $permanent);
            } catch (\Throwable $ignored) {
                // The caller's callback must not break the page.
            }
        }
    }

    private function log(string $level, string $message): void
    {
        if (is_callable($this->options['logger']))
            ($this->options['logger'])($level, $message);
        else
            error_log("[analytics] $level: $message");
    }
}
