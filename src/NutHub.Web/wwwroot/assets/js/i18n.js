// Translations. Every visible string goes through t(key, params). Dictionaries are flat maps of dotted keys;
// a value can be a string with {placeholders} or a plural map ({ one, other, ... }) chosen by params.count.
// Missing keys fall back to English, then to the key itself, so a missing translation is visible but harmless.
import en from './i18n/en.js';
import it from './i18n/it.js';

const DICTIONARIES = { en, it };
const STORAGE_KEY = 'nuthub.lang';

export const LANGUAGES = [
  { code: 'en', name: 'English' },
  { code: 'it', name: 'Italiano' },
];

let current = detect();
const listeners = new Set();
const pluralRules = new Map();

function stored() {
  try {
    return window.localStorage.getItem(STORAGE_KEY);
  } catch {
    return null;
  }
}

function detect() {
  const saved = stored();
  if (saved && DICTIONARIES[saved]) return saved;
  for (const tag of navigator.languages ?? [navigator.language]) {
    const base = String(tag || '').toLowerCase().split('-')[0];
    if (DICTIONARIES[base]) return base;
  }
  return 'en';
}

/** The active language code ("en", "it"). */
export function lang() {
  return current;
}

/**
 * The locale used for numbers and dates: the browser's own locale when it matches the chosen language (so an
 * English speaker in the UK gets day/month order), otherwise the language itself.
 */
export function locale() {
  for (const tag of navigator.languages ?? [navigator.language]) {
    if (tag && tag.toLowerCase().split('-')[0] === current) return tag;
  }
  return current === 'it' ? 'it-IT' : 'en-US';
}

/** Changes the language, remembers it and notifies the listeners (the shell re-renders the page). */
export function setLang(code) {
  if (!DICTIONARIES[code] || code === current) return;
  current = code;
  try {
    window.localStorage.setItem(STORAGE_KEY, code);
  } catch {
    // Not persisted; the choice still applies to this tab.
  }
  document.documentElement.lang = code;
  for (const fn of listeners) fn(code);
}

/** Registers a language change listener; returns the unsubscribe function. */
export function onLangChange(fn) {
  listeners.add(fn);
  return () => listeners.delete(fn);
}

/** Whether a key exists in the active dictionary or in English. */
export function has(key) {
  return key in DICTIONARIES[current] || key in en;
}

function plural(entry, count) {
  let rules = pluralRules.get(current);
  if (!rules) {
    rules = new Intl.PluralRules(current);
    pluralRules.set(current, rules);
  }
  const n = Number(count);
  if (n === 0 && 'zero' in entry) return entry.zero;
  return entry[rules.select(Number.isFinite(n) ? n : 0)] ?? entry.other ?? '';
}

/** Translates a key, replacing {name} placeholders with params. */
export function t(key, params) {
  let entry = DICTIONARIES[current][key];
  if (entry === undefined) entry = en[key];
  if (entry === undefined) return key;
  if (typeof entry === 'object') entry = plural(entry, params?.count);
  if (!params) return entry;
  return entry.replace(/\{(\w+)\}/g, (match, name) => {
    const value = params[name];
    return value === undefined || value === null ? '' : String(value);
  });
}

/** Translates `${prefix}.${value}` when known, otherwise returns the raw value (for server enums). */
export function tEnum(prefix, value) {
  if (value === undefined || value === null || value === '') return '';
  const key = `${prefix}.${value}`;
  return has(key) ? t(key) : String(value);
}

document.documentElement.lang = current;
