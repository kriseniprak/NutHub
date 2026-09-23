// Application frame: a top app bar (brand, the main navigation as a few text tabs, one overall status pill with its
// summary popover, the user menu), the secondary navigation of the settings area, and the host-protection banner
// that stays visible while a shutdown is pending (on every page but the dashboard, whose attention banner shows it).
import { h, setText, replace } from './dom.js';
import { icon } from './icons.js';
import { t, LANGUAGES, lang, setLang } from './i18n.js';
import { auth, hasRole, canRead, displayName, logout } from './auth.js';
import { state, on } from './store.js';
import { themeMode, setThemeMode } from './theme.js';
import { menuButton } from './components/menu.js';
import { popover } from './components/popover.js';
import { toast } from './components/toast.js';
import { confirmDialog } from './components/dialog.js';
import { api, errorText } from './api.js';
import { countdown } from './format.js';
import { roleText, hostStateText, overallStatus, upsCounts } from './labels.js';
import { navigate } from './router.js';
import { SETTINGS_GROUPS, settingsItem } from './settings-nav.js';

const NAV = [
  { area: 'dashboard', path: '/', key: 'nav.dashboard' },
  { area: 'events', path: '/events', key: 'nav.events' },
  { area: 'settings', path: '/settings', key: 'nav.settings', role: 'admin', icon: 'settings' },
];

export const PENDING_STATES = new Set(['pending', 'waitingForSecondaries', 'shuttingDown']);

/** Builds the shell inside `root`; returns { main, setRoute(route, location), render() }. */
export function createShell(root) {
  const main = h('main', { id: 'main', class: 'content', tabindex: '-1' });
  const brandName = h('span', { class: 'brand-name' });
  const brand = h('a', { class: 'brand', href: '#/' }, h('img', { src: 'assets/img/logo.svg', alt: '', width: 28, height: 28 }), brandName);
  const tabs = h('nav', { class: 'appnav' });
  const compactNav = h('div', { class: 'appnav-compact' });
  const statusArea = h('div', { class: 'status-area' });
  const userArea = h('div', { class: 'user-area' });
  const appbar = h('header', { class: 'appbar' },
    h('div', { class: 'appbar-inner' }, brand, tabs, compactNav, h('div', { class: 'appbar-spacer' }), statusArea, userArea));
  const banner = h('div', { class: 'hp-banner', hidden: true, role: 'alert' });
  const settingsNav = h('nav', { class: 'settings-nav', hidden: true });
  const frame = h('div', { class: 'frame' }, settingsNav, main);
  const skip = h('a', { class: 'skip-link', href: '#main' });
  skip.addEventListener('click', (event) => {
    event.preventDefault();
    main.focus();
  });
  const app = h('div', { class: 'app' }, skip, appbar, banner, frame);
  replace(root, app);

  let currentPath = '/';
  let currentRoute = null;
  let bare = false;

  function visibleNav() {
    return NAV.filter((item) => !item.role || hasRole(item.role));
  }

  // ---- Main navigation: text tabs, a compact menu on phones ----
  function renderNav() {
    const area = currentRoute?.area ?? null;
    const items = canRead() && !bare ? visibleNav() : [];
    replace(tabs, items.map((item) => h('a', {
      class: ['appnav-link', item.area === area && 'active'],
      href: `#${item.path}`,
      'aria-current': item.area === area ? 'page' : null,
    }, item.icon ? icon(item.icon, { className: 'icon-sm' }) : null, h('span', { text: t(item.key) }))));
    tabs.setAttribute('aria-label', t('nav.label'));
    const current = items.find((item) => item.area === area);
    if (!items.length) {
      replace(compactNav);
      return;
    }
    const menu = menuButton({
      label: t('nav.label'),
      text: current ? t(current.key) : t('nav.menu'),
      align: 'start',
      buttonClass: 'btn btn-ghost appnav-button',
      items: () => items.map((item) => ({ label: t(item.key), checked: item.area === area, onSelect: () => navigate(item.path) })),
    });
    replace(compactNav, menu.el);
  }

  // ---- Overall status pill and its summary ----
  const pillDot = h('span', { class: 'status-dot', 'aria-hidden': 'true' });
  const pillText = h('span', { class: 'status-text' });
  const pillLive = h('span', { class: 'sr-only' });
  const pillButton = h('button', { type: 'button', class: 'status-pill' }, pillDot, pillText, pillLive);
  const summary = popover({ button: pillButton, render: summaryContent, label: t('status.summary'), className: 'status-pop' });
  statusArea.appendChild(summary.el);

  function connectionText() {
    const status = state.connection;
    if (status === 'open') return t('status.liveOn');
    if (status === 'connecting') return t('conn.connecting');
    if (status === 'reconnecting') return t('conn.reconnecting');
    return t('conn.offline');
  }

  function renderStatus() {
    statusArea.hidden = bare || !canRead();
    if (statusArea.hidden) {
      summary.close(false);
      return;
    }
    const overall = overallStatus(state.ups, state.loaded);
    pillButton.className = `status-pill lvl-${overall.level}`;
    setText(pillText, overall.text);
    const live = state.connection === 'open';
    pillDot.classList.toggle('is-stale', !live);
    pillDot.classList.toggle('is-pending', state.connection === 'connecting' || state.connection === 'reconnecting');
    // The dot turns grey while the live stream is down; the tooltip and the hidden text say why.
    const note = live ? '' : t('status.streamDown', { state: connectionText() });
    pillButton.title = note || t('status.details');
    setText(pillLive, note ? `. ${note}` : '');
    summary.panel.setAttribute('aria-label', t('status.summary'));
    summary.refresh();
  }

  function counter(value, label, sev) {
    return h('div', { class: ['status-counter', value > 0 && `sev-${sev}`] },
      h('span', { class: 'status-counter-value', text: String(value) }),
      h('span', { class: 'status-counter-label', text: label }));
  }

  function summaryContent() {
    const overall = overallStatus(state.ups, state.loaded);
    const c = upsCounts(state.ups);
    const server = state.server ?? {};
    const nut = server.nut ?? {};
    const hp = state.hostProtection ?? server.hostProtection;
    const nutText = nut.enabled
      ? (nut.endpoints?.length ? nut.endpoints.join(', ') : t('dashboard.nutNoEndpoint'))
      : t('dashboard.nutDisabled');
    const row = (label, value) => h('div', { class: 'status-fact' }, h('dt', { text: label }), h('dd', {}, value));
    return [
      h('p', { class: 'status-pop-title', text: overall.text }),
      h('div', { class: 'status-counters' },
        counter(c.online, t('dashboard.online'), 'ok'),
        counter(c.onBattery, t('dashboard.onBattery'), 'warning'),
        counter(c.critical, t('dashboard.critical'), 'critical'),
        counter(c.offline, t('dashboard.offline'), 'offline')),
      h('dl', { class: 'status-facts' },
        row(t('status.live'), connectionText()),
        row(t('dashboard.nutServer'), h('span', { class: 'mono', text: `${nutText}${nut.tls ? ' · TLS' : ''}` })),
        row(t('dashboard.nutClients'), String(state.clients.total ?? nut.clients ?? 0)),
        row(t('dashboard.hostProtection'), hp ? `${hostStateText(hp.state)}${hp.dryRun ? ` · ${t('hp.dryRunPill')}` : ''}` : '–')),
      nut.lastError ? h('p', { class: 'status-pop-error' }, icon('warning', { className: 'icon-sm' }), h('span', { text: nut.lastError })) : null,
      hasRole('admin') ? h('a', { class: 'status-pop-link', href: '#/settings/nut', text: t('status.serverSettings') }) : null,
    ];
  }

  // ---- User menu ----
  function languageItems() {
    return [
      { heading: t('menu.language') },
      ...LANGUAGES.map((l) => ({ label: l.name, checked: lang() === l.code, onSelect: () => setLang(l.code) })),
      { heading: t('menu.theme') },
      ...['system', 'light', 'dark'].map((mode) => ({
        label: t(`theme.${mode}`),
        checked: themeMode() === mode,
        onSelect: () => setThemeMode(mode),
      })),
    ];
  }

  function renderUser() {
    if (auth.authenticated && auth.user) {
      const name = displayName();
      const initials = name.split(/\s+/).map((p) => p[0]).join('').slice(0, 2).toUpperCase() || '?';
      const menu = menuButton({
        label: t('menu.user', { name }),
        buttonClass: 'user-button',
        items: () => [
          { content: h('div', { class: 'menu-user' }, h('strong', { text: name }), h('span', { text: `${auth.user.name} · ${roleText(auth.user.role)}` })) },
          { separator: true },
          { label: t('menu.account'), icon: 'user', onSelect: () => navigate('/account') },
          { separator: true },
          ...languageItems(),
          { separator: true },
          { label: t('menu.signOut'), icon: 'logout', onSelect: signOut },
        ],
      });
      menu.button.prepend(h('span', { class: 'avatar', 'aria-hidden': 'true', text: initials }),
        h('span', { class: 'user-name', text: name }));
      replace(userArea, menu.el);
    } else {
      const prefs = menuButton({ label: t('menu.preferences'), iconName: 'sliders', buttonClass: 'btn btn-icon btn-ghost', items: languageItems });
      const children = [prefs.el];
      if (!bare) {
        children.push(h('a', { class: 'btn btn-primary btn-sm', href: signInHref() }, h('span', { text: t('auth.signIn') })));
      }
      replace(userArea, children);
    }
  }

  function signInHref() {
    const next = window.location.hash.replace(/^#/, '') || '/';
    return next.startsWith('/login') ? '#/login' : `#/login?next=${encodeURIComponent(next)}`;
  }

  async function signOut() {
    try {
      await logout();
      toast(t('auth.signedOut'), { kind: 'success' });
    } catch (err) {
      toast(errorText(err), { kind: 'error' });
    }
    navigate('/login');
  }

  // ---- Settings navigation: a list on the left on wide screens, a link back to the list on phones ----
  function renderSettingsNav() {
    const inSettings = currentRoute?.area === 'settings' && !currentRoute.home && hasRole('admin');
    settingsNav.hidden = !inSettings;
    frame.classList.toggle('frame--settings', inSettings);
    if (!inSettings) {
      replace(settingsNav);
      return;
    }
    const active = settingsItem(currentPath);
    // Below a list page (a UPS form) the phone link leads back to that list, otherwise to the settings home.
    const parent = active && active.path !== currentPath ? active : null;
    const back = h('a', { class: 'settings-back', href: parent ? `#${parent.path}` : '#/settings' },
      icon('chevronLeft', { className: 'icon-sm' }), h('span', { text: parent ? t(parent.key) : t('nav.settings') }));
    const groups = SETTINGS_GROUPS.map((group) => h('div', { class: 'settings-group' },
      h('p', { class: 'settings-group-title', text: t(group.key) }),
      group.items.map((item) => h('a', {
        class: ['settings-link', active === item && 'active'],
        href: `#${item.path}`,
        'aria-current': active === item && item.path === currentPath ? 'page' : null,
      }, t(item.key)))));
    replace(settingsNav, back, h('div', { class: 'settings-list' }, groups));
    settingsNav.setAttribute('aria-label', t('nav.settings'));
  }

  // ---- Host protection banner with a live countdown ----
  let bannerTimer = null;
  function renderBanner() {
    clearInterval(bannerTimer);
    const hp = state.hostProtection;
    if (!hp || !PENDING_STATES.has(hp.state) || bare || currentPath === '/') {
      banner.hidden = true;
      return;
    }
    const countdownEl = h('strong', { class: 'hp-countdown' });
    const tick = () => {
      if (!hp.shutdownAt) {
        setText(countdownEl, '');
        return;
      }
      const seconds = (Date.parse(hp.shutdownAt) - Date.now()) / 1000;
      setText(countdownEl, t('hp.banner.in', { time: countdown(seconds) }));
    };
    const cancel = hasRole('operator') && hp.state !== 'shuttingDown'
      ? h('button', { type: 'button', class: 'btn btn-sm' }, h('span', { text: t('hp.cancel') }))
      : null;
    cancel?.addEventListener('click', () => cancelShutdown(cancel));
    replace(banner, h('div', { class: 'hp-banner-inner' },
      icon('shield'),
      h('div', { class: 'hp-banner-text' },
        h('strong', { text: hp.dryRun ? t('hp.banner.titleDryRun') : t('hp.banner.title') }),
        h('span', { text: `${hostStateText(hp.state)}${hp.reason ? ` — ${hp.reason}` : ''}` }),
        countdownEl),
      cancel));
    banner.className = `hp-banner hp-${hp.state}`;
    banner.hidden = false;
    tick();
    bannerTimer = setInterval(tick, 1000);
  }

  function renderBrand() {
    setText(brandName, state.server?.name || auth.serverName || 'NutHub');
    brand.setAttribute('aria-label', t('nav.home', { name: brandName.textContent }));
  }

  function render() {
    renderBrand();
    skip.textContent = t('nav.skip');
    app.classList.toggle('app--bare', bare);
    renderNav();
    renderStatus();
    renderUser();
    renderSettingsNav();
    renderBanner();
  }

  on('connection', renderStatus);
  on('ups', renderStatus);
  on('upsRemoved', renderStatus);
  on('clients', () => summary.refresh());
  on('hostProtection', () => {
    renderBanner();
    summary.refresh();
  });
  on('overview', () => {
    renderBrand();
    renderStatus();
    renderBanner();
  });

  render();

  return {
    main,
    render,
    setRoute(route, location) {
      currentPath = location?.path ?? '/';
      currentRoute = route ?? null;
      bare = !!route?.bare;
      frame.className = ['frame', route?.wide && 'frame--wide', bare && 'frame--bare'].filter(Boolean).join(' ');
      summary.close(false);
      render();
    },
  };
}

/** Asks for confirmation, then cancels a pending host shutdown (operator or admin). */
export async function cancelShutdown(buttonEl) {
  const ok = await confirmDialog({
    title: t('hp.cancelTitle'),
    message: t('hp.cancelText'),
    confirmLabel: t('hp.cancel'),
  });
  if (!ok) return;
  if (buttonEl) buttonEl.disabled = true;
  try {
    const result = await api.post('api/host-protection/cancel');
    toast(result?.ok ? t('hp.cancelled') : t('hp.cancelNotPossible'), { kind: result?.ok ? 'success' : 'warning' });
  } catch (err) {
    toast(errorText(err), { kind: 'error' });
  } finally {
    if (buttonEl) buttonEl.disabled = false;
  }
}
