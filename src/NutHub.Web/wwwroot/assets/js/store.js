// Live state of the server: the overview (server info and UPS summaries) kept current by the server-sent event
// stream GET api/stream. Pages subscribe with on(type, fn) and never open their own streams.
//
// Emitted types: overview (full resync), ups (one summary changed), upsRemoved, event (EventDto), clients,
// hostProtection, connection ("connecting" | "open" | "reconnecting" | "closed").
import { api } from './api.js';

export const state = {
  loaded: false,
  server: null,
  ups: [],
  clients: { total: 0, byUps: {} },
  hostProtection: null,
  connection: 'closed',
  lastMessageAt: 0,
};

const listeners = new Map();

/** Subscribes to a store event; returns the unsubscribe function. */
export function on(type, fn) {
  let set = listeners.get(type);
  if (!set) {
    set = new Set();
    listeners.set(type, set);
  }
  set.add(fn);
  return () => set.delete(fn);
}

function emit(type, payload) {
  const set = listeners.get(type);
  if (!set) return;
  for (const fn of [...set]) {
    try {
      fn(payload);
    } catch (err) {
      console.error(`store listener for "${type}" failed`, err);
    }
  }
}

/** Finds a UPS summary by name (case-insensitive, like upsd). */
export function findUps(name) {
  if (!name) return null;
  const lower = String(name).toLowerCase();
  return state.ups.find((u) => u.name === name) ?? state.ups.find((u) => u.name.toLowerCase() === lower) ?? null;
}

function applyOverview(overview) {
  if (!overview || !Array.isArray(overview.ups)) return;
  state.loaded = true;
  state.server = overview.server ?? null;
  state.hostProtection = overview.server?.hostProtection ?? state.hostProtection;
  state.ups = overview.ups.slice();
  const byUps = {};
  for (const u of state.ups) byUps[u.name] = u.clients ?? 0;
  state.clients = { total: overview.server?.nut?.clients ?? 0, byUps };
  emit('overview', state);
}

function applyUps(summary) {
  if (!summary?.name) return;
  const index = state.ups.findIndex((u) => u.name === summary.name);
  if (index >= 0) {
    const previous = state.ups[index];
    // Updates can arrive out of order around a resync; never go back to an older snapshot of the same driver run.
    if (typeof previous.sequence === 'number' && typeof summary.sequence === 'number'
        && summary.sequence < previous.sequence && summary.lastUpdate && previous.lastUpdate
        && summary.lastUpdate < previous.lastUpdate) {
      return;
    }
    state.ups[index] = summary;
    emit('ups', { summary, added: false });
  } else {
    state.ups.push(summary);
    emit('ups', { summary, added: true });
  }
  if (typeof summary.clients === 'number') state.clients.byUps[summary.name] = summary.clients;
}

function applyRemoved(payload) {
  const name = payload?.name;
  const index = state.ups.findIndex((u) => u.name === name);
  if (index < 0) return;
  state.ups.splice(index, 1);
  delete state.clients.byUps[name];
  emit('upsRemoved', name);
}

function applyClients(payload) {
  if (!payload) return;
  state.clients = { total: payload.total ?? 0, byUps: payload.byUps ?? {} };
  if (state.server?.nut) state.server.nut.clients = state.clients.total;
  for (const u of state.ups) {
    const n = state.clients.byUps[u.name];
    u.clients = typeof n === 'number' ? n : 0;
  }
  emit('clients', state.clients);
}

function applyHostProtection(payload) {
  if (!payload) return;
  state.hostProtection = payload;
  if (state.server) state.server.hostProtection = payload;
  emit('hostProtection', payload);
}

/** Loads the overview with a plain request (first paint, or when the stream is down). */
let pendingOverview = null;
export async function loadOverview(options) {
  // Concurrent callers (shell start-up, first page) share one request.
  if (!pendingOverview) {
    pendingOverview = api.get('api/overview', { quiet: options?.quiet })
      .finally(() => {
        pendingOverview = null;
      });
  }
  const overview = await pendingOverview;
  if (options?.signal?.aborted) throw new DOMException('Aborted', 'AbortError');
  applyOverview(overview);
  return state;
}

/** Updates the local copy of a UPS summary after an action, before the stream confirms it. */
export function patchUps(summary) {
  applyUps(summary);
}

// ---- Stream -------------------------------------------------------------------------------------------------

const RECONNECT_DELAYS = [2000, 5000, 10000, 30000];
/** The server pings every 15 s: silence for this long means a dead connection that the browser did not notice. */
const WATCHDOG_MS = 45000;

let source = null;
let reconnectTimer = null;
let reconnectAttempt = 0;
let watchdog = null;
let wanted = false;
let errorHandler = null;

function setConnection(value) {
  if (state.connection === value) return;
  state.connection = value;
  emit('connection', value);
}

function parse(event) {
  state.lastMessageAt = Date.now();
  try {
    return JSON.parse(event.data);
  } catch {
    return null;
  }
}

function listen(es, type, handler) {
  es.addEventListener(type, (event) => {
    if (es !== source) return;
    const data = parse(event);
    if (data !== null) handler(data);
  });
}

function armWatchdog() {
  clearInterval(watchdog);
  watchdog = setInterval(() => {
    if (!source || state.connection !== 'open') return;
    if (Date.now() - state.lastMessageAt > WATCHDOG_MS) restart();
  }, 5000);
}

function restart() {
  closeSource();
  scheduleReconnect(0);
}

function closeSource() {
  if (source) {
    source.close();
    source = null;
  }
}

function scheduleReconnect(delay) {
  clearTimeout(reconnectTimer);
  if (!wanted) return;
  const wait = delay ?? RECONNECT_DELAYS[Math.min(reconnectAttempt, RECONNECT_DELAYS.length - 1)];
  reconnectAttempt += 1;
  reconnectTimer = setTimeout(open, wait);
}

function open() {
  if (!wanted) return;
  closeSource();
  setConnection(state.loaded ? 'reconnecting' : 'connecting');
  const es = new EventSource('api/stream');
  source = es;
  state.lastMessageAt = Date.now();
  es.onopen = () => {
    if (es !== source) return;
    reconnectAttempt = 0;
    state.lastMessageAt = Date.now();
    setConnection('open');
  };
  es.onerror = () => {
    if (es !== source) return;
    setConnection('reconnecting');
    // The stream ends when the session expires: let the shell check who we are now.
    if (errorHandler) errorHandler();
    if (es.readyState === EventSource.CLOSED) {
      // The browser gave up (HTTP error answer); retry on our own schedule.
      closeSource();
      scheduleReconnect();
    }
  };
  listen(es, 'overview', applyOverview);
  listen(es, 'ups', applyUps);
  listen(es, 'upsRemoved', applyRemoved);
  listen(es, 'event', (e) => emit('event', e));
  listen(es, 'clients', applyClients);
  listen(es, 'hostProtection', applyHostProtection);
  listen(es, 'ping', () => {});
  armWatchdog();
}

/** Opens the stream (idempotent). `onError` runs on every stream error (used to re-check the session). */
export function startStream(onError) {
  errorHandler = onError ?? errorHandler;
  if (wanted && source) return;
  wanted = true;
  reconnectAttempt = 0;
  open();
}

/** Closes the stream (sign-out, forced password change). */
export function stopStream() {
  wanted = false;
  clearTimeout(reconnectTimer);
  clearInterval(watchdog);
  closeSource();
  setConnection('closed');
}

/** Reconnects immediately (for example when the tab becomes visible again after the stream died). */
export function reconnectNow() {
  if (!wanted) return;
  if (source && source.readyState !== EventSource.CLOSED && state.connection === 'open') return;
  reconnectAttempt = 0;
  open();
}
