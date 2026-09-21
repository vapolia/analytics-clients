import { loadLocalization } from './optional';
import type { Device } from '../core/types';

/**
 * The device context, read from the device itself: one setting, no hardware id, no advertising id,
 * and no geolocation — `country` is the user's region setting, which is what the exemption requires.
 *
 * `expo-localization` is optional. Without it the region is simply not sent, and an app that passes
 * its own `device` in the options needs no Expo package at all.
 */
export async function detectDevice(): Promise<Device> {
  const localization = loadLocalization();
  if (!localization) return {};

  return { country: localization.getLocales()[0]?.regionCode ?? undefined };
}
