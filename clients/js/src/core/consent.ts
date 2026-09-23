/**
 * The default answer for `requiresPriorConsent`, from the regimes listed in OBLIGATIONS.md. Left
 * null, the client asks this itself, with the device locale.
 *
 * The EEA outside France, Italy, Spain and the Netherlands, plus the United Kingdom.
 */
export const PRIOR_CONSENT_COUNTRIES: ReadonlySet<string> = new Set([
  'AT', 'BE', 'BG', 'CY', 'CZ', 'DE', 'DK', 'EE', 'FI', 'GB', 'GR', 'HR', 'HU',
  'IE', 'IS', 'LI', 'LT', 'LU', 'LV', 'MT', 'NO', 'PL', 'PT', 'RO', 'SE', 'SI', 'SK',
]);

/**
 * Whether `locale` — a BCP-47 tag such as `fr-FR`, the device locale — asks before anything may be
 * stored. True when the tag carries no region.
 *
 * `fr-CA` answers true and `en-CA` false: Quebec asks first while the rest of Canada does not, and
 * the language is the only signal a locale carries about it. A francophone outside Quebec is asked
 * needlessly, an anglophone inside it is not asked at all.
 *
 * A starting point for your own counsel, not a legal opinion. Authority positions move, and this list
 * moves only when the package is updated.
 */
export function localeRequiresPriorConsent(locale: string | null | undefined): boolean {
  const { language, region } = splitBcp47(locale);
  if (region === undefined) return true;
  if (PRIOR_CONSENT_COUNTRIES.has(region)) return true;
  return region === 'CA' && language?.toLowerCase() === 'fr';
}

/**
 * The primary language subtag and the region subtag of a BCP-47 tag, both undefined when absent. The
 * region is the first 2-letter subtag after the language, which skips a script (`zh-Hant-TW`) and a
 * UN M.49 code (`es-419`, no region).
 */
export function splitBcp47(locale: string | null | undefined): {
  language: string | undefined;
  region: string | undefined;
} {
  const parts = (locale ?? '').trim().replace(/_/g, '-').split('-').filter((p) => p !== '');
  if (parts.length === 0) return { language: undefined, region: undefined };

  const language = parts[0]!.length >= 2 && parts[0]!.length <= 3 ? parts[0] : undefined;
  const region = parts.slice(1).find((p) => /^[A-Za-z]{2}$/.test(p));

  return { language, region: region?.toUpperCase() };
}
