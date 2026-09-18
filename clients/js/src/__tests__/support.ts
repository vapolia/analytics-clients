import type { AnalyticsStorage } from '../core/types';
import type { Poster, SendResult } from '../core/transport';

/** AsyncStorage, reduced to what the client uses. */
export class MemoryStorage implements AnalyticsStorage {
  readonly items = new Map<string, string>();

  async getItem(key: string): Promise<string | null> {
    return this.items.get(key) ?? null;
  }

  async setItem(key: string, value: string): Promise<void> {
    this.items.set(key, value);
  }

  async removeItem(key: string): Promise<void> {
    this.items.delete(key);
  }
}

/** The collector, reduced to what a client can observe: the bodies it received, and its answers. */
export class FakeCollector implements Poster {
  readonly bodies: string[] = [];

  constructor(private readonly answers: SendResult[] = []) {}

  async post(body: string): Promise<SendResult> {
    this.bodies.push(body);
    return this.answers.shift() ?? { kind: 'ok' };
  }

  get batches(): { installId: string; events: { name: string }[] }[] {
    return this.bodies.map((body) => JSON.parse(body));
  }
}
