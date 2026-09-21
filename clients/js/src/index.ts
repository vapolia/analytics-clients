import { AppState, type AppStateStatus, type NativeEventSubscription } from 'react-native';

import { Identity } from './core/identity';
import { Queue } from './core/queue';
import { Spool } from './core/spool';
import { FetchPoster } from './core/transport';
import { resolveOptions } from './core/types';
import { detectDevice } from './native/device';
import { asyncStorage } from './native/storage';
import { setBackgroundFlusher } from './native/background';
import type { AnalyticsOptions, AnalyticsStats, Context, Device, Props } from './core/types';

export type {
  AnalyticsAdvancedOptions,
  AnalyticsAppOptions,
  AnalyticsLogger,
  AnalyticsOptions,
  AnalyticsStats,
  AnalyticsStorage,
  Context,
  Device,
  InstallSeed,
  Props,
  PropValue,
} from './core/types';
export { installAgeBucket } from './core/install';
export { registerBackgroundFlush } from './native/background';

interface Running {
  queue: Queue;
  identity: Identity;
  subscription: NativeEventSubscription | undefined;
  context: () => Context | undefined;
  autoFlushOnBackground: boolean;
}

let running: Running | undefined;
let device: Device = {};
let starting: Promise<void> | undefined;
let warned = false;

/**
 * Starts the client. Call it once, as early as possible — awaiting it is optional: events tracked
 * before storage answers are queued, not lost.
 *
 * ```ts
 * void Analytics.start({ ingestionUrl: 'https://analytics.example.com/myapp' });
 * Analytics.track('game_end', { result: 'win', moves: 34 });
 * ```
 */
export async function start(options: AnalyticsOptions): Promise<void> {
  if (running) return;
  if (starting) return starting;

  const resolved = resolveOptions(options);
  if (!resolved.enabled) return;

  starting = (async () => {
    const identity = new Identity(
      asyncStorage,
      () => Date.now(),
      resolved.installIdLifetimeMs,
      resolved.optOutLifetimeMs,
      resolved.seedInstallId
    );
    const queue = new Queue(
      resolved,
      new FetchPoster(resolved.ingestionUrl, resolved.requestTimeoutMs, resolved.token || undefined),
      // Zero capacity is how an app turns the spool off, as in the .NET client.
      resolved.spoolCapacity > 0
        ? new Spool(asyncStorage, resolved.source, resolved.spoolCapacity)
        : undefined,
      () => identity.current()
    );

    running = {
      queue,
      identity,
      subscription: undefined,
      context: resolved.context,
      autoFlushOnBackground: resolved.autoFlushOnBackground,
    };
    setBackgroundFlusher(() => queue.flush());

    // The detected context first, so anything the caller passed wins over it.
    const detected = await detectDevice().catch(() => ({}) as Device);
    device = { ...detected, ...resolved.device };

    await identity.load();
    await queue.start();

    running.subscription = AppState.addEventListener('change', onAppStateChange);
  })().finally(() => {
    starting = undefined;
  });

  return starting;
}

/**
 * Queues one event. Never blocks, never throws. `name` and every property key must be on the
 * source's whitelist, which this client cannot know.
 */
export function track(name: string, props?: Props): void {
  if (!running) {
    if (!warned) {
      warned = true;
      console.warn(`[analytics] start() was never called: "${name}" and the next ones are ignored`);
    }
    return;
  }

  if (running.identity.isOptedOut()) return;
  running.queue.track(device, name, props, running.context());
}

/** Sends what is queued. Awaiting it is optional. */
export async function flush(): Promise<void> {
  await running?.queue.flush();
}

/** One last flush, then the sender stops. Rarely needed. */
export async function stop(): Promise<void> {
  const current = running;
  running = undefined;
  current?.subscription?.remove();
  await current?.queue.stop();
}

/**
 * The right of opposition. Turning it on stops collection, drops what was queued, and forgets the
 * installation id, so opting back in cannot resume the same installation.
 */
export async function setOptedOut(value: boolean): Promise<void> {
  if (!running) return;

  await running.identity.setOptedOut(value);
  if (value) await running.queue.clear();
}

export function isOptedOut(): boolean {
  return running?.identity.isOptedOut() ?? false;
}

/** The current installation id, for a support screen. Undefined when opted out or not started. */
export function getInstallId(): string | undefined {
  return running?.identity.current();
}

export function getStats(): AnalyticsStats {
  return (
    running?.queue.getStats() ?? { accepted: 0, rejected: 0, dropped: 0, sent: 0, requests: 0 }
  );
}

/**
 * Corrects what the device probe reported. Not for anything about the app itself, which belongs in
 * the batch context — see `context` in the options.
 */
export function updateDevice(transform: (current: Device) => Device): void {
  device = transform(device);
}

/**
 * Whether this installation has never been seen before — what decides your own `first_open`. Kept
 * apart from the id, so a rotation does not count as a new installation.
 */
export function isFirstRun(): boolean {
  return running?.identity.isFirstOpenPending() ?? false;
}

/** Records that the installation has been seen. Call it once you emitted your own `first_open`. */
export async function markSeen(): Promise<void> {
  await running?.identity.markFirstOpenSent();
}

/**
 * When this installation was first seen, in milliseconds since the epoch, kept across id renewals.
 * Feed it to `installAgeBucket` if your source whitelists a bucket for it.
 */
export function firstSeen(): number | undefined {
  return running?.identity.firstSeen();
}

/**
 * The app returning to the foreground, and leaving it. It hangs off the AppState listener the client
 * already holds, rather than a second one.
 */
export const lifecycle: { onForeground?: () => void; onBackground?: () => void } = {};

let wasActive = true;

function onAppStateChange(state: AppStateStatus): void {
  const current = running;
  if (!current) return;

  if (state === 'active') {
    if (!wasActive) lifecycle.onForeground?.();
    wasActive = true;
    return;
  }

  if (state === 'background') {
    wasActive = false;
    lifecycle.onBackground?.();
    if (!current.autoFlushOnBackground) return;
    // Best effort: the JS engine is suspended shortly after this. The spool, written a few hundred
    // milliseconds after every event, is what actually survives.
    void current.queue.persist();
    void current.queue.flush();
  }
}

/** Everything under one name, for call sites that prefer `Analytics.track(...)`. */
export const Analytics = {
  start,
  track,
  flush,
  stop,
  setOptedOut,
  isOptedOut,
  getInstallId,
  getStats,
  updateDevice,
  isFirstRun,
  markSeen,
  firstSeen,
  lifecycle,
};

export default Analytics;
