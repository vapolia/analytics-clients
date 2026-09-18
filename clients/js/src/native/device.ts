import { Platform } from 'react-native';

import { loadApplication, loadDevice, loadLocalization } from './optional';
import type { Device } from '../core/types';

/**
 * The device context, read from the device itself.
 *
 * Everything here is a setting or a build constant: no hardware id, no advertising id, and no
 * geolocation — `country` is the user's region setting, which is what the exemption requires.
 *
 * Every Expo package is optional. What is missing is simply not sent, and an app that passes its own
 * `device` in the options needs none of them.
 */
export async function detectDevice(): Promise<Device> {
  const device: Device = {
    platform: platform(),
    osVersion: String(Platform.Version),
    // iOS builds come from the App Store or TestFlight. On Android the installer package is not
    // readable from JS, so the field is left out rather than assumed to be Play.
    store: Platform.OS === 'ios' ? 'apple' : undefined,
  };

  const localization = loadLocalization();
  if (localization) {
    const locale = localization.getLocales()[0];
    device.locale = locale?.languageTag ?? undefined;
    device.country = locale?.regionCode ?? undefined;
  }

  const application = loadApplication();
  if (application) {
    device.build = application.nativeBuildVersion ?? undefined;
  }

  const expoDevice = loadDevice();
  if (expoDevice) {
    try {
      const type = await expoDevice.getDeviceTypeAsync();
      device.deviceClass = deviceClass(expoDevice, type);
    } catch {
      // Leave it out: an unknown device class is better than a guessed one.
    }
  }

  return device;
}

function platform(): string {
  // Platform.isMacCatalyst exists on the iOS runtime only; the collector has a value for it.
  const iosPlatform = Platform as unknown as { isMacCatalyst?: boolean };
  if (Platform.OS === 'ios') return iosPlatform.isMacCatalyst ? 'maccatalyst' : 'ios';
  return Platform.OS;
}

function deviceClass(expoDevice: NonNullable<ReturnType<typeof loadDevice>>, type: number): string {
  switch (type) {
    case expoDevice.DeviceType.PHONE:
      return 'phone';
    case expoDevice.DeviceType.TABLET:
      return 'tablet';
    case expoDevice.DeviceType.DESKTOP:
      return 'desktop';
    default:
      return 'other';
  }
}
