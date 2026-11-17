<?php

declare(strict_types=1);

// Plain assertions, no PHPUnit: `php tests/run.php` runs on any PHP 7.4+ without Composer.

require_once __DIR__ . '/../src/Analytics.php';

use Vapolia\Analytics\Analytics;

/** The request and the collector, in memory. */
final class TestAnalytics extends Analytics
{
    public array $cookies = [];
    /** @var array<string, array{0: string, 1: int, 2: ?string}> last write per cookie: value, expiry, domain */
    public array $written = [];
    public ?string $language = 'fr-FR,fr;q=0.9';
    public bool $sent = false;
    public array $bodies = [];
    /** @var array<int, array{0: int, 1: ?int}> answers served in order, then 204 */
    public array $answers = [];
    public array $slept = [];
    public array $addresses = [];
    public ?string $remote = '203.0.113.5';

    protected function readCookie(string $name): ?string { return $this->cookies[$name] ?? null; }
    protected function writeCookie(string $name, string $value, int $expires, ?string $domain = null): void
    {
        $this->written[$name] = [$value, $expires, $domain];
        if ($expires > time()) $this->cookies[$name] = $value; else unset($this->cookies[$name]);
    }
    protected function headersSent(): bool { return $this->sent; }
    protected function isHttpRequest(): bool { return true; }
    protected function acceptLanguage(): ?string { return $this->language; }
    protected function remoteAddress(): ?string { return $this->remote; }
    protected function post(string $body, ?string $clientAddress = null): array { $this->bodies[] = json_decode($body, true); $this->addresses[] = $clientAddress; return array_shift($this->answers) ?? [204, null]; }
    protected function sleepMs(int $ms): void { $this->slept[] = $ms; }
}

function client(array $options = []): TestAnalytics
{
    return new TestAnalytics($options + [
        'ingestionUrl' => 'https://analytics.example.com/testsource',
        'advanced' => ['sendOnShutdown' => false, 'logger' => function () {}],
    ]);
}

$failures = 0;
function test(string $name, callable $body): void
{
    global $failures;
    try {
        $body();
        echo "ok   $name\n";
    } catch (\Throwable $e) {
        $failures++;
        echo "FAIL $name: {$e->getMessage()} (line {$e->getLine()})\n";
    }
}
function same($expected, $actual): void
{
    if ($expected !== $actual)
        throw new \RuntimeException('expected ' . var_export($expected, true) . ', got ' . var_export($actual, true));
}

test('sends one batch with the payload the collector expects', function () {
    $a = client(['context' => fn() => ['logged_in' => true]]);
    $a->track('page_view', ['page' => 'home', 'n' => 3]);
    $a->track('share');
    $a->flush();

    same(1, count($a->bodies));
    $body = $a->bodies[0];
    same(['installId', 'country', 'context', 'events'], array_keys($body));
    same($a->getInstallId(), $body['installId']);
    same('FR', $body['country']);
    same(['logged_in' => true], $body['context']);
    same('page_view', $body['events'][0]['name']);
    same(['n' => 3, 'page' => 'home'], $body['events'][0]['props']);
    same(false, isset($body['events'][1]['props']));
    same(1, preg_match('/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$/', $body['events'][0]['ts']));
    same(['accepted' => 2, 'rejected' => 0, 'dropped' => 0, 'sent' => 2, 'requests' => 1], $a->getStats());
});

test('writes the identity cookie once and reuses it', function () {
    $a = client();
    $id = $a->getInstallId();
    same($id, Analytics::installId($id));
    same($id, $a->cookies['__Host-vau']);

    $b = client();
    $b->cookies = $a->cookies;
    same($id, $b->getInstallId());
    same(false, isset($b->written['__Host-vau']));
});

test('measures nothing once the output has started and no cookie exists', function () {
    $a = client();
    $a->sent = true;
    $a->track('page_view');
    $a->flush();
    same([], $a->bodies);
});

test('writes nothing before the answer under a prior-consent regime', function () {
    $a = client();
    $a->language = 'de-DE,de;q=0.9';
    same(null, $a->consentAnswer());
    same(true, $a->isOptedOut());
    $a->track('page_view');
    $a->flush();
    same([], $a->bodies);
    same([], $a->written);
});

test('an acceptance outranks the regime, in this request and the next', function () {
    $a = client();
    $a->language = 'de-DE';
    $a->setOptedOut(false);
    same(true, $a->consentAnswer());
    same('0', $a->cookies['__Host-vau_off']);
    $a->track('page_view');
    $a->flush();
    same(1, count($a->bodies));

    $b = client();
    $b->language = 'de-DE';
    $b->cookies = $a->cookies;
    same(false, $b->isOptedOut());
    same($a->getInstallId(), $b->getInstallId());
});

test('opposing drops the queue and deletes the identity cookie', function () {
    $a = client();
    $a->track('page_view');
    $a->setOptedOut(true);
    same(false, $a->consentAnswer());
    same('1', $a->cookies['__Host-vau_off']);
    same(false, isset($a->cookies['__Host-vau']));
    $a->track('page_view');
    $a->flush();
    same([], $a->bodies);
    same(null, $a->getInstallId());
});

test('default cookie names carry the __Host- prefix and no domain', function () {
    $a = client();
    $a->getInstallId();
    $a->setOptedOut(false);
    same(['__Host-vau', '__Host-vau_off'], array_keys($a->written));
    same(null, $a->written['__Host-vau'][2]);
    same(null, $a->written['__Host-vau_off'][2]);
});

test('cookieDomain switches the default names to __Secure- and sets the domain', function () {
    $a = client(['web' => ['cookieDomain' => 'example.com']]);
    $a->getInstallId();
    $a->setOptedOut(false);
    same(['__Secure-vau', '__Secure-vau_off'], array_keys($a->written));
    same('example.com', $a->written['__Secure-vau'][2]);
    same('example.com', $a->written['__Secure-vau_off'][2]);
});

test('an explicit cookie name is kept as is, and still gets the domain', function () {
    $a = client(['web' => ['cookieName' => 'mine', 'optOutCookieName' => 'mine_off', 'cookieDomain' => 'example.com']]);
    $a->getInstallId();
    $a->setOptedOut(false);
    same(['mine', 'mine_off'], array_keys($a->written));
    same('example.com', $a->written['mine'][2]);
    same('example.com', $a->written['mine_off'][2]);

    $b = client(['web' => ['cookieName' => 'mine']]);
    $b->getInstallId();
    same(['mine'], array_keys($b->written));
    same(null, $b->written['mine'][2]);
});

test('migrates a legacy consent answer to the new name', function () {
    foreach (['0' => true, '1' => false] as $value => $answer) {
        $a = client(['web' => ['cookieDomain' => 'example.com']]);
        $a->cookies['_vau_off'] = (string)$value;
        same($answer, $a->consentAnswer());
        same($answer, $a->consentAnswer());
        same((string)$value, $a->cookies['__Secure-vau_off']);
        same('example.com', $a->written['__Secure-vau_off'][2]);
        same(true, $a->written['__Secure-vau_off'][1] > time() + 389 * 86400);
        same(false, isset($a->cookies['_vau_off']));
        same(['', 1, null], $a->written['_vau_off']);
    }
});

test('migrates a legacy install id: same id, new cookie, legacy deleted', function () {
    $legacy = '11111111-0000-4000-8000-000011111111';
    $a = client();
    $a->cookies['_vau'] = $legacy;
    same($legacy, $a->getInstallId());
    same($legacy, $a->cookies['__Host-vau']);
    same(true, $a->written['__Host-vau'][1] > time() + 389 * 86400);
    same(null, $a->written['__Host-vau'][2]);
    same(false, isset($a->cookies['_vau']));
    same(['', 1, null], $a->written['_vau']);
});

test('uses the legacy values without rewriting once the output has started', function () {
    $legacy = '11111111-0000-4000-8000-000011111111';
    $a = client();
    $a->sent = true;
    $a->cookies['_vau'] = $legacy;
    $a->cookies['_vau_off'] = '0';
    same(true, $a->consentAnswer());
    same($legacy, $a->getInstallId());
    same([], $a->written);
    same('0', $a->cookies['_vau_off']);
});

test('does not migrate when the effective name is the legacy one', function () {
    $a = client(['web' => ['cookieName' => '_vau', 'optOutCookieName' => '_vau_off']]);
    $a->cookies['_vau_off'] = '1';
    same(false, $a->consentAnswer());
    same([], $a->written);
});

test('a new cookie outranks the legacy one', function () {
    $a = client();
    $a->cookies['__Host-vau_off'] = '0';
    $a->cookies['_vau_off'] = '1';
    same(true, $a->consentAnswer());
    same([], $a->written);
});

test('opposing deletes the identity cookie under the new and the legacy name', function () {
    $a = client(['web' => ['cookieDomain' => 'example.com']]);
    $a->cookies['__Secure-vau'] = '11111111-0000-4000-8000-000011111111';
    $a->cookies['_vau'] = '22222222-0000-4000-8000-000022222222';
    $a->setOptedOut(true);
    same(['', 1, 'example.com'], $a->written['__Secure-vau']);
    same(['', 1, null], $a->written['_vau']);
    same(false, isset($a->cookies['__Secure-vau']));
    same(false, isset($a->cookies['_vau']));
    same('1', $a->cookies['__Secure-vau_off']);
});

test('requiresPriorConsentForLocale wins over requiresPriorConsent', function () {
    $a = client(['requiresPriorConsent' => false, 'web' => ['requiresPriorConsentForLocale' => fn($l) => $l === 'fr-FR']]);
    same(true, $a->isOptedOut());
    $b = client(['requiresPriorConsent' => true]);
    same(true, $b->isOptedOut());
    $c = client(['requiresPriorConsent' => false]);
    $c->language = 'de-DE';
    same(false, $c->isOptedOut());
});

test('refuses an excluded country', function () {
    $a = client(['excludedCountries' => ['fr']]);
    $a->track('page_view');
    $a->flush();
    same([], $a->bodies);
});

test('splits batches per context and per batch size', function () {
    $plan = 'free';
    $a = client(['context' => function () use (&$plan) { return ['plan' => $plan]; }, 'advanced' => ['batchSize' => 2, 'sendOnShutdown' => false]]);
    $a->track('a'); $a->track('b'); $a->track('c');
    $plan = 'paid';
    $a->track('d');
    $a->flush();
    same([2, 1, 1], array_map(fn($b) => count($b['events']), $a->bodies));
    same('paid', $a->bodies[2]['context']['plan']);
});

test('retries a 5xx, honours Retry-After, then succeeds', function () {
    $a = client();
    $a->answers = [[503, null], [429, 2]];
    $a->track('page_view');
    $a->flush();
    same(3, count($a->bodies));
    same([500, 2000], $a->slept);
    same(1, $a->getStats()['sent']);
});

test('drops at once on a 4xx, and after maxAttempts on a 5xx', function () {
    $errors = [];
    $a = client(['advanced' => ['sendOnShutdown' => false, 'logger' => function () {}, 'onError' => function ($e, $reason, $permanent) use (&$errors) { $errors[] = $permanent; }]]);
    $a->answers = [[404, null]];
    $a->track('page_view');
    $a->flush();
    same(1, count($a->bodies));

    $a->answers = [[500, null], [500, null], [500, null]];
    $a->track('page_view');
    $a->flush();
    same(4, count($a->bodies));
    same([true, false], $errors);
    same(2, $a->getStats()['dropped']);
});

test('isDebugBuild measures nothing', function () {
    $a = client(['isDebugBuild' => true]);
    $a->track('page_view');
    $a->flush();
    same([], $a->bodies);
    same([], $a->written);
});

test('cleans names, keys and values like the collector', function () {
    same('abc', Analytics::text("  a\x00b\u{85}c  "));
    same(null, Analytics::text("   "));
    same(null, Analytics::text("\xff"));
    same(64, strlen(Analytics::text(str_repeat('x', 100))));
    // 63 units then an emoji of 2 units: the emoji does not fit and is not split.
    same(str_repeat('x', 63), Analytics::text(str_repeat('x', 63) . "\u{1F600}"));
    same(['a' => 1.5, 'b' => true], Analytics::props(['b' => true, 'a' => 1.5, 'c' => NAN, 'd' => [1], 'e' => null], 12));
    same(12, count(Analytics::props(array_fill_keys(range('a', 'z'), 1), 12)));
});

test('reads the prior-consent regime off the locale', function () {
    same(true, Analytics::localeRequiresPriorConsent('de-DE'));
    same(false, Analytics::localeRequiresPriorConsent('fr-FR'));
    same(true, Analytics::localeRequiresPriorConsent('fr-CA'));
    same(false, Analytics::localeRequiresPriorConsent('en-CA'));
    same(true, Analytics::localeRequiresPriorConsent(null));
    same(true, Analytics::localeRequiresPriorConsent('es-419'));
    same(false, Analytics::localeRequiresPriorConsent('zh-Hant-TW'));
    same(true, Analytics::localeRequiresPriorConsent('de_AT'));
});

test('rejects malformed and nil install ids', function () {
    same(null, Analytics::installId('00000000-0000-0000-0000-000000000000'));
    same(null, Analytics::installId('not-a-uuid'));
    same('11111111-0000-4000-8000-000011111111', Analytics::installId(' 11111111-0000-4000-8000-000011111111 '));
});

test('adds the visitor time zone when the site supplies it', function () {
    $a = client(['web' => ['timeZoneOffset' => fn() => 120]]);
    $a->track('page_view');
    $a->flush();
    same(120, $a->bodies[0]['events'][0]['tz']);
});

test('sends the visitor address from REMOTE_ADDR, or from web.clientAddress', function () {
    $a = client(['requiresPriorConsent' => false]);
    $a->track('page_view');
    $a->flush();
    same(['203.0.113.5'], $a->addresses);

    $b = client(['requiresPriorConsent' => false, 'web' => ['clientAddress' => fn() => '::FFFF:198.51.100.4']]);
    $b->track('page_view');
    $b->flush();
    same(['198.51.100.4'], $b->addresses);
});

test('drops an invalid address', function () {
    same(null, Analytics::ipAddress('not-an-ip'));
    same(null, Analytics::ipAddress('10.0.0.1, 10.0.0.2'));
    same('2001:db8::1', Analytics::ipAddress(' 2001:DB8::1%eth0 '));
});

echo $failures === 0 ? "\nall passed\n" : "\n$failures failed\n";
exit($failures === 0 ? 0 : 1);
