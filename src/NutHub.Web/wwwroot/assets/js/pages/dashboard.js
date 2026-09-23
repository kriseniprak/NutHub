// Dashboard: the server name, one attention banner only when something needs it, and the UPSes in one of three views
// chosen with a switch and remembered per browser: Cards (the essentials, default), List (a compact sortable table
// for many UPSes) and Detailed (richer cards with voltages, temperature and an hourly sparkline). The counters and
// the NUT server details live in the status pill of the app bar.
import { h, replace, setText, seg } from '../dom.js';
import { icon } from '../icons.js';
import { t } from '../i18n.js';
import { api, isAbort } from '../api.js';
import { state, on, loadOverview } from '../store.js';
import { auth, hasRole } from '../auth.js';
import { navigate, link } from '../router.js';
import { pageHeader, loading, errorState } from '../page.js';
import { upsCard, upsDetailCard } from '../components/upscard.js';
import { segmented } from '../components/segmented.js';
import { dataTable } from '../components/table.js';
import { countdown, duration, pct, unit, relTime, isNum } from '../format.js';
import { upsStatusText, upsFlags, hostStateText, severityText, isOffline } from '../labels.js';
import { PENDING_STATES, cancelShutdown } from '../shell.js';

const VIEWS = ['cards', 'list', 'detailed'];
const VIEW_KEY = 'nuthub.dashboardView';
const SPARK_REFRESH_MS = 5 * 60 * 1000;
const SEVERITY_RANK = { critical: 0, warning: 1, info: 2, ok: 3, offline: 4 };

function storedView() {
  try {
    const v = window.localStorage.getItem(VIEW_KEY);
    return VIEWS.includes(v) ? v : 'cards';
  } catch {
    return 'cards';
  }
}

function storeView(view) {
  try {
    window.localStorage.setItem(VIEW_KEY, view);
  } catch {
    // Only a convenience: the view still changes for this visit.
  }
}

/** UPS conditions worth a banner: forced shutdown, critical, on battery. */
function upsAttention() {
  const items = [];
  for (const u of state.ups) {
    if (u.enabled === false || (u.availability !== 'available' && !u.forcedShutdown)) continue;
    const runtime = u.battery?.runtime;
    if (u.forcedShutdown || upsFlags(u).includes('FSD')) {
      items.push({ level: 'critical', ups: u.name, text: t('attention.fsd', { ups: u.name }) });
    } else if (u.severity === 'critical') {
      items.push({ level: 'critical', ups: u.name, text: t('attention.critical', { ups: u.name, status: upsStatusText(u) }) });
    } else if (upsFlags(u).includes('OB')) {
      items.push({
        level: 'warning',
        ups: u.name,
        text: isNum(runtime) ? t('attention.onBatteryRuntime', { ups: u.name, time: duration(runtime) }) : t('attention.onBattery', { ups: u.name }),
      });
    }
  }
  return items.sort((a, b) => (a.level === b.level ? 0 : a.level === 'critical' ? -1 : 1));
}

/** The attention banner; built once so its Cancel button keeps the focus across live updates. */
function attentionBanner() {
  const badgeIcon = h('span', { class: 'attention-icon' });
  const title = h('p', { class: 'attention-title' });
  const text = h('p', { class: 'attention-text' });
  const list = h('ul', { class: 'attention-list' });
  const cancel = h('button', { type: 'button', class: 'btn btn-sm', hidden: true }, h('span', { text: t('hp.cancel') }));
  cancel.addEventListener('click', () => cancelShutdown(cancel));
  const el = h('div', { class: 'attention', hidden: true },
    badgeIcon, h('div', { class: 'attention-body' }, title, text, list), h('div', { class: 'attention-actions' }, cancel));
  const countdownEl = h('strong', { class: 'attention-countdown' });
  let signature = null;
  let hp = null;

  const upsLink = (item) => h('a', { href: link('/ups', item.ups), text: item.text });

  function tick() {
    if (!hp?.shutdownAt) {
      setText(countdownEl, '');
      return;
    }
    setText(countdownEl, t('hp.banner.in', { time: countdown((Date.parse(hp.shutdownAt) - Date.now()) / 1000) }));
  }

  function render() {
    const current = state.hostProtection;
    hp = current && PENDING_STATES.has(current.state) ? current : null;
    const items = upsAttention();
    const next = JSON.stringify([hp?.state, hp?.reason, hp?.shutdownAt, hp?.dryRun, items]);
    if (next === signature) return;
    signature = next;
    el.hidden = !hp && !items.length;
    if (el.hidden) return;
    const critical = hp?.state === 'shuttingDown' || items.some((i) => i.level === 'critical');
    el.className = `attention sev-${critical ? 'critical' : 'warning'}`;
    replace(badgeIcon, icon(critical ? 'critical' : 'warning'));
    let rest = items;
    if (hp) {
      setText(title, hp.dryRun ? t('hp.banner.titleDryRun') : t('hp.banner.title'));
      replace(text, `${hostStateText(hp.state)}${hp.reason ? ` — ${hp.reason}` : ''} `, countdownEl);
      text.hidden = false;
    } else {
      replace(title, upsLink(items[0]));
      text.hidden = true;
      rest = items.slice(1);
    }
    replace(list, rest.map((item) => h('li', {}, upsLink(item))));
    list.hidden = !rest.length;
    cancel.hidden = !hp || hp.state === 'shuttingDown' || !hasRole('operator');
    tick();
  }

  return { el, render, tick };
}

function listTable() {
  const table = dataTable({
    caption: t('dashboard.listCaption'),
    rowKey: (u) => u.name,
    emptyText: t('dashboard.emptyTitle'),
    // Rows without live data stay readable but step back, like the greyed cards.
    rowClass: (u) => ['is-clickable', (u.enabled === false || isOffline(u)) && 'is-offline'],
    columns: [
      {
        key: 'severity',
        label: t('dashboard.state'),
        hideLabel: true,
        sortValue: (u) => SEVERITY_RANK[u.severity] ?? 5,
        render: (u) => h('span', { class: ['status-dot', `sev-${u.severity || 'offline'}`], role: 'img', 'aria-label': severityText(u.severity || 'offline') }),
      },
      { key: 'name', label: t('common.name'), render: (u) => h('a', { class: 'cell-link', href: link('/ups', u.name), text: u.name }) },
      { key: 'status', label: t('dashboard.state'), sortValue: (u) => upsStatusText(u), render: (u) => upsStatusText(u) },
      { key: 'charge', label: t('metric.charge'), align: 'end', sortValue: (u) => u.battery?.charge, render: (u) => pct(u.battery?.charge) },
      { key: 'runtime', label: t('metric.runtime'), align: 'end', sortValue: (u) => u.battery?.runtime, render: (u) => duration(u.battery?.runtime) },
      { key: 'load', label: t('metric.load'), align: 'end', render: (u) => pct(u.load) },
      { key: 'inputVoltage', label: t('metric.inputVoltage'), align: 'end', render: (u) => unit(u.inputVoltage, 'V', 0) },
      { key: 'lastUpdate', label: t('dashboard.lastUpdate'), align: 'end', render: (u) => relTime(u.lastUpdate) },
    ],
  });
  // The whole row opens the UPS; the name stays a real link for the keyboard and for "open in a new tab".
  table.el.addEventListener('click', (event) => {
    if (event.target.closest('a, button')) return;
    const tr = event.target.closest('tbody tr[data-key]');
    if (tr) navigate(`/ups/${encodeURIComponent(tr.dataset.key)}`);
  });
  return table;
}

export default {
  title: () => t('nav.dashboard'),
  async render(el, ctx) {
    let view = storedView();
    const header = h('div');
    const attention = attentionBanner();
    const content = h('div', { class: 'dash-view' });
    const body = h('div', { class: 'dashboard' }, header, attention.el, content);
    el.appendChild(body);

    if (!state.loaded) {
      content.appendChild(loading());
      try {
        await loadOverview({ signal: ctx.signal });
      } catch (err) {
        if (isAbort(err)) return;
        el.replaceChildren(errorState(err, () => {
          el.replaceChildren();
          this.render(el, ctx);
        }));
        return;
      }
    }

    const switcher = segmented({
      label: t('dashboard.view'),
      options: [
        { value: 'cards', label: t('dashboard.viewCards') },
        { value: 'list', label: t('dashboard.viewList') },
        { value: 'detailed', label: t('dashboard.viewDetailed') },
      ],
      value: view,
      size: 'sm',
      onChange: (value) => {
        view = value;
        storeView(value);
        mount();
      },
    });
    const addLink = hasRole('admin')
      ? h('a', { class: 'btn btn-ghost btn-sm', href: '#/settings/ups/new' }, icon('plus'), h('span', { text: t('dashboard.addUps') }))
      : null;

    function renderHeader() {
      const server = state.server ?? {};
      replace(header, pageHeader({
        title: server.name || auth.serverName || 'NutHub',
        subtitle: server.location || null,
        actions: state.ups.length ? [addLink, switcher.el] : null,
      }));
    }

    function emptyState() {
      return h('div', { class: 'empty-state card' },
        icon('battery', { className: 'empty-icon' }),
        h('h2', { text: t('dashboard.emptyTitle') }),
        h('p', { text: hasRole('admin') ? t('dashboard.emptyAdmin') : t('dashboard.emptyViewer') }),
        hasRole('admin') ? h('a', { class: 'btn btn-primary', href: '#/settings/ups/new' }, icon('plus'), h('span', { text: t('dashboard.addUps') })) : null);
    }

    // ---- Views ----
    const cards = new Map();
    let grid = null;
    let table = null;
    let sparkTimer = null;

    function mount() {
      cards.clear();
      grid = null;
      table = null;
      clearInterval(sparkTimer);
      sparkTimer = null;
      content.replaceChildren();
      if (!state.ups.length) {
        content.appendChild(emptyState());
        return;
      }
      if (view === 'list') {
        table = listTable();
        content.appendChild(h('div', { class: 'card table-card ups-list' }, table.el));
      } else {
        grid = h('div', { class: ['ups-grid', view === 'detailed' && 'ups-grid--detailed'] });
        content.appendChild(grid);
        if (view === 'detailed') sparkTimer = setInterval(refreshSparks, SPARK_REFRESH_MS);
      }
      reconcile();
    }

    function reconcile() {
      if (!state.ups.length) {
        if (!content.querySelector('.empty-state')) mount();
        return;
      }
      if (content.querySelector('.empty-state')) {
        mount();
        return;
      }
      if (table) {
        table.setRows(state.ups);
        return;
      }
      const names = new Set(state.ups.map((u) => u.name));
      for (const [name, card] of cards) {
        if (!names.has(name)) {
          card.el.remove();
          cards.delete(name);
        }
      }
      state.ups.forEach((u, index) => {
        let card = cards.get(u.name);
        if (!card) {
          card = view === 'detailed' ? upsDetailCard(u) : upsCard(u);
          cards.set(u.name, card);
          if (view === 'detailed') loadSpark(u.name);
        } else {
          card.update(u);
        }
        if (grid.children[index] !== card.el) grid.insertBefore(card.el, grid.children[index] ?? null);
      });
    }

    function updateOne(summary) {
      if (table) table.setRows(state.ups);
      else cards.get(summary.name)?.update(summary);
    }

    async function loadSpark(name) {
      try {
        const history = await api.get(`api/ups/${seg(name)}/history?range=1h&vars=battery.charge,ups.load`, { signal: ctx.signal, quiet: true });
        cards.get(name)?.setHistory?.(history?.series ?? {});
      } catch (err) {
        if (!isAbort(err)) cards.get(name)?.setHistory?.({});
      }
    }

    function refreshSparks() {
      let delay = 0;
      for (const name of cards.keys()) {
        // Spread the requests so a large installation does not burst the server.
        setTimeout(() => {
          if (!ctx.signal.aborted && view === 'detailed') loadSpark(name);
        }, delay);
        delay += 250;
      }
    }

    renderHeader();
    attention.render();
    mount();

    ctx.onCleanup(on('overview', () => {
      renderHeader();
      attention.render();
      reconcile();
    }));
    ctx.onCleanup(on('ups', ({ summary, added }) => {
      if (added) {
        renderHeader();
        reconcile();
      } else {
        updateOne(summary);
      }
      attention.render();
    }));
    ctx.onCleanup(on('upsRemoved', () => {
      renderHeader();
      reconcile();
      attention.render();
    }));
    ctx.onCleanup(on('hostProtection', () => attention.render()));
    const ticker = setInterval(() => attention.tick(), 1000);
    ctx.onCleanup(() => {
      clearInterval(ticker);
      clearInterval(sparkTimer);
    });
  },
};
