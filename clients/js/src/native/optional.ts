/**
 * The optional packages: AsyncStorage when the app passes its own storage, and the Expo ones.
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

export interface AsyncStorageModule {
  getItem(key: string): Promise<string | null>;
  setItem(key: string, value: string): Promise<void>;
  removeItem(key: string): Promise<void>;
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

export function loadAsyncStorage(): AsyncStorageModule | undefined {
  if (!canRequire()) return undefined;
  try {
    const module = require('@react-native-async-storage/async-storage') as {
      default?: AsyncStorageModule;
    } & AsyncStorageModule;
    return module.default ?? module;
  } catch {
    return undefined;
  }
}
