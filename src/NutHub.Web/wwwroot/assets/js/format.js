// Locale-aware formatting of numbers, units, durations and dates, following the active panel language.
import { locale, t } from './i18n.js';
import { h } from './dom.js';

const numberFormats = new Map();
const dateFormats = new Map();
let relativeFormat = null;
let relativeLocale = null;

function nf(maxDigits, minDigits) {
  const key = `${locale()}|${maxDigits}|${minDigits}`;
  let f = numberFormats.get(key);
  if (!f) {
    f = new Intl.NumberFormat(locale(), { maximumFractionDigits: maxDigits, minimumFractionDigits: minDigits });
    numberFormats.set(key, f);
  }
  return f;
}

function df(options) {
  const key = `${locale()}|${JSON.stringify(options)}`;
  let f = dateFormats.get(key);
  if (!f) {
    f = new Intl.DateTimeFormat(locale(), options);
    dateFormats.set(key, f);
  }
  return f;
}

/** True for a usable finite number. */
export function isNum(v) {
  return typeof v === 'number' && Number.isFinite(v);
}

/** Formats a number; returns an en dash for unknown values. */
export function num(v, maxDigits = 1, minDigits = 0) {
  if (!isNum(v)) return '–';
  return nf(maxDigits, Math.min(minDigits, maxDigits)).format(v);
}

/** Formats a number with its unit ("231.2 V", "34 %"). */
export function unit(v, u, maxDigits = 1) {
  if (!isNum(v)) return '–';
  if (!u) return num(v, maxDigits);
  return `${num(v, maxDigits)} ${u}`;
}

/** Formats a percentage (value already in percent). */
export function pct(v, maxDigits = 0) {
  return unit(v, '%', maxDigits);
}

/** Formats a duration in seconds for people: "45 s", "27 min", "1 h 05 min", "3 d 4 h". */
export function duration(seconds) {
  if (!isNum(seconds)) return '–';
  const s = Math.max(0, Math.round(seconds));
  if (s < 60) return t('fmt.seconds', { n: num(s, 0) });
  const totalMinutes = Math.floor(s / 60);
  if (totalMinutes < 60) return t('fmt.minutes', { n: num(totalMinutes, 0) });
  const totalHours = Math.floor(totalMinutes / 60);
  if (totalHours < 48) {
    const m = totalMinutes % 60;
    return m ? t('fmt.hoursMinutes', { h: num(totalHours, 0), m: String(m).padStart(2, '0') })
      : t('fmt.hours', { n: num(totalHours, 0) });
  }
  const d = Math.floor(totalHours / 24);
  const hh = totalHours % 24;
  return hh ? t('fmt.daysHours', { d: num(d, 0), h: num(hh, 0) }) : t('fmt.days', { n: num(d, 0) });
}

/** Formats a countdown "m:ss" / "h:mm:ss". */
export function countdown(seconds) {
  if (!isNum(seconds)) return '–';
  const s = Math.max(0, Math.ceil(seconds));
  const hh = Math.floor(s / 3600);
  const mm = Math.floor((s % 3600) / 60);
  const ss = s % 60;
  const two = (n) => String(n).padStart(2, '0');
  return hh ? `${hh}:${two(mm)}:${two(ss)}` : `${mm}:${two(ss)}`;
}

/** Formats a byte count. */
export function bytes(n) {
  if (!isNum(n)) return '–';
  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB'];
  let v = n;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i += 1;
  }
  return unit(v, units[i], i === 0 ? 0 : 1);
}

function toDate(value) {
  if (value instanceof Date) return value;
  if (value === null || value === undefined || value === '') return null;
  const d = new Date(value);
  return Number.isNaN(d.getTime()) ? null : d;
}

/** Date and time, medium style. */
export function dateTime(value) {
  const d = toDate(value);
  return d ? df({ dateStyle: 'medium', timeStyle: 'medium' }).format(d) : '–';
}

/** Short date and time for axes and compact tables. */
export function dateTimeShort(value) {
  const d = toDate(value);
  return d ? df({ month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }).format(d) : '–';
}

/** Time of day. */
export function timeOfDay(value, withSeconds = false) {
  const d = toDate(value);
  if (!d) return '–';
  return df(withSeconds ? { hour: '2-digit', minute: '2-digit', second: '2-digit' }
    : { hour: '2-digit', minute: '2-digit' }).format(d);
}

/** Full date with the weekday, for day headings ("Monday, 21 September 2026"). */
export function longDate(value) {
  const d = toDate(value);
  return d ? df({ weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' }).format(d) : '–';
}

/** Day and month for axes. */
export function dayMonth(value) {
  const d = toDate(value);
  return d ? df({ month: 'short', day: 'numeric' }).format(d) : '–';
}

/** Relative time ("3 s ago", "in 2 min"). */
export function relative(value, now = Date.now()) {
  const d = toDate(value);
  if (!d) return '–';
  if (!relativeFormat || relativeLocale !== locale()) {
    relativeLocale = locale();
    relativeFormat = new Intl.RelativeTimeFormat(relativeLocale, { numeric: 'always', style: 'short' });
  }
  const diff = (d.getTime() - now) / 1000;
  const abs = Math.abs(diff);
  if (abs < 2) return t('time.justNow');
  if (abs < 60) return relativeFormat.format(Math.round(diff), 'second');
  if (abs < 3600) return relativeFormat.format(Math.round(diff / 60), 'minute');
  if (abs < 86400) return relativeFormat.format(Math.round(diff / 3600), 'hour');
  if (abs < 86400 * 45) return relativeFormat.format(Math.round(diff / 86400), 'day');
  return dateTimeShort(d);
}

/**
 * A <time> element showing relative time, kept current by the shell's once-a-second ticker
 * (see refreshRelativeTimes) with the absolute time as tooltip.
 */
export function relTime(value, className) {
  const d = toDate(value);
  const el = h('time', { class: ['reltime', className], dataset: { rel: d ? String(d.getTime()) : '' } });
  if (d) {
    el.dateTime = d.toISOString();
    el.title = dateTime(d);
    el.textContent = relative(d);
  } else {
    el.textContent = '–';
  }
  return el;
}

/** Updates a relTime element to a new value. */
export function setRelTime(el, value) {
  const d = toDate(value);
  const ms = d ? String(d.getTime()) : '';
  if (el.dataset.rel === ms) return;
  el.dataset.rel = ms;
  el.dateTime = d ? d.toISOString() : '';
  el.title = d ? dateTime(d) : '';
  el.textContent = d ? relative(d) : '–';
}

/** Refreshes every relative time in the document (called once a second). */
export function refreshRelativeTimes(root = document) {
  const now = Date.now();
  for (const el of root.querySelectorAll('time.reltime[data-rel]')) {
    const ms = Number(el.dataset.rel);
    if (!ms) continue;
    const text = relative(new Date(ms), now);
    if (el.textContent !== text) el.textContent = text;
  }
}

/** The unit of a NUT variable, from its name, for display and chart axes. */
export function varUnit(name) {
  if (!name) return '';
  if (/(^|\.)(charge|load)(\.|$)/.test(name) && !name.endsWith('.low') && !name.includes('restart')) return '%';
  if (/\.charge\.(low|warning|restart)$/.test(name)) return '%';
  if (/\.voltage(\.|$)/.test(name)) return 'V';
  if (/\.current(\.|$)/.test(name)) return 'A';
  if (/\.frequency(\.|$)/.test(name)) return 'Hz';
  if (/\.temperature(\.|$)/.test(name)) return '°C';
  if (/\.humidity(\.|$)/.test(name)) return '%';
  if (/\.realpower(\.|$)/.test(name)) return 'W';
  if (/\.power(\.|$)/.test(name)) return 'VA';
  if (/\.(runtime|delay)(\.|$)/.test(name) || name.endsWith('.timer') || name.includes('.timer.')) return 's';
  return '';
}

/** Formats a NUT variable value with its unit when numeric. */
export function varValue(name, raw) {
  if (raw === null || raw === undefined) return '–';
  const u = varUnit(name);
  const n = Number(raw);
  if (u && raw !== '' && Number.isFinite(n)) {
    if (u === 's' && /runtime/.test(name)) return `${duration(n)} (${num(n, 0)} s)`;
    return unit(n, u, 2);
  }
  return String(raw);
}
