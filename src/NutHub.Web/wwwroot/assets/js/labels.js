// Human-readable, localised texts derived from server data: UPS status, event sentences, enums.
import { t, has, tEnum, lang } from './i18n.js';
import { duration, num } from './format.js';

/** Display order of ups.status tokens: the power source first, then the battery, then conditions. */
const FLAG_ORDER = ['FSD', 'OL', 'OB', 'LB', 'HB', 'RB', 'CHRG', 'DISCHRG', 'BYPASS', 'CAL', 'OFF', 'OVER',
  'TRIM', 'BOOST', 'ALARM', 'TEST'];

function flagRank(flag) {
  const i = FLAG_ORDER.indexOf(flag);
  return i < 0 ? FLAG_ORDER.length : i;
}

/** The flags of a summary (from `flags`, or parsed from `status`). */
export function upsFlags(u) {
  if (Array.isArray(u?.flags)) return u.flags;
  return String(u?.status ?? '').split(/\s+/).filter(Boolean);
}

/** Localised status parts of a UPS: ["On line", "Charging"], or the driver state when there is no data. */
export function upsStatusParts(u) {
  if (!u) return [];
  if (u.enabled === false || u.driverState === 'disabled') return [t('upsState.disabled')];
  if (u.availability === 'driverNotConnected') return [tEnum('driverState', u.driverState || 'disconnected')];
  const parts = [...upsFlags(u)]
    .sort((a, b) => flagRank(a) - flagRank(b))
    .map((flag) => (has(`flag.${flag}`) ? t(`flag.${flag}`) : flag));
  if (u.availability === 'stale') parts.push(t('upsState.stale'));
  if (!parts.length) parts.push(t('upsState.unknown'));
  return parts;
}

/** "On line · Charging". */
export function upsStatusText(u) {
  return upsStatusParts(u).join(' · ');
}

/** The one status word a small card has room for: stale data first, otherwise the power source. */
export function upsStatusShort(u) {
  if (u?.availability === 'stale' && u.enabled !== false) return t('upsState.stale');
  return upsStatusParts(u)[0] ?? '';
}

/** Whether the UPS runs on battery according to its last known flags. */
export function isOnBattery(u) {
  return upsFlags(u).includes('OB');
}

/** Whether a UPS gives no live data (driver down or stale), leaving out the ones disabled on purpose. */
export function isOffline(u) {
  if (u?.enabled === false || u?.driverState === 'disabled') return false;
  return u?.severity === 'offline' || (u?.availability ?? 'available') !== 'available';
}

/** Counters of the status summary: on line power, on battery, critical, offline. */
export function upsCounts(list) {
  const c = { online: 0, onBattery: 0, critical: 0, offline: 0 };
  for (const u of list) {
    if (isOffline(u)) {
      c.offline += 1;
      continue;
    }
    if (u.severity === 'critical') c.critical += 1;
    const flags = upsFlags(u);
    if (flags.includes('OB')) c.onBattery += 1;
    else if (flags.includes('OL')) c.online += 1;
  }
  return c;
}

/**
 * The overall state for the status pill of the app bar: { level, text }, the worst colour first (critical, then
 * on battery or needing attention, then offline).
 */
export function overallStatus(list, loaded) {
  if (!loaded) return { level: 'offline', text: t('status.loading') };
  if (!list.length) return { level: 'offline', text: t('status.noUps') };
  const live = list.filter((u) => !isOffline(u) && u.enabled !== false);
  const critical = live.filter((u) => u.severity === 'critical');
  if (critical.length === 1) return { level: 'critical', text: t('status.criticalOne', { ups: critical[0].name }) };
  if (critical.length) return { level: 'critical', text: t('status.criticalMany', { count: critical.length }) };
  const onBattery = live.filter(isOnBattery).length;
  if (onBattery) return { level: 'warning', text: t('status.onBattery', { count: onBattery }) };
  const attention = live.filter((u) => u.severity === 'warning').length;
  if (attention) return { level: 'warning', text: t('status.attention', { count: attention }) };
  const offline = list.filter(isOffline).length;
  if (offline) return { level: 'offline', text: t('status.offline', { count: offline }) };
  if (!live.length) return { level: 'offline', text: t('status.noneActive') };
  return { level: 'ok', text: t('status.allOnline') };
}

/** Localised severity name (UPS and event severities). */
export function severityText(severity) {
  return tEnum('severity', severity);
}

/** Localised event category name. */
export function categoryText(category) {
  return tEnum('category', category);
}

/** Localised short name of an event type ("On battery"). */
export function eventTypeText(type) {
  return tEnum('evType', type);
}

/**
 * Event types whose server message carries details that are not in `data` (a driver error, a login address...):
 * in other languages the original message is shown under the translated sentence.
 */
const DETAIL_IN_MESSAGE = new Set(['communicationLost', 'noCommunication', 'driverFailed', 'driverStarted',
  'driverStopped', 'alarm', 'shutdownPending', 'shutdownCancelled', 'shutdownStarted', 'serverStarted',
  'serverStopping', 'configurationChanged', 'userLogin', 'userLoginFailed', 'nutClientLogin', 'nutClientLogout',
  'forcedShutdown', 'forcedShutdownCleared', 'notificationTest']);

function readings(data) {
  const parts = [];
  const charge = Number(data['battery.charge']);
  if (data['battery.charge'] !== undefined && Number.isFinite(charge)) {
    parts.push(t('ev.detail.charge', { value: num(charge, 0) }));
  }
  const runtime = Number(data['battery.runtime']);
  if (data['battery.runtime'] !== undefined && Number.isFinite(runtime)) {
    parts.push(t('ev.detail.runtime', { value: duration(runtime) }));
  }
  return parts;
}

/**
 * The text of an event: { text, detail }. English uses the server's sentence (it has every detail); other
 * languages build the sentence from the type and data, and keep the original as detail when it says more.
 */
export function eventText(ev) {
  const fallback = { text: ev?.message || ev?.type || '', detail: null };
  if (!ev || lang() === 'en') return fallback;
  const data = ev.data ?? {};
  let key = `ev.${ev.type}`;
  if (ev.type === 'commandFailed' && data.variable && !data.command) key = 'ev.variableFailed';
  if (!has(key)) return fallback;

  const command = data.parameter ? `${data.command ?? ''} ${data.parameter}` : data.command ?? '';
  let text = t(key, {
    ups: ev.ups ?? '',
    command,
    variable: data.variable ?? '',
    value: data.value ?? '',
    actor: ev.actor ?? '',
    error: data.error ?? '',
  });
  if (['onBattery', 'online', 'lowBattery'].includes(ev.type)) {
    const extra = readings(data);
    if (extra.length) text += ` (${extra.join(', ')})`;
  }
  if ((ev.type === 'commandFailed') && data.error) text += `: ${data.error}`;
  const detail = DETAIL_IN_MESSAGE.has(ev.type) && ev.message && ev.message !== text ? ev.message : null;
  return { text, detail };
}

/** Localised NUT monitor role. */
export function monitorRoleText(role) {
  return tEnum('monitorRole', role);
}

/** Localised web role. */
export function roleText(role) {
  return tEnum('role', role);
}

/** Localised host protection state. */
export function hostStateText(state) {
  return tEnum('hpState', state);
}

/** Severity-like class for a host protection state. */
export function hostStateSeverity(state) {
  switch (state) {
    case 'monitoring': return 'ok';
    case 'pending':
    case 'waitingForSecondaries': return 'warning';
    case 'shuttingDown': return 'critical';
    case 'dryRunCompleted': return 'info';
    default: return 'offline';
  }
}
