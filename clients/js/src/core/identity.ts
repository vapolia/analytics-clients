import { installId as cleanInstallId } from './clean';
import type { AnalyticsStorage } from './types';

const KEY_ID = 'vapolia.analytics.installId';
const KEY_ISSUED_AT = 'vapolia.analytics.installIdIssuedAt';
const KEY_FIRST_OPEN = 'vapolia.analytics.firstOpenSent';
const KEY_OPTED_OUT = 'vapolia.analytics.optedOut';

const DAY_MS = 24 * 60 * 60 * 1000;

/** 13 months is the legal ceiling, with no extension. The margin absorbs clock drift. */
const MAX_AGE_MS = 390 * DAY_MS;

/**
 * The installation id, and the two flags that go with it. The id is random, local, and renewed on
 * its own: never derived from an account, a device id or an advertising id.
 *
 * It is kept in the app's own key/value storage, deliberately not in the keychain / secure store: a
 * keychain item survives the app being deleted, which would make the id outlive the installation it
 * names.
 */
export class Identity {
  private id: string | undefined;
  private optedOut = false;
  private firstOpenSent = true;
  private loaded = false;

  constructor(
    private readonly storage: AnalyticsStorage,
    private readonly now: () => number = () => Date.now()
  ) {}

  /** Reads storage once, rotating the id if it reached its ceiling. */
  async load(): Promise<void> {
    if (this.loaded) return;

    const [stored, issuedAtRaw, firstOpen, optedOut] = await Promise.all([
      this.read(KEY_ID),
      this.read(KEY_ISSUED_AT),
      this.read(KEY_FIRST_OPEN),
      this.read(KEY_OPTED_OUT),
    ]);

    this.loaded = true;
    this.optedOut = optedOut === 'true';
    this.firstOpenSent = firstOpen === 'true';

    if (this.optedOut) return;

    const today = this.now();
    const issuedAt = Number.parseInt(issuedAtRaw ?? '', 10);
    // A device clock moved backwards would otherwise freeze the id: reissue rather than extend.
    const expired =
      !Number.isFinite(issuedAt) ||
      today - issuedAt >= MAX_AGE_MS ||
      issuedAt > today + DAY_MS;

    const valid = cleanInstallId(stored);
    if (valid !== undefined && !expired) {
      this.id = valid;
      return;
    }

    this.id = randomUuid();
    await this.write(KEY_ID, this.id);
    await this.write(KEY_ISSUED_AT, String(today));
  }

  /** The current id, or undefined while storage has not answered yet, or when opted out. */
  current(): string | undefined {
    return this.optedOut ? undefined : this.id;
  }

  /**
   * Whether `first_open` still has to be sent. Kept apart from the id: a renewal is not a new
   * installation.
   */
  isFirstOpenPending(): boolean {
    return this.loaded && !this.firstOpenSent;
  }

  async markFirstOpenSent(): Promise<void> {
    this.firstOpenSent = true;
    await this.write(KEY_FIRST_OPEN, 'true');
  }

  isOptedOut(): boolean {
    return this.optedOut;
  }

  /**
   * Opting out forgets the id, so opting back in later cannot resume the same installation.
   */
  async setOptedOut(value: boolean): Promise<void> {
    this.optedOut = value;
    await this.write(KEY_OPTED_OUT, value ? 'true' : 'false');

    if (!value) {
      if (this.id === undefined) {
        this.id = randomUuid();
        await this.write(KEY_ID, this.id);
        await this.write(KEY_ISSUED_AT, String(this.now()));
      }
      return;
    }

    this.id = undefined;
    await this.remove(KEY_ID);
    await this.remove(KEY_ISSUED_AT);
  }

  private async read(key: string): Promise<string | null> {
    try {
      return await this.storage.getItem(key);
    } catch {
      return null;
    }
  }

  private async write(key: string, value: string): Promise<void> {
    try {
      await this.storage.setItem(key, value);
    } catch {
      // An id that cannot be persisted is regenerated next launch: a few duplicated installs, no crash.
    }
  }

  private async remove(key: string): Promise<void> {
    try {
      await this.storage.removeItem(key);
    } catch {
      // Same as above.
    }
  }
}

/**
 * A v4 UUID. `crypto.randomUUID` when the runtime has it, then `getRandomValues`, then `Math.random`:
 * this id names an installation, it is not a secret.
 */
export function randomUuid(): string {
  const scope = globalThis as {
    crypto?: {
      randomUUID?: () => string;
      getRandomValues?: (array: Uint8Array) => Uint8Array;
    };
  };

  if (typeof scope.crypto?.randomUUID === 'function') return scope.crypto.randomUUID().toLowerCase();

  const bytes = new Uint8Array(16);
  if (typeof scope.crypto?.getRandomValues === 'function') {
    scope.crypto.getRandomValues(bytes);
  } else {
    for (let index = 0; index < bytes.length; index += 1) {
      bytes[index] = Math.floor(Math.random() * 256);
    }
  }

  bytes[6] = ((bytes[6] ?? 0) & 0x0f) | 0x40;
  bytes[8] = ((bytes[8] ?? 0) & 0x3f) | 0x80;

  const hex = Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
