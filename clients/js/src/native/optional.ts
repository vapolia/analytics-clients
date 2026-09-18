/**
 * The optional Expo packages.
 *
 * Each one is loaded through its own literal `require` in its own try/catch: Metro resolves requires
 * statically, so `require(name)` with a variable would not work, and a missing package must be a
 * silent absence rather than a bundling error.
 *
 * The `typeof require` guard is for a true ESM runtime, where `require` does not exist — Metro
 * transpiles modules to CommonJS, so it does exist there.
 */

export interface ExpoLocalization {
  getLocales(): { languageTag?: string; regionCode?: string | null }[];
}

export interface ExpoDevice {
  DeviceType: { PHONE: number; TABLET: number; DESKTOP: number; TV: number; UNKNOWN: number };
  getDeviceTypeAsync(): Promise<number>;
}

export interface ExpoApplication {
  nativeBuildVersion: string | null;
}

function canRequire(): boolean {
  return typeof require === 'function';
}

export function loadLocalization(): ExpoLocalization | undefined {
  if (!canRequire()) return undefined;
  try {
    return require('expo-localization') as ExpoLocalization;
  } catch {
    return undefined;
  }
}

export function loadDevice(): ExpoDevice | undefined {
  if (!canRequire()) return undefined;
  try {
    return require('expo-device') as ExpoDevice;
  } catch {
    return undefined;
  }
}

export function loadApplication(): ExpoApplication | undefined {
  if (!canRequire()) return undefined;
  try {
    return require('expo-application') as ExpoApplication;
  } catch {
    return undefined;
  }
}
