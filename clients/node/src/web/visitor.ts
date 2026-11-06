import type { IncomingMessage, ServerResponse } from 'node:http';
import { randomUUID } from 'node:crypto';
import * as clean from '../clean.js';
import type { Client } from '../client.js';
import type { Props } from '../types.js';
import { fromFetch, fromNode, type RequestAdapter } from './adapters.js';
import { localeRequiresPriorConsent, preferredLanguage, splitBcp47 } from './consent.js';
import { Relay, type RelayOptions } from './relay.js';

const DAY_MS = 24 * 60 * 60 * 1000;

export type WebRequest = IncomingMessage | Request;

export interface WebOptions {
  /** The answer while the visitor has not chosen. Left undefined, it follows the Accept-Language region. */
  requiresPriorConsent?: boolean;
  /** Takes precedence over `requiresPriorConsent` when set. */
  requiresPriorConsentForLocale?: (locale: string | undefined) => boolean;
  /** Default `_vau`, shared with the PHP and ASP.NET Core clients. */
  cookieName?: string;
  /** Default `_vau_off`: `1` refused, `0` accepted. */
  optOutCookieName?: string;
  /** Default 390 days. Never extended on a later visit. */
  installIdLifetimeMs?: number;
  /** Default 390 days. */
  optOutLifetimeMs?: number;
  /**
   * The visitor's IP address. Defaults to the socket's peer for node:http and to none for the Fetch
   * API. Behind a proxy, return the address the proxy reports, for example Express `req.ip` with
   * `trust proxy` set.
   */
  clientAddress?: (request: WebRequest) => string | undefined;
  /** The visitor's offset in minutes east of UTC, for example from a cookie set by a script. */
  timeZoneOffset?: (request: WebRequest) => number | undefined;
  /** What is true of the visitor for a whole batch. Its keys must be on the source's context whitelist. */
  context?: (request: WebRequest) => Props | undefined;
}

interface ResolvedWebOptions extends Required<Pick<WebOptions, 'cookieName' | 'optOutCookieName' | 'installIdLifetimeMs' | 'optOutLifetimeMs'>> {
  options: WebOptions;
}

/** Measures website visitors with a first-party cookie, from the server. */
export class WebAnalytics {
  private readonly resolved: ResolvedWebOptions;

  constructor(
    private readonly client: Client,
    options: WebOptions = {}
  ) {
    this.resolved = {
      options,
      cookieName: options.cookieName ?? '_vau',
      optOutCookieName: options.optOutCookieName ?? '_vau_off',
      installIdLifetimeMs: options.installIdLifetimeMs ?? 390 * DAY_MS,
      optOutLifetimeMs: options.optOutLifetimeMs ?? 390 * DAY_MS,
    };
  }

  /** The visitor of a node:http request. Call it before the response headers are sent. */
  forNode(req: IncomingMessage, res: ServerResponse): Visitor {
    return new Visitor(this.client, this.resolved, fromNode(req, res), req);
  }

  /** The visitor of a Fetch API request. Cookies are appended to `responseHeaders`. */
  forFetch(request: Request, responseHeaders: Headers): Visitor {
    return new Visitor(this.client, this.resolved, fromFetch(request, responseHeaders), request);
  }

  /** The three endpoints a JavaScript front end calls when the server emits the events for it. */
  relay(options: RelayOptions): Relay {
    return new Relay(this, options);
  }
}

/** One visitor, for the duration of one request. */
export class Visitor {
  private id: string | undefined;
  private answer: boolean | undefined;

  constructor(
    private readonly client: Client,
    private readonly web: ResolvedWebOptions,
    private readonly adapter: RequestAdapter,
    private readonly request: WebRequest
  ) {}

  /** True accepted, false refused, undefined not answered yet: whether to show the consent banner. */
  get consentAnswer(): boolean | undefined {
    if (this.answer !== undefined) return this.answer;

    const cookie = this.adapter.cookie(this.web.optOutCookieName);
    if (cookie === '1') return false;
    if (cookie === '0') return true;
    return undefined;
  }

  /** Whether the regime of this request requires consent before anything is stored, whatever the answer. */
  get requiresPriorConsent(): boolean {
    const locale = this.locale();
    const { options } = this.web;
    if (options.requiresPriorConsentForLocale) return options.requiresPriorConsentForLocale(locale);
    return options.requiresPriorConsent ?? localeRequiresPriorConsent(locale);
  }

  get isOptedOut(): boolean {
    const answer = this.consentAnswer;
    return answer === undefined ? this.requiresPriorConsent : !answer;
  }

  /**
   * The visitor's id, from the cookie, created when missing and the headers are not sent yet.
   * Undefined when opposed, not answered under a prior-consent regime, or too late to set a cookie.
   */
  get installId(): string | undefined {
    // Checked first: an opposed visitor gets no identifier written at all.
    if (this.isOptedOut) return undefined;
    if (this.id !== undefined) return this.id;

    const fromCookie = clean.installId(this.adapter.cookie(this.web.cookieName));
    if (fromCookie !== undefined) return (this.id = fromCookie);

    // Too late for a cookie: the visitor is measured on the next request.
    if (!this.adapter.canSetCookies()) return undefined;

    const issued = randomUUID();
    this.adapter.setCookie(this.web.cookieName, issued, Math.floor(this.web.installIdLifetimeMs / 1000));
    return (this.id = issued);
  }

  /** The region of the first `Accept-Language` tag. Never a geolocation of the IP. */
  get country(): string | undefined {
    return splitBcp47(this.locale()).region;
  }

  /**
   * The right of opposition, and the consent banner's two buttons. Call it before the headers are
   * sent. Opposing drops this visitor's buffered events and deletes the identity cookie.
   */
  setOptedOut(value: boolean): void {
    this.answer = !value;

    if (value) {
      const id = this.id ?? clean.installId(this.adapter.cookie(this.web.cookieName));
      this.id = undefined;
      if (id !== undefined) this.client.discard(id);
    }

    if (!this.adapter.canSetCookies()) return;

    // "0", not a deletion: an acceptance has to outrank requiresPriorConsent on the next request.
    this.adapter.setCookie(this.web.optOutCookieName, value ? '1' : '0', Math.floor(this.web.optOutLifetimeMs / 1000));
    if (value) this.adapter.setCookie(this.web.cookieName, '', 0);
  }

  /** Queues one event for this visitor. Never throws. */
  track(name: string, props?: Props): void {
    try {
      const id = this.installId;
      if (id === undefined) return;

      const { options } = this.web;
      const address = options.clientAddress ? options.clientAddress(this.request) : this.adapter.remoteAddress();
      this.client.enqueue(
        id,
        { country: this.country },
        options.context?.(this.request),
        options.timeZoneOffset?.(this.request),
        address,
        name,
        props
      );
    } catch {
      // A failing callback of the site costs this event, never the request.
    }
  }

  private locale(): string | undefined {
    return preferredLanguage(this.adapter.header('accept-language'));
  }
}
