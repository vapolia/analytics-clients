import type { AnalyticsStorage } from '../core/types';
import { loadAsyncStorage } from './optional';

/**
 * The installation id, the consent answer and the spool, when the app passes no `advanced.storage`.
 * Without storage there is no stable id and no recovery of what could not be sent, so the client
 * does not start without one.
 *
 * AsyncStorage carries its own iOS privacy manifest entry for `UserDefaults` (reason CA92.1), so an
 * app using it has nothing to declare for this client.
 */
export function defaultStorage(): AnalyticsStorage | undefined {
  const asyncStorage = loadAsyncStorage();
  if (!asyncStorage) return undefined;

  return {
    getItem: (key) => asyncStorage.getItem(key),
    setItem: (key, value) => asyncStorage.setItem(key, value),
    removeItem: (key) => asyncStorage.removeItem(key),
  };
}
