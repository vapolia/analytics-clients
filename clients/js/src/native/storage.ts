import AsyncStorage from '@react-native-async-storage/async-storage';

import type { AnalyticsStorage } from '../core/types';

/**
 * The installation id and the spool. AsyncStorage is a required peer: without storage there is no
 * stable id and no recovery of what could not be sent, which is most of what this client does.
 *
 * It also carries its own iOS privacy manifest entry for `UserDefaults` (reason CA92.1), so an app
 * using it has nothing to declare for this client.
 */
export const asyncStorage: AnalyticsStorage = {
  getItem: (key) => AsyncStorage.getItem(key),
  setItem: (key, value) => AsyncStorage.setItem(key, value),
  removeItem: (key) => AsyncStorage.removeItem(key),
};
