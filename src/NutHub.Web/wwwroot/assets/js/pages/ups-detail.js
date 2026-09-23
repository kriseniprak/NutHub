// UPS detail: a header with one Actions menu (instant commands, FSD, driver restart, edit) and tabs: Overview (the
// NutDesk-style gauges), History, Variables, Events and, when NUT clients are logged in, Clients. Summary values
// follow the live stream; variables, commands and clients are refreshed by polling. The open tab is kept in the
// address (?tab=history) so it survives a reload and can be shared.
import { h, replace, setText, seg } from '../dom.js';
import { icon } from '../icons.js';
import { t } from '../i18n.js';
import { api, isAbort, errorText, commandResultText } from '../api.js';
import { hasRole } from '../auth.js';
import { on, findUps, patchUps } from '../store.js';
import { navigate } from '../router.js';
import { pageHeader, loading, errorState, callout } from '../page.js';
import { statusBadge, updateStatusBadge, pill } from '../components/badge.js';
import { menuButton } from '../components/menu.js';
import { confirmDialog } from '../components/dialog.js';
import { toast } from '../components/toast.js';
import { dataTable } from '../components/table.js';
import { tabs } from '../components/tabs.js';
import { eventDays } from '../components/eventlist.js';
import { relTime } from '../format.js';
import { overviewTab } from './ups/overview.js';
import { historySection } from './ups/history.js';
import { variablesSection } from './ups/variables.js';
import { runCommand } from './ups/commands.js';

const DETAIL_REFRESH_MS = 5000;
const EVENT_LIMIT = 30;
const TABS = ['overview', 'history', 'variables', 'events', 'clients'];

export default {
  title: (params) => params.name,
  async render(el, ctx) {
    const name = ctx.params.name;
    el.appendChild(loading());
    let detail;
    try {
      detail = await api.get(`api/ups/${seg(name)}`, { signal: ctx.signal });
    } catch (err) {
      if (isAbort(err)) return;
      el.replaceChildren(err.code === 'notFound'
        ? h('div', { class: 'empty-state' }, h('h1', { text: t('ups.notFoundTitle') }), h('p', { text: t('ups.notFoundText', { name }) }),
          h('a', { class: 'btn btn-primary', href: '#/', text: t('page.backToDashboard') }))
        : errorState(err, () => navigate(`/ups/${encodeURIComponent(name)}`)));
      return;
    }
    el.replaceChildren();
    let summary = findUps(name) ?? detail.summary;
    const upsName = detail.summary.name;
    ctx.setTitle(upsName);

    // ---- Header and the Actions menu ----
    const badge = statusBadge(summary);
    const subtitle = h('span');
    let actionsMenu = null;

    function actionItems() {
      const items = [];
      if (hasRole('operator')) {
        const commands = (detail.commands ?? []).slice().sort((a, b) => a.name.localeCompare(b.name));
        if (commands.length) {
          items.push({ heading: t('ups.commands') });
          for (const c of commands) {
            items.push({ label: c.name, description: c.description, danger: c.dangerous, onSelect: () => runCommand(upsName, c) });
          }
          items.push({ separator: true });
        }
        items.push(summary.forcedShutdown
          ? { label: t('ups.clearFsd'), onSelect: clearFsd }
          : { label: t('ups.setFsd'), description: t('ups.setFsdHint'), danger: true, onSelect: setFsd });
      }
      if (hasRole('admin')) {
        items.push({ separator: true },
          { label: t('ups.restartDriver'), onSelect: restartDriver },
          { label: t('ups.editSettings'), onSelect: () => navigate(`/settings/ups/${encodeURIComponent(upsName)}/edit`) });
      }
      return items;
    }
    if (hasRole('operator')) {
      actionsMenu = menuButton({ label: t('ups.actions'), text: t('ups.actions'), items: actionItems, className: 'actions-button' });
    }
    const header = pageHeader({ title: upsName, badge, subtitle, actions: actionsMenu?.el, back: { href: '#/', label: t('nav.dashboard') } });
    const fsdBanner = h('div', { class: 'fsd-banner', role: 'alert', hidden: true },
      icon('critical'), h('div', {}, h('strong', { text: t('ups.fsdTitle') }), h('p', { text: t('ups.fsdText') })));
    const availabilityBox = h('div', { class: 'availability', hidden: true });

    async function busy(request) {
      if (actionsMenu) actionsMenu.button.disabled = true;
      try {
        await request();
      } finally {
        if (actionsMenu) actionsMenu.button.disabled = false;
      }
    }

    async function setFsd() {
      const ok = await confirmDialog({
        title: t('ups.setFsdTitle', { ups: upsName }),
        message: t('ups.setFsdText', { ups: upsName }),
        confirmLabel: t('ups.setFsd'),
        danger: true,
        confirmText: upsName,
      });
      if (!ok) return;
      await busy(async () => {
        try {
          const result = await api.post(`api/ups/${seg(upsName)}/fsd`);
          toast(result?.ok ? t('ups.fsdSet') : commandResultText(result), { kind: result?.ok ? 'success' : 'error' });
        } catch (err) {
          toast(errorText(err), { kind: 'error' });
        }
      });
    }

    async function clearFsd() {
      const ok = await confirmDialog({
        title: t('ups.clearFsdTitle', { ups: upsName }),
        message: t('ups.clearFsdText'),
        confirmLabel: t('ups.clearFsd'),
      });
      if (!ok) return;
      await busy(async () => {
        try {
          const result = await api.del(`api/ups/${seg(upsName)}/fsd`);
          toast(result?.ok ? t('ups.fsdCleared') : commandResultText(result), { kind: result?.ok ? 'success' : 'error' });
        } catch (err) {
          toast(errorText(err), { kind: 'error' });
        }
      });
    }

    async function restartDriver() {
      const ok = await confirmDialog({
        title: t('ups.restartTitle', { ups: upsName }),
        message: t('ups.restartText'),
        confirmLabel: t('ups.restartDriver'),
      });
      if (!ok) return;
      await busy(async () => {
        try {
          await api.post(`api/admin/ups/${seg(upsName)}/restart`);
          toast(t('ups.restarted'), { kind: 'success' });
        } catch (err) {
          toast(errorText(err), { kind: 'error' });
        }
      });
    }

    // ---- Tabs ----
    const overview = overviewTab();
    const variables = variablesSection(upsName, () => refreshDetail());
    const clientsTable = dataTable({
      caption: t('ups.clients'),
      rowKey: (c) => c.id,
      emptyText: t('ups.noClients'),
      sort: { key: 'address', dir: 'asc' },
      columns: [
        { key: 'address', label: t('clients.address'), render: (c) => h('span', { class: 'mono', text: `${c.address}:${c.port}` }) },
        { key: 'username', label: t('clients.user'), render: (c) => c.username || h('span', { class: 'faint', text: t('clients.anonymous') }) },
        { key: 'primary', label: t('clients.role'), sortValue: (c) => (c.primary ? 0 : 1), render: (c) => pill(c.primary ? t('monitorRole.primary') : t('monitorRole.secondary'), c.primary ? 'primary' : 'neutral') },
        { key: 'tls', label: 'TLS', render: (c) => (c.tls ? pill('TLS', 'ok') : h('span', { class: 'faint', text: t('common.no') })) },
        { key: 'connectedAt', label: t('clients.connected'), render: (c) => relTime(c.connectedAt) },
        { key: 'lastActivity', label: t('clients.lastActivity'), render: (c) => relTime(c.lastActivity) },
        { key: 'commands', label: t('clients.commands'), align: 'end' },
      ],
    });
    const clientsCard = h('section', { class: 'card section' }, h('p', { class: 'section-desc', text: t('ups.clientsHint') }), clientsTable.el);

    function eventsTab() {
      const days = eventDays({ showUps: false });
      const empty = h('p', { class: 'muted events-status', text: t('events.none'), hidden: true });
      const all = h('a', { class: 'btn btn-sm', href: `#/events?ups=${encodeURIComponent(upsName)}` },
        h('span', { text: t('ups.allEvents') }), icon('chevronRight', { className: 'icon-sm' }));
      const card = h('div', { class: 'card events-card' }, days.el, empty, h('div', { class: 'events-more' }, all));
      (async () => {
        try {
          const page = await api.get(`api/events?ups=${encodeURIComponent(upsName)}&limit=${EVENT_LIMIT}`, { signal: ctx.signal });
          days.append(page?.items ?? []);
          empty.hidden = (page?.items ?? []).length > 0;
        } catch (err) {
          if (!isAbort(err)) replace(empty, errorText(err));
          empty.hidden = false;
        }
      })();
      ctx.onCleanup(on('event', (ev) => {
        if (ev.ups !== upsName) return;
        empty.hidden = true;
        days.prepend(ev);
        days.trim(EVENT_LIMIT);
      }));
      return card;
    }

    const requested = ctx.query.get('tab');
    const tabBar = tabs({
      label: t('ups.sections'),
      selected: TABS.includes(requested) ? requested : 'overview',
      items: [
        { id: 'overview', label: t('ups.tab.overview'), render: () => overview.el },
        { id: 'history', label: t('ups.history'), render: () => historySection(upsName, ctx).el },
        { id: 'variables', label: t('ups.variables'), render: () => variables.el },
        { id: 'events', label: t('nav.events'), render: eventsTab },
        { id: 'clients', label: t('ups.clients'), render: () => clientsCard },
      ],
      onChange: (id) => {
        const base = `#/ups/${encodeURIComponent(upsName)}`;
        window.history.replaceState(null, '', id && id !== 'overview' ? `${base}?tab=${id}` : base);
      },
    });

    el.append(header, fsdBanner, availabilityBox, tabBar.el);

    function renderSummary() {
      updateStatusBadge(badge, summary);
      setText(subtitle, [summary.mfr, summary.model].filter(Boolean).join(' ') || summary.description || summary.driverName || '');
      fsdBanner.hidden = !summary.forcedShutdown;
      overview.update(summary, detail);

      const offline = summary.enabled === false || summary.driverState === 'disabled' || summary.availability !== 'available';
      availabilityBox.hidden = !offline;
      if (offline) {
        let title;
        let text;
        if (summary.enabled === false || summary.driverState === 'disabled') {
          title = t('ups.overlay.disabled');
          text = t('ups.disabledText');
        } else if (summary.availability === 'stale') {
          title = t('ups.overlay.stale');
          text = summary.driverMessage || t('ups.overlay.staleHint');
        } else {
          title = t('ups.overlay.noConnection');
          text = summary.driverMessage || t('ups.noConnectionText');
        }
        replace(availabilityBox, callout(summary.enabled === false ? 'info' : 'warning', title, h('p', { text })));
      }
    }

    function applyDetail(d) {
      detail = d;
      variables.update(d);
      const clients = d.clients ?? [];
      clientsTable.setRows(clients);
      tabBar.setVisible('clients', clients.length > 0);
      overview.update(summary, detail);
    }

    renderSummary();
    applyDetail(detail);

    // Live summary from the stream.
    ctx.onCleanup(on('ups', ({ summary: s }) => {
      if (s.name !== upsName) return;
      summary = s;
      renderSummary();
    }));
    ctx.onCleanup(on('upsRemoved', (removed) => {
      if (removed === upsName) {
        toast(t('ups.removed', { name: upsName }), { kind: 'warning' });
        navigate('/');
      }
    }));

    // Polling of the parts the stream does not carry.
    let refreshing = false;
    async function refreshDetail() {
      if (refreshing || ctx.signal.aborted) return;
      refreshing = true;
      try {
        const d = await api.get(`api/ups/${seg(upsName)}`, { signal: ctx.signal, quiet: true });
        if (d?.summary) {
          summary = findUps(upsName) ?? d.summary;
          patchUps(d.summary);
        }
        applyDetail(d);
      } catch (err) {
        if (!isAbort(err) && err.code === 'notFound') navigate('/');
      } finally {
        refreshing = false;
      }
    }
    const timer = setInterval(() => {
      if (document.visibilityState === 'visible') refreshDetail();
    }, Math.max(DETAIL_REFRESH_MS, (detail.pollIntervalSeconds ?? 2) * 1000));
    ctx.onCleanup(() => clearInterval(timer));
  },
};
