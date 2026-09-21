const DAY_MS = 24 * 60 * 60 * 1000;

/**
 * The age of an installation, in buckets, for an app that wants to segment on it.
 *
 * The client keeps the first-seen date — it is the only thing that knows it, and it keeps it across
 * id renewals — but it does not send a bucket on its own: that would be the client naming what is
 * measured. Call this from your `context` if the key is on your whitelist.
 */
export function installAgeBucket(firstSeen: number, now: number = Date.now()): string {
  const days = (now - firstSeen) / DAY_MS;
  if (days < 1) return '0';
  if (days < 8) return '1-7';
  if (days < 31) return '8-30';
  if (days < 91) return '31-90';
  return '90+';
}
