// UPS devices: the configured UPSes with their live state, one clean row each. The name opens the settings of the
// UPS; the "..." menu holds the rest (enable or disable, move up or down, restart the driver, delete). The order can
// also be changed by dragging a row.
import { h, replace, seg } from '../../dom.js';
import { icon } from '../../icons.js';
import { t } from '../../i18n.js';
import { api, isAbort, errorText } from '../../api.js';
import { on, findUps } from '../../store.js';
import { navigate } from '../../router.js';
import { pageHeader, loading, errorState } from '../../page.js';
import { statusBadge, updateStatusBadge, pill } from '../../components/badge.js';
import { itemRow, itemList, rowMenu } from '../../components/itemlist.js';
import { confirmDialog } from '../../components/dialog.js';
import { toast } from '../../components/toast.js';
import { loadDrivers } from './common.js';

/** The body sent back on update: the config without the read-only secretsSet. */
export function configBody(config) {
  const { secretsSet, ...rest } = config;
  return rest;
}

const editPath = (name) => `/settings/ups/${encodeURIComponent(name)}/edit`;

export default {
  title: () => t('nav.upsDevices'),
  async render(el, ctx) {
    el.append(pageHeader({
      title: t('nav.upsDevices'),
      subtitle: t('upsAdmin.subtitle'),
      actions: h('a', { class: 'btn btn-primary', href: '#/settings/ups/new' }, icon('plus'), h('span', { text: t('dashboard.addUps') })),
    }));
    const body = h('div', {}, loading());
    el.appendChild(body);

    let configs;
    let drivers = [];
    try {
      [configs, drivers] = await Promise.all([
        api.get('api/admin/ups', { signal: ctx.signal }),
        loadDrivers(ctx.signal).catch(() => []),
      ]);
    } catch (err) {
      if (isAbort(err)) return;
      replace(body, errorState(err, () => navigate('/settings/ups')));
      return;
    }

    const driverName = (id) => drivers.find((d) => d.id === id)?.displayName ?? id;
    const list = itemList(t('nav.upsDevices'));
    const hint = h('p', { class: 'muted small list-hint', text: t('upsAdmin.orderHint') });
    const badges = new Map();
    let dragName = null;

    function row(config, index) {
      const summary = findUps(config.name);
      const badge = summary && config.enabled ? statusBadge(summary) : pill(config.enabled ? t('upsState.starting') : t('upsState.disabled'));
      badges.set(config.name, badge);
      const menu = rowMenu(config.name, () => [
        { label: t('upsAdmin.open'), onSelect: () => navigate(`/ups/${encodeURIComponent(config.name)}`) },
        { label: t('common.edit'), onSelect: () => navigate(editPath(config.name)) },
        { separator: true },
        { label: config.enabled ? t('upsAdmin.disable') : t('upsAdmin.enable'), onSelect: () => setEnabled(config, !config.enabled) },
        { label: t('upsAdmin.moveUp', { name: config.name }), disabled: index === 0, onSelect: () => move(index, index - 1, config.name) },
        { label: t('upsAdmin.moveDown', { name: config.name }), disabled: index === configs.length - 1, onSelect: () => move(index, index + 1, config.name) },
        { label: t('ups.restartDriver'), disabled: !config.enabled, onSelect: () => restart(config) },
        { separator: true },
        { label: t('common.delete'), danger: true, onSelect: () => remove(config) },
      ]);
      const li = itemRow({
        title: config.name,
        href: `#${editPath(config.name)}`,
        meta: [config.description, driverName(config.driver)].filter(Boolean).join(' · '),
        side: badge,
        menu,
        lead: h('span', { class: 'drag-handle', title: t('upsAdmin.dragHint'), 'aria-hidden': 'true' }, icon('grip')),
        className: !config.enabled && 'is-disabled',
        dataset: { name: config.name },
      });
      li.draggable = true;
      li.addEventListener('dragstart', (event) => {
        dragName = config.name;
        li.classList.add('dragging');
        event.dataTransfer.effectAllowed = 'move';
        event.dataTransfer.setData('text/plain', config.name);
      });
      li.addEventListener('dragend', () => {
        dragName = null;
        li.classList.remove('dragging');
        for (const x of list.querySelectorAll('.drop-before, .drop-after')) x.classList.remove('drop-before', 'drop-after');
      });
      li.addEventListener('dragover', (event) => {
        if (!dragName || dragName === config.name) return;
        event.preventDefault();
        const rect = li.getBoundingClientRect();
        const after = event.clientY > rect.top + rect.height / 2;
        li.classList.toggle('drop-after', after);
        li.classList.toggle('drop-before', !after);
      });
      li.addEventListener('dragleave', () => li.classList.remove('drop-before', 'drop-after'));
      li.addEventListener('drop', (event) => {
        event.preventDefault();
        const after = li.classList.contains('drop-after');
        li.classList.remove('drop-before', 'drop-after');
        const from = configs.findIndex((c) => c.name === dragName);
        let to = configs.findIndex((c) => c.name === config.name);
        if (from < 0 || to < 0) return;
        if (after) to += 1;
        if (from < to) to -= 1;
        move(from, to);
      });
      return li;
    }

    function render(focusName) {
      badges.clear();
      if (!configs.length) {
        replace(body, h('div', { class: 'empty-state card' },
          icon('battery', { className: 'empty-icon' }),
          h('h2', { text: t('dashboard.emptyTitle') }),
          h('p', { text: t('upsAdmin.empty') }),
          h('div', { class: 'row' },
            h('a', { class: 'btn btn-primary', href: '#/settings/ups/new' }, icon('plus'), h('span', { text: t('dashboard.addUps') })),
            h('a', { class: 'btn', href: '#/settings/import' }, h('span', { text: t('nav.import') })))));
        return;
      }
      replace(list, configs.map(row));
      replace(body, h('div', { class: 'card list-card' }, list), hint);
      // After a move from the menu, keep the keyboard on the moved row.
      if (focusName) list.querySelector(`li[data-name="${CSS.escape(focusName)}"] .item-menu button`)?.focus();
    }

    async function move(from, to, focusName) {
      if (to < 0 || to >= configs.length || from === to) return;
      const previous = configs.slice();
      const [item] = configs.splice(from, 1);
      configs.splice(to, 0, item);
      render(focusName);
      try {
        await api.put('api/admin/ups-order', { names: configs.map((c) => c.name) });
      } catch (err) {
        configs = previous;
        render();
        toast(errorText(err), { kind: 'error' });
      }
    }

    async function setEnabled(config, enabled) {
      try {
        const updated = await api.put(`api/admin/ups/${seg(config.name)}`, { ...configBody(config), enabled });
        Object.assign(config, updated ?? { enabled });
        toast(enabled ? t('upsAdmin.enabledToast', { name: config.name }) : t('upsAdmin.disabledToast', { name: config.name }), { kind: 'success' });
        render(config.name);
      } catch (err) {
        toast(errorText(err), { kind: 'error' });
      }
    }

    async function restart(config) {
      try {
        await api.post(`api/admin/ups/${seg(config.name)}/restart`);
        toast(t('ups.restarted'), { kind: 'success' });
      } catch (err) {
        toast(errorText(err), { kind: 'error' });
      }
    }

    async function remove(config) {
      const ok = await confirmDialog({
        title: t('upsAdmin.deleteTitle', { name: config.name }),
        message: t('upsAdmin.deleteText', { name: config.name }),
        confirmLabel: t('common.delete'),
        danger: true,
      });
      if (!ok) return;
      try {
        await api.del(`api/admin/ups/${seg(config.name)}`);
        configs = configs.filter((c) => c !== config);
        toast(t('upsAdmin.deleted', { name: config.name }), { kind: 'success' });
        render();
      } catch (err) {
        toast(errorText(err), { kind: 'error' });
      }
    }

    render();
    ctx.onCleanup(on('ups', ({ summary }) => {
      const badge = badges.get(summary.name);
      if (badge?.classList.contains('status-badge')) updateStatusBadge(badge, summary);
      else if (badge && configs.find((c) => c.name === summary.name)?.enabled) render();
    }));
  },
};
