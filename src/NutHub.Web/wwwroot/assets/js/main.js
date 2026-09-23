// Entry point: loads who we are, builds the shell, declares the routes and keeps the live stream in step with
// the session (started when the visitor may read, stopped on sign-out, re-checked when the stream fails).
import { h, replace } from './dom.js';
import { t, onLangChange } from './i18n.js';
import { auth, refreshAuth, onAuthChange, canRead, hasRole } from './auth.js';
import { onSessionProblem, errorText } from './api.js';
import { state, startStream, stopStream, loadOverview, reconnectNow, on } from './store.js';
import { addRoute, startRouter, navigate, refresh, currentLocation, setTitleSuffix } from './router.js';
import { createShell } from './shell.js';
import { refreshRelativeTimes } from './format.js';
import { confirmDialog } from './components/dialog.js';
import { toast } from './components/toast.js';
import { onThemeChange } from './theme.js';

const page = (path) => () => import(path);
const settings = { role: 'admin', area: 'settings' };

addRoute('/login', page('./pages/login.js'), { public: true, bare: true });
addRoute('/', page('./pages/dashboard.js'), { area: 'dashboard', wide: true });
addRoute('/ups/:name', page('./pages/ups-detail.js'), { area: 'dashboard', wide: true });
addRoute('/events', page('./pages/events.js'), { area: 'events' });
addRoute('/account', page('./pages/account.js'), { role: 'self' });
addRoute('/settings', page('./pages/settings.js'), { ...settings, home: true });
addRoute('/settings/ups', page('./pages/admin/ups-list.js'), settings);
addRoute('/settings/ups/new', page('./pages/admin/ups-edit.js'), settings);
addRoute('/settings/ups/:name/edit', page('./pages/admin/ups-edit.js'), settings);
addRoute('/settings/import', page('./pages/admin/import.js'), settings);
addRoute('/settings/web-users', page('./pages/admin/web-users.js'), settings);
addRoute('/settings/nut-users', page('./pages/admin/nut-users.js'), settings);
addRoute('/settings/nut', page('./pages/admin/nut-server.js'), settings);
addRoute('/settings/web', page('./pages/admin/web-settings.js'), settings);
addRoute('/settings/identity', page('./pages/admin/server-identity.js'), settings);
addRoute('/settings/notifications', page('./pages/admin/notifications.js'), settings);
addRoute('/settings/host-protection', page('./pages/admin/host-protection.js'), settings);
addRoute('/settings/history', page('./pages/admin/history-settings.js'), settings);
addRoute('/settings/system', page('./pages/admin/system.js'), settings);
addRoute('/settings/logs', page('./pages/admin/logs.js'), { ...settings, wide: true });

// The administration pages used to live under #/admin: bookmarks and links in old notifications keep working.
const moved = (from, to) => addRoute(from, null, { redirect: to });
moved('/admin', '/settings');
moved('/admin/ups/new', '/settings/ups/new');
moved('/admin/ups/:name/edit', (p) => `/settings/ups/${encodeURIComponent(p.name)}/edit`);
moved('/admin/server', '/settings/identity');
for (const name of ['ups', 'import', 'nut', 'nut-users', 'web-users', 'notifications', 'host-protection', 'history', 'web',
  'system', 'logs']) {
  moved(`/admin/${name}`, `/settings/${name}`);
}

function loginRedirect(location) {
  const next = location?.raw && !location.raw.startsWith('/login') ? location.raw : '/';
  return { redirect: next === '/' ? '/login' : `/login?next=${encodeURIComponent(next)}` };
}

/** Route guard: sign-in, forced password change and roles. */
function guard(route, location) {
  const mustChange = !!auth.user?.mustChangePassword;
  if (route.public) {
    if (route.pattern === '/login' && auth.authenticated) {
      if (mustChange) return { redirect: '/account' };
      const next = location.query.get('next');
      return { redirect: next && next.startsWith('/') && !next.startsWith('/login') ? next : '/' };
    }
    return null;
  }
  if (mustChange && route.pattern !== '/account') return { redirect: '/account' };
  if (route.role === 'self') return auth.authenticated ? null : loginRedirect(location);
  if (!auth.authenticated && !auth.anonymousRead) return loginRedirect(location);
  const role = route.role ?? 'viewer';
  if (role === 'viewer') return null;
  if (!auth.authenticated) return loginRedirect(location);
  return hasRole(role) ? null : { forbidden: true };
}

let shell = null;
let authCheckTimer = null;

function syncStream() {
  if (canRead()) {
    startStream(onStreamError);
    if (!state.loaded) loadOverview().catch(() => {});
  } else {
    stopStream();
  }
}

/** Stream errors happen on network loss and when the session expired: check which one (debounced). */
function onStreamError() {
  clearTimeout(authCheckTimer);
  authCheckTimer = setTimeout(() => {
    refreshAuth().catch(() => {});
  }, 1500);
}

let lastAuth = null;
function handleAuthChange() {
  const wasAuthenticated = lastAuth?.authenticated;
  lastAuth = { authenticated: auth.authenticated, user: auth.user?.name };
  setTitleSuffix(auth.serverName);
  shell?.render();
  syncStream();
  const location = currentLocation();
  if (wasAuthenticated && !auth.authenticated && !auth.signedOutByUser && !location.path.startsWith('/login')) {
    toast(t('auth.sessionExpired'), { kind: 'warning' });
  }
  if (auth.authenticated) auth.signedOutByUser = false;
  refresh();
}

function fatal(root, err) {
  replace(root, h('div', { class: 'boot-screen boot-error', role: 'alert' },
    h('img', { src: 'assets/img/logo.svg', alt: '', width: 56, height: 56 }),
    h('p', { class: 'boot-name', text: t('boot.unreachable') }),
    h('p', { class: 'muted', text: errorText(err) }),
    h('button', { type: 'button', class: 'btn btn-primary', text: t('common.retry'), onClick: () => boot() })));
}

async function boot() {
  const root = document.getElementById('app');
  try {
    await refreshAuth();
  } catch (err) {
    fatal(root, err);
    return;
  }
  lastAuth = { authenticated: auth.authenticated, user: auth.user?.name };
  setTitleSuffix(auth.serverName);
  shell = createShell(root);
  onAuthChange(handleAuthChange);
  onSessionProblem((err) => {
    if (err.code === 'unauthorized' || err.code === 'passwordChangeRequired') onStreamError();
  });
  on('overview', () => setTitleSuffix(state.server?.name || auth.serverName));
  onLangChange(() => {
    shell.render();
    refresh();
  });
  onThemeChange(() => shell.render());
  syncStream();
  startRouter({
    element: shell.main,
    guard,
    onRoute: (route, location) => shell.setRoute(route, location),
    confirmLeave: () => confirmDialog({
      title: t('dirty.title'),
      message: t('dirty.text'),
      confirmLabel: t('dirty.leave'),
      danger: true,
    }),
  });
  setInterval(() => refreshRelativeTimes(), 1000);
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible' && canRead()) {
      reconnectNow();
      refreshAuth().catch(() => {});
    }
  });
}

// Exposed for pages that must leave to another route after an action.
export { navigate };

boot();
