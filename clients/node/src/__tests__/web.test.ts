import { createServer, type IncomingMessage, type ServerResponse } from 'node:http';
import type { AddressInfo } from 'node:net';
import { afterEach, describe, expect, it } from 'vitest';
import { WebAnalytics, type WebOptions } from '../web/visitor.js';
import { localeRequiresPriorConsent, splitBcp47 } from '../web/consent.js';
import { collector, INSTALL, newClient, OTHER } from './support.js';

function web(options: WebOptions = {}) {
  const col = collector();
  const client = newClient(col.fetcher, { token: 'server-token' });
  return { ...col, client, analytics: new WebAnalytics(client, options) };
}

function request(headers: Record<string, string> = {}, url = 'https://site.example.com/', init: RequestInit = {}) {
  return new Request(url, { ...init, headers });
}

function setCookies(headers: Headers): string[] {
  return headers.getSetCookie();
}

describe('consent regime', () => {
  it('reads the region of a BCP-47 tag', () => {
    expect(splitBcp47('zh-Hant-TW')).toEqual({ language: 'zh', region: 'TW' });
    expect(splitBcp47('es-419')).toEqual({ language: 'es', region: undefined });
  });

  it('asks for consent in Quebec, in the listed countries and without a region', () => {
    expect(localeRequiresPriorConsent('fr-CA')).toBe(true);
    expect(localeRequiresPriorConsent('en-CA')).toBe(false);
    expect(localeRequiresPriorConsent('de-DE')).toBe(true);
    expect(localeRequiresPriorConsent('fr-FR')).toBe(false);
    expect(localeRequiresPriorConsent('fr')).toBe(true);
  });
});

describe('Visitor', () => {
  it('issues the identity cookie once and reuses it', async () => {
    const { analytics, client, requests } = web({ requiresPriorConsent: false });

    const first = new Headers();
    const visitor = analytics.forFetch(request({ 'accept-language': 'fr-FR,fr;q=0.9' }), first);
    visitor.track('page_view');
    visitor.track('page_view');
    expect(setCookies(first)).toHaveLength(1);
    expect(setCookies(first)[0]).toMatch(/^__Host-vau=[0-9a-f-]{36}; Max-Age=33696000; Expires=[^;]+; Path=\/; Secure; HttpOnly; SameSite=Lax$/);

    const id = visitor.installId!;
    const second = new Headers();
    analytics.forFetch(request({ cookie: `__Host-vau=${id}`, 'accept-language': 'fr-FR' }), second).track('page_view');
    expect(setCookies(second)).toHaveLength(0);

    await client.flush();
    expect(requests).toHaveLength(1);
    expect(requests[0]!.body).toMatchObject({ installId: id, country: 'FR' });
  });

  it('measures nothing before an answer under a prior-consent regime', async () => {
    const { analytics, client, requests } = web();
    const headers = new Headers();

    analytics.forFetch(request({ 'accept-language': 'de-DE' }), headers).track('page_view');
    await client.flush();

    expect(requests).toHaveLength(0);
    expect(setCookies(headers)).toHaveLength(0);
  });

  it('lets an acceptance outrank requiresPriorConsent', () => {
    const { analytics } = web({ requiresPriorConsent: true });
    const visitor = analytics.forFetch(request({ cookie: '__Host-vau_off=0' }), new Headers());

    expect(visitor.consentAnswer).toBe(true);
    expect(visitor.isOptedOut).toBe(false);
    expect(visitor.requiresPriorConsent).toBe(true);
  });

  it('drops the buffered events and the identity cookie on opposition', async () => {
    const { analytics, client, requests } = web({ requiresPriorConsent: false });
    const headers = new Headers();
    const visitor = analytics.forFetch(request({ cookie: `__Host-vau=${INSTALL}` }), headers);

    visitor.track('page_view');
    visitor.setOptedOut(true);
    visitor.track('page_view');
    await client.flush();

    expect(requests).toHaveLength(0);
    expect(setCookies(headers)).toEqual([
      expect.stringMatching(/^__Host-vau_off=1; /),
      expect.stringMatching(/^_vau_off=; Max-Age=0; /),
      expect.stringMatching(/^__Host-vau=; Max-Age=0; /),
      expect.stringMatching(/^_vau=; Max-Age=0; /),
    ]);
  });

  it('sends the address the site resolves', async () => {
    const { analytics, client, requests } = web({ requiresPriorConsent: false, clientAddress: () => '203.0.113.9' });

    analytics.forFetch(request({ cookie: `__Host-vau=${INSTALL}` }), new Headers()).track('page_view');
    await client.flush();

    expect(requests[0]!.headers.get('X-Analytics-Client-Address')).toBe('203.0.113.9');
  });

  it('mixes two visitors in one flush into two requests, each with its own address', async () => {
    const other = '33333333-0000-0000-0000-000033333333';
    const addresses: Record<string, string> = { [INSTALL]: '203.0.113.1', [other]: '203.0.113.2' };
    const { analytics, client, requests } = web({
      requiresPriorConsent: false,
      clientAddress: (r) => addresses[(r as Request).headers.get('cookie')!.slice(11)],
    });

    analytics.forFetch(request({ cookie: `__Host-vau=${INSTALL}` }), new Headers()).track('page_view');
    analytics.forFetch(request({ cookie: `__Host-vau=${other}` }), new Headers()).track('page_view');
    await client.flush();

    expect(requests.map((r) => [r.body.installId, r.headers.get('X-Analytics-Client-Address')])).toEqual([
      [INSTALL, '203.0.113.1'],
      [other, '203.0.113.2'],
    ]);
  });
});

describe('cookie names and domain', () => {
  it('uses __Host- names without Domain by default', () => {
    const { analytics } = web({ requiresPriorConsent: false });
    const headers = new Headers();
    const visitor = analytics.forFetch(request(), headers);
    visitor.track('page_view');
    visitor.setOptedOut(false);

    const cookies = setCookies(headers).filter((c) => c.startsWith('__Host-'));
    expect(cookies).toHaveLength(2);
    expect(cookies.some((c) => c.startsWith('__Host-vau='))).toBe(true);
    expect(cookies.some((c) => c.startsWith('__Host-vau_off=0; '))).toBe(true);
    for (const cookie of cookies) expect(cookie).not.toMatch(/Domain=/);
  });

  it('uses __Secure- names with Domain when cookieDomain is set', () => {
    const { analytics } = web({ requiresPriorConsent: false, cookieDomain: 'example.com' });
    const headers = new Headers();
    const visitor = analytics.forFetch(request(), headers);
    visitor.track('page_view');
    visitor.setOptedOut(false);

    expect(setCookies(headers)).toEqual([
      expect.stringMatching(/^__Secure-vau=[0-9a-f-]{36}; .*Path=\/; Domain=example\.com; Secure; HttpOnly/),
      expect.stringMatching(/^__Secure-vau_off=0; .*Domain=example\.com; Secure/),
      expect.stringMatching(/^_vau_off=; Max-Age=0; (?!.*Domain)/),
    ]);
  });

  it('keeps an explicit name as is, and adds Domain when set', () => {
    const { analytics } = web({
      requiresPriorConsent: false,
      cookieName: 'my_id',
      optOutCookieName: 'my_off',
      cookieDomain: 'example.com',
    });
    const headers = new Headers();
    const visitor = analytics.forFetch(request(), headers);
    visitor.track('page_view');
    visitor.setOptedOut(true);

    expect(setCookies(headers)).toEqual([
      expect.stringMatching(/^my_id=[0-9a-f-]{36}; .*Domain=example\.com/),
      expect.stringMatching(/^my_off=1; .*Domain=example\.com/),
      expect.stringMatching(/^_vau_off=; Max-Age=0; (?!.*Domain)/),
      expect.stringMatching(/^my_id=; Max-Age=0; .*Domain=example\.com/),
      expect.stringMatching(/^_vau=; Max-Age=0; (?!.*Domain)/),
    ]);
  });

  it('migrates the legacy consent cookie', () => {
    const { analytics } = web({ requiresPriorConsent: true });
    const headers = new Headers();
    const visitor = analytics.forFetch(request({ cookie: '_vau_off=1' }), headers);

    expect(visitor.consentAnswer).toBe(false);
    expect(visitor.consentAnswer).toBe(false);
    expect(setCookies(headers)).toEqual([
      expect.stringMatching(/^__Host-vau_off=1; Max-Age=33696000; /),
      expect.stringMatching(/^_vau_off=; Max-Age=0; /),
    ]);
  });

  it('migrates the legacy id and restarts its lifetime', async () => {
    const { analytics, client, requests } = web({ requiresPriorConsent: false });
    const headers = new Headers();
    analytics.forFetch(request({ cookie: `_vau=${INSTALL}` }), headers).track('page_view');
    await client.flush();

    expect(requests[0]!.body).toMatchObject({ installId: INSTALL });
    expect(setCookies(headers)).toEqual([
      expect.stringMatching(new RegExp(`^__Host-vau=${INSTALL}; Max-Age=33696000; `)),
      expect.stringMatching(/^_vau=; Max-Age=0; /),
    ]);
  });

  it('prefers the current cookies over the legacy ones', () => {
    const { analytics } = web({ requiresPriorConsent: false });
    const headers = new Headers();
    const cookie = `__Host-vau=${INSTALL}; __Host-vau_off=0; _vau=${OTHER}; _vau_off=1`;
    const visitor = analytics.forFetch(request({ cookie }), headers);

    expect(visitor.consentAnswer).toBe(true);
    expect(visitor.installId).toBe(INSTALL);
    expect(setCookies(headers)).toHaveLength(0);
  });

  it('uses the legacy values without rewriting once the headers are sent', () => {
    const { analytics } = web({ requiresPriorConsent: true });
    const visitor = analytics.forNode(
      { headers: { cookie: `_vau=${INSTALL}; _vau_off=0` } } as unknown as IncomingMessage,
      {
        headersSent: true,
        getHeader: () => undefined,
        setHeader: () => {
          throw new Error('too late');
        },
      } as unknown as ServerResponse
    );

    expect(visitor.consentAnswer).toBe(true);
    expect(visitor.installId).toBe(INSTALL);
  });

  it('does not migrate when the explicit names are the legacy ones', () => {
    const { analytics } = web({ requiresPriorConsent: false, cookieName: '_vau', optOutCookieName: '_vau_off' });
    const headers = new Headers();
    const visitor = analytics.forFetch(request({ cookie: `_vau=${INSTALL}; _vau_off=0` }), headers);

    expect(visitor.installId).toBe(INSTALL);
    expect(visitor.consentAnswer).toBe(true);
    expect(setCookies(headers)).toHaveLength(0);
  });

  it('deletes the id under both names on opposition, with the same Domain as the write', () => {
    const { analytics } = web({ requiresPriorConsent: false, cookieDomain: 'example.com' });
    const headers = new Headers();
    analytics.forFetch(request({ cookie: `_vau=${INSTALL}` }), headers).setOptedOut(true);

    const cookies = setCookies(headers);
    expect(cookies).toContainEqual(expect.stringMatching(/^__Secure-vau=; Max-Age=0; .*Domain=example\.com/));
    expect(cookies).toContainEqual(expect.stringMatching(/^_vau=; Max-Age=0; (?!.*Domain)/));
  });
});

describe('node:http', () => {
  let close: (() => void) | undefined;
  afterEach(() => close?.());

  async function serve(handler: (req: IncomingMessage, res: ServerResponse) => void | Promise<void>) {
    const server = createServer((req, res) => void handler(req, res));
    await new Promise<void>((done) => server.listen(0, '127.0.0.1', done));
    close = () => server.close();
    return `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
  }

  it('reads the socket address and skips measurement once the headers are sent', async () => {
    const { analytics, client, requests } = web({ requiresPriorConsent: false });
    const base = await serve((req, res) => {
      if (req.url === '/late') {
        res.writeHead(200);
        analytics.forNode(req, res).track('page_view');
        res.end();
        return;
      }
      analytics.forNode(req, res).track('page_view');
      res.end();
    });

    const response = await fetch(`${base}/`);
    expect(response.headers.getSetCookie()).toHaveLength(1);
    await fetch(`${base}/late`);
    await client.flush();

    expect(requests).toHaveLength(1);
    expect(requests[0]!.headers.get('X-Analytics-Client-Address')).toBe('127.0.0.1');
  });

  it('relays the declared events and keys, and answers 204', async () => {
    const { analytics, client, requests } = web({ requiresPriorConsent: false });
    const relay = analytics.relay({
      events: { content_viewed: ['content_id', 'duration_s'], route_shared: { keys: [], with: { content_type: 'route' } } },
      props: { duration_s: ({ value }) => (typeof value === 'number' ? Math.min(value, 60) : undefined) },
    });
    const base = await serve(relay.node);

    const response = await fetch(`${base}/api/analytics/events`, {
      method: 'POST',
      headers: { cookie: `__Host-vau=${INSTALL}` },
      body: JSON.stringify([
        { name: 'content_viewed', props: { content_id: 'a', duration_s: 999, secret: 'x' } },
        { name: 'route_shared', props: { content_type: 'spoofed' } },
        { name: 'undeclared' },
      ]),
    });
    expect(response.status).toBe(204);
    await client.flush();

    const events = requests[0]!.body.events as Array<{ name: string; props?: Record<string, unknown> }>;
    expect(events.map((e) => [e.name, e.props])).toEqual([
      ['content_viewed', { content_id: 'a', duration_s: 60 }],
      ['route_shared', { content_type: 'route' }],
    ]);
  });

  it('answers the state and stores the consent', async () => {
    const { analytics } = web();
    const relay = analytics.relay({ events: {} });
    const base = await serve(relay.node);

    const state = await (await fetch(`${base}/x/state`, { headers: { 'accept-language': 'de-DE' } })).json();
    expect(state).toEqual({ requiresPriorConsent: true, consentAnswer: null });

    const bad = await fetch(`${base}/x/consent`, { method: 'POST', body: '{}' });
    expect(bad.status).toBe(400);

    const ok = await fetch(`${base}/x/consent`, { method: 'POST', body: JSON.stringify({ accepted: true }) });
    expect(ok.status).toBe(204);
    expect(ok.headers.getSetCookie()[0]).toMatch(/^__Host-vau_off=0; /);
  });

  it('drops a body over maxBodyBytes', async () => {
    const { analytics, client, requests } = web({ requiresPriorConsent: false });
    const relay = analytics.relay({ events: { a: ['p'] }, maxBodyBytes: 32 });
    const base = await serve(relay.node);

    const body = JSON.stringify([{ name: 'a', props: { p: 'x'.repeat(40) } }]);
    const response = await fetch(`${base}/events`, { method: 'POST', headers: { cookie: `__Host-vau=${INSTALL}` }, body });
    expect(response.status).toBe(204);
    await client.flush();
    expect(requests).toHaveLength(0);
  });
});

describe('Fetch API relay', () => {
  it('relays events and returns the cookie on the response', async () => {
    const { analytics, client, requests } = web({ requiresPriorConsent: false, clientAddress: () => '198.51.100.1' });
    const relay = analytics.relay({ events: { a: [] } });

    const response = await relay.fetch(
      request({}, 'https://site.example.com/api/analytics/events', { method: 'POST', body: '{"name":"a"}' })
    );
    expect(response.status).toBe(204);
    expect(response.headers.getSetCookie()[0]).toMatch(/^__Host-vau=/);

    await client.flush();
    expect(requests).toHaveLength(1);
    expect((await relay.fetch(request({}, 'https://site.example.com/other'))).status).toBe(404);
  });

  it('drops the events beyond the ceiling of one address, an IPv6 /64 counting as one', async () => {
    let address: string | undefined = '203.0.113.7';
    const { analytics, client, requests } = web({ requiresPriorConsent: false, clientAddress: () => address });
    const relay = analytics.relay({ events: { a: [] }, maxEventsPerAddress: 3 });
    const post = () =>
      relay.fetch(
        request({ cookie: `__Host-vau=${INSTALL}` }, 'https://site.example.com/events', {
          method: 'POST',
          body: '[{"name":"a"},{"name":"a"}]',
        })
      );

    await post(); // 2 kept
    await post(); // 1 kept
    await post(); // 0 kept
    address = '2001:db8:1:2::1';
    await post(); // 2 kept
    address = '2001:0db8:0001:0002:ffff::9';
    await post(); // 1 kept: the same /64
    address = '2001:db8:1:3::1';
    await post(); // 2 kept
    address = undefined;
    await post(); // 2 kept: no address, no ceiling
    await post(); // 2 kept
    await client.flush();

    const sent = requests.flatMap((r) => (r.body as { events: unknown[] }).events);
    expect(sent).toHaveLength(12);
  });

  it('does not limit when maxEventsPerAddress is 0', async () => {
    const { analytics, client, requests } = web({ requiresPriorConsent: false, clientAddress: () => '203.0.113.7' });
    const relay = analytics.relay({ events: { a: [] }, maxEventsPerAddress: 0, maxEventsPerRequest: 50 });
    const body = JSON.stringify(Array.from({ length: 40 }, () => ({ name: 'a' })));

    await relay.fetch(request({ cookie: `__Host-vau=${INSTALL}` }, 'https://site.example.com/events', { method: 'POST', body }));
    await client.flush();

    expect(requests.flatMap((r) => (r.body as { events: unknown[] }).events)).toHaveLength(40);
  });
});
