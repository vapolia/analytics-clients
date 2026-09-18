import { describe, expect, it } from 'vitest';

import { encodeBatch } from '../core/batch';

describe('tz', () => {
  it('sends minutes east of UTC per event, apart from ts, and nothing when unknown', () => {
    const json = encodeBatch('11111111-0000-0000-0000-000011111111', {}, [
      { name: 'app_open', ts: 0, props: {}, tz: 120 },
      { name: 'game_end', ts: 1, props: {} },
    ]);

    const events = JSON.parse(json).events;
    expect(events[0].tz).toBe(120);
    expect(events[0].ts.endsWith('Z')).toBe(true);
    expect('tz' in events[1]).toBe(false);
  });
});
