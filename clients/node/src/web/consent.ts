/**
 * The default answer for `requiresPriorConsent`: the EEA outside France, Italy, Spain and the
 * Netherlands, plus the United Kingdom. Same list as the other clients, from OBLIGATIONS.md.
 */
export const PRIOR_CONSENT_COUNTRIES: readonly string[] = [
  'AT', 'BE', 'BG', 'CY', 'CZ', 'DE', 'DK', 'EE', 'FI', 'GB', 'GR', 'HR', 'HU',
  'IE', 'IS', 'LI', 'LT', 'LU', 'LV', 'MT', 'NO', 'PL', 'PT', 'RO', 'SE', 'SI', 'SK',
];

/**
 * The language and region subtags of a BCP-47 tag. The region is the first two-letter subtag after
 * the language, which skips a script (`zh-Hant-TW`) and a UN M.49 code.
 */
export function splitBcp47(locale: string | undefined): { language?: string; region?: string } {
  const parts = (locale ?? '').trim().replace(/_/g, '-').split('-').filter((p) => p !== '');
  const first = parts[0];
  if (first === undefined) return {};

  const language = first.length >= 2 && first.length <= 3 ? first : undefined;
  const region = parts.slice(1).find((p) => /^[a-z]{2}$/i.test(p))?.toUpperCase();
  return { language, region };
}

/**
 * Whether `locale` asks for consent before anything is stored. True when the tag carries no region.
 * `fr-CA` answers true and `en-CA` false, for Quebec.
 */
export function localeRequiresPriorConsent(locale: string | undefined): boolean {
  const { language, region } = splitBcp47(locale);
  if (region === undefined) return true;
  if (PRIOR_CONSENT_COUNTRIES.includes(region)) return true;
  return region === 'CA' && language?.toLowerCase() === 'fr';
}

/** The first tag of an `Accept-Language` header, as in the PHP and ASP.NET Core clients. */
export function preferredLanguage(header: string | undefined): string | undefined {
  const first = (header ?? '').split(',')[0]?.split(';')[0]?.trim();
  return first ? first : undefined;
}
