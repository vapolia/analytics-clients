import { describe, expect, it } from 'vitest';

import { localeRequiresPriorConsent } from '../core/consent';

describe('localeRequiresPriorConsent', () => {
  it('asks first across the EEA outside the four exempt countries, and in the UK', () => {
    expect(localeRequiresPriorConsent('de-DE')).toBe(true);
    expect(localeRequiresPriorConsent('en-gb')).toBe(true);
    expect(localeRequiresPriorConsent('nb-NO')).toBe(true);
  });

  it('does not ask in an exempt country, nor under a notice regime', () => {
    expect(localeRequiresPriorConsent('fr-FR')).toBe(false);
    expect(localeRequiresPriorConsent('it-IT')).toBe(false);
    expect(localeRequiresPriorConsent('en-US')).toBe(false);
  });

  it('reads Quebec off the language, which is all a locale says about a province', () => {
    expect(localeRequiresPriorConsent('fr-CA')).toBe(true);
    expect(localeRequiresPriorConsent('en-CA')).toBe(false);
  });

  it('asks first when the tag carries no region', () => {
    expect(localeRequiresPriorConsent(undefined)).toBe(true);
    expect(localeRequiresPriorConsent(null)).toBe(true);
    expect(localeRequiresPriorConsent('fr')).toBe(true);
    expect(localeRequiresPriorConsent('es-419')).toBe(true);
    expect(localeRequiresPriorConsent('  ')).toBe(true);
  });

  it('reads the region past a script subtag, and takes an underscore tag', () => {
    expect(localeRequiresPriorConsent('zh-Hant-TW')).toBe(false);
    expect(localeRequiresPriorConsent('de_AT')).toBe(true);
  });
});
