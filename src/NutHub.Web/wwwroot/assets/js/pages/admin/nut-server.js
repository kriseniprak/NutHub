// NUT server settings (listeners, access, limits, TLS), the live list of connected NUT clients and a help box
// for configuring upsmon / NutDesk against this server.
import { h, replace, seg, debounce } from '../../dom.js';
import { icon } from '../../icons.js';
import { t } from '../../i18n.js';
import { api, isAbort, errorText } from '../../api.js';
import { state, on } from '../../store.js';
import { navigate } from '../../router.js';
import { pageHeader, section, loading, errorState, callout } from '../../page.js';
import {
  field, textInput, numberInput, switchField, formErrorBox, setFieldError, secretControl, clearErrors,
} from '../../components/fields.js';
import { listEditor, validateCidr } from '../../components/listeditor.js';
import { dataTable } from '../../components/table.js';
import { confirmDialog } from '../../components/dialog.js';
import { toast } from '../../components/toast.js';
import { pill } from '../../components/badge.js';
import { relTime } from '../../format.js';
import {
  loadSettings, trackDirty, saveBar, advancedGroup, saveWithErrors, saveButton, checkInt, intValue,
} from './common.js';

function listenEditor(endpoints) {
  const rows = h('div', { class: 'listen-rows' });
  const add = h('button', { type: 'button', class: 'btn btn-sm' }, icon('plus'), h('span', { text: t('nut.addEndpoint') }));
  function renumber() {
    [...rows.children].forEach((row, i) => {
      row.querySelector('.listen-address').dataset.field = `listen[${i}].address`;
      row.querySelector('.listen-port').dataset.field = `listen[${i}].port`;
    });
  }
  function addRow(ep = { address: '*', port: 3493 }) {
    const address = textInput({ value: ep.address ?? '', placeholder: '*', 'aria-label': t('nut.address'), className: 'input-mono' });
    const port = numberInput({ value: ep.port ?? 3493, min: 1, max: 65535, step: 1, 'aria-label': t('nut.port') });
    const remove = h('button', { type: 'button', class: 'btn btn-icon btn-ghost', 'aria-label': t('common.remove'), title: t('common.remove') }, icon('trash'));
    const row = h('div', { class: 'listen-row' },
      h('div', { class: 'field listen-address' }, address),
      h('div', { class: 'field listen-port' }, port),
      remove);
    remove.addEventListener('click', () => {
      row.remove();
      renumber();
      rows.dispatchEvent(new Event('change', { bubbles: true }));
    });
    rows.appendChild(row);
    renumber();
    return address;
  }
  add.addEventListener('click', () => addRow().focus());
  for (const ep of endpoints ?? []) addRow(ep);
  const el = h('div', { class: 'listen-editor' },
    h('div', { class: 'listen-head', 'aria-hidden': 'true' }, h('span', { text: t('nut.address') }), h('span', { text: t('nut.port') })),
    rows, add);
  return {
    el,
    values() {
      return [...rows.children].map((row) => ({
        address: row.querySelector('.listen-address input').value.trim() || '*',
        port: intValue(row.querySelector('.listen-port input')),
      }));
    },
    validate() {
      let ok = true;
      for (const row of rows.children) {
        const portField = row.querySelector('.listen-port');
        const message = checkInt(portField.querySelector('input'), { min: 1, max: 65535 });
        setFieldError(portField, message);
        if (message) ok = false;
      }
      return ok;
    },
  };
}

function clientsSection(ctx) {
  const table = dataTable({
    caption: t('nut.clients'),
    rowKey: (c) => c.id,
    emptyText: t('nut.noClients'),
    sort: { key: 'connectedAt', dir: 'desc' },
    columns: [
      { key: 'address', label: t('clients.address'), render: (c) => h('span', { class: 'mono', text: `${c.address}:${c.port}` }) },
      { key: 'username', label: t('clients.user'), render: (c) => c.username || h('span', { class: 'faint', text: t('clients.anonymous') }) },
      { key: 'loginUps', label: t('clients.ups'), render: (c) => (c.loginUps ? h('a', { href: `#/ups/${encodeURIComponent(c.loginUps)}`, text: c.loginUps }) : '–') },
      { key: 'primary', label: t('clients.role'), sortValue: (c) => (c.primary ? 0 : 1), render: (c) => (c.loginUps ? pill(c.primary ? t('monitorRole.primary') : t('monitorRole.secondary'), c.primary ? 'primary' : 'neutral') : '–') },
      { key: 'tls', label: 'TLS', render: (c) => (c.tls ? pill('TLS', 'ok') : h('span', { class: 'faint', text: t('common.no') })) },
      { key: 'connectedAt', label: t('clients.connected'), sortValue: (c) => c.connectedAt, render: (c) => relTime(c.connectedAt) },
      { key: 'lastActivity', label: t('clients.lastActivity'), sortValue: (c) => c.lastActivity, render: (c) => relTime(c.lastActivity) },
      { key: 'commands', label: t('clients.commands'), align: 'end' },
      {
        key: 'actions',
        label: t('common.actions'),
        hideLabel: true,
        sortable: false,
        align: 'end',
        render: (c) => {
          const b = h('button', { type: 'button', class: 'btn btn-sm' }, icon('xCircle'), h('span', { text: t('nut.disconnect') }));
          b.addEventListener('click', () => disconnect(c, b));
          return h('div', { class: 'cell-actions' }, b);
        },
      },
    ],
  });
  const refreshButton = h('button', { type: 'button', class: 'btn btn-sm', 'aria-label': t('common.refresh') }, icon('refresh'), h('span', { text: t('common.refresh') }));
  const status = h('p', { class: 'muted small', role: 'status' });
  const el = section({ title: t('nut.clients'), description: t('nut.clientsHint'), actions: refreshButton }, status, table.el);

  async function load() {
    try {
      table.setRows(await api.get('api/admin/clients', { signal: ctx.signal }));
      status.textContent = '';
    } catch (err) {
      if (!isAbort(err)) status.textContent = errorText(err);
    }
  }
  async function disconnect(c, button) {
    const ok = await confirmDialog({
      title: t('nut.disconnectTitle'),
      message: t('nut.disconnectText', { address: `${c.address}:${c.port}`, user: c.username || t('clients.anonymous') }),
      confirmLabel: t('nut.disconnect'),
      danger: !!c.primary,
    });
    if (!ok) return;
    button.disabled = true;
    try {
      await api.del(`api/admin/clients/${seg(c.id)}`);
      toast(t('nut.disconnected'), { kind: 'success' });
      load();
    } catch (err) {
      toast(errorText(err), { kind: 'error' });
      button.disabled = false;
    }
  }
  refreshButton.addEventListener('click', load);
  const debounced = debounce(load, 600);
  ctx.onCleanup(on('clients', debounced));
  const timer = setInterval(() => {
    if (document.visibilityState === 'visible') load();
  }, 15000);
  ctx.onCleanup(() => {
    clearInterval(timer);
    debounced.cancel();
  });
  load();
  return el;
}

function helpBox(settings) {
  const host = window.location.hostname && !['localhost', '127.0.0.1', '::1', '[::1]'].includes(window.location.hostname)
    ? window.location.hostname : t('nut.thisHost');
  const port = settings.listen?.[0]?.port ?? 3493;
  const ups = state.ups[0]?.name ?? 'ups';
  const target = port === 3493 ? `${ups}@${host}` : `${ups}@${host}:${port}`;
  const line = `MONITOR ${target} 1 <user> <password> secondary`;
  const copy = h('button', { type: 'button', class: 'btn btn-sm', 'aria-label': t('common.copy') }, icon('copy'), h('span', { text: t('common.copy') }));
  copy.addEventListener('click', async () => {
    try {
      await navigator.clipboard.writeText(line);
      toast(t('common.copied'), { kind: 'success' });
    } catch {
      toast(t('common.copyFailed'), { kind: 'warning' });
    }
  });
  // Setup help is needed once per client: it stays folded until asked for.
  return advancedGroup(t('nut.helpTitle'), section({ className: 'help-box' },
    h('p', { text: t('nut.helpUpsmon') }),
    h('div', { class: 'code-line' }, h('pre', {}, h('code', { text: line })), copy),
    h('ul', { class: 'help-list' },
      h('li', { text: t('nut.helpRoles') }),
      h('li', { text: t('nut.helpAccount') }),
      h('li', { text: t('nut.helpNutDesk', { host, port }) }),
      h('li', { text: t('nut.helpFirewall', { port }) })),
    h('p', {}, h('a', { href: '#/settings/nut-users' }, t('nut.manageAccounts')))));
}

export default {
  title: () => t('nav.nutServer'),
  async render(el, ctx) {
    el.append(pageHeader({ title: t('nav.nutServer'), subtitle: t('nut.subtitle') }));
    const body = h('div', {}, loading());
    el.appendChild(body);
    let settings;
    try {
      settings = (await loadSettings(ctx.signal)).nut;
    } catch (err) {
      if (isAbort(err)) return;
      replace(body, errorState(err, () => navigate('/settings/nut')));
      return;
    }

    const errors = formErrorBox();
    const enabled = switchField({ label: t('nut.enabled'), checked: settings.enabled, name: 'enabled', help: t('nut.enabledHelp') });
    const listen = listenEditor(settings.listen);
    const networks = listEditor({
      values: settings.allowedNetworks,
      placeholder: '192.168.1.0/24',
      label: t('nut.network'),
      name: 'allowedNetworks',
      validate: validateCidr,
      addLabel: t('nut.addNetwork'),
    });
    const maxConn = numberInput({ value: settings.maxConnections, min: 1, max: 100000, step: 1 });
    const maxAge = numberInput({ value: settings.maxAgeSeconds, min: 1, max: 3600, step: 1 });
    const fsdDelay = numberInput({ value: settings.fsdClearDelaySeconds, min: 0, max: 86400, step: 1 });
    const version = textInput({ value: settings.versionString ?? '', placeholder: t('nut.versionDefault'), autocomplete: 'off' });
    const tls = settings.tls ?? {};
    const tlsEnabled = switchField({ label: t('nut.tlsEnabled'), checked: tls.enabled, name: 'tls.enabled', help: t('nut.tlsEnabledHelp') });
    const certPath = textInput({ value: tls.certificatePath ?? '', placeholder: t('nut.certPlaceholder'), className: 'input-mono', autocomplete: 'off' });
    const certPassword = secretControl({ isSet: !!tls.certificatePasswordSet });
    const requireTls = switchField({ label: t('nut.requireTls'), checked: tls.requireTlsForAuthentication, name: 'tls.requireTlsForAuthentication', help: t('nut.requireTlsHelp') });
    const maxConnField = field({ label: t('nut.maxConnections'), control: maxConn, name: 'maxConnections', className: 'field-narrow' });
    const maxAgeField = field({ label: t('nut.maxAge'), control: maxAge, name: 'maxAgeSeconds', help: t('nut.maxAgeHelp'), className: 'field-narrow' });
    const fsdField = field({ label: t('nut.fsdDelay'), control: fsdDelay, name: 'fsdClearDelaySeconds', help: t('nut.fsdDelayHelp'), className: 'field-narrow' });
    const tlsFields = h('div', { class: 'form-grid' },
      field({ label: t('nut.certPath'), control: certPath, name: 'tls.certificatePath', help: t('nut.certPathHelp'), className: 'field-wide' }),
      field({ label: t('nut.certPassword'), control: certPassword.el, name: 'tls.certificatePassword' }),
      requireTls);
    const syncTls = () => {
      tlsFields.hidden = !tlsEnabled.input.checked;
    };
    tlsEnabled.input.addEventListener('change', syncTls);
    syncTls();

    const submit = saveButton();
    const form = h('form', { class: 'form', novalidate: true },
      errors.el,
      section({ title: t('nut.listening') },
        enabled,
        field({ label: t('nut.endpoints'), control: listen.el, name: 'listen', help: t('nut.endpointsHelp') })),
      section({ title: t('nut.access') },
        field({ label: t('nut.allowedNetworks'), control: networks.el, name: 'allowedNetworks', help: t('nut.allowedNetworksHelp') })),
      section({ title: 'TLS', description: t('nut.tlsHint') }, tlsEnabled, tlsFields),
      advancedGroup(t('settings.advanced'), section({ title: t('nut.behaviour') },
        h('div', { class: 'form-grid' }, maxConnField, maxAgeField, fsdField,
          field({ label: t('nut.version'), control: version, name: 'versionString', help: t('nut.versionHelp') })))),
      saveBar(submit));
    const dirty = trackDirty(form, ctx);

    replace(body, form, clientsSection(ctx), helpBox(settings));

    form.addEventListener('submit', async (event) => {
      event.preventDefault();
      clearErrors(form);
      let ok = listen.validate();
      ok = networks.validate() && ok;
      for (const [f, input, range] of [[maxConnField, maxConn, { min: 1 }], [maxAgeField, maxAge, { min: 1 }], [fsdField, fsdDelay, { min: 0 }]]) {
        const message = checkInt(input, range);
        setFieldError(f, message);
        if (message) ok = false;
      }
      if (!ok) {
        errors.show(t('form.fixErrors'));
        return;
      }
      const tlsBody = {
        enabled: tlsEnabled.input.checked,
        certificatePath: certPath.value.trim() || null,
        requireTlsForAuthentication: requireTls.input.checked,
      };
      const password = certPassword.value();
      if (password !== undefined) tlsBody.certificatePassword = password;
      const payload = {
        enabled: enabled.input.checked,
        listen: listen.values(),
        maxConnections: intValue(maxConn),
        allowedNetworks: networks.getValues(),
        maxAgeSeconds: intValue(maxAge),
        fsdClearDelaySeconds: intValue(fsdDelay),
        tls: tlsBody,
        versionString: version.value.trim() || null,
      };
      if (!payload.enabled) {
        const confirmed = await confirmDialog({ title: t('nut.disableTitle'), message: t('nut.disableText'), confirmLabel: t('common.save'), danger: true });
        if (!confirmed) return;
      }
      const saved = await saveWithErrors({
        form, errorBox: errors, button: submit, success: t('settings.saved'),
        request: () => api.put('api/admin/settings/nut', payload),
      });
      if (saved) {
        dirty.clean();
        navigate('/settings/nut', { replace: true });
      }
    });
    if (!state.server?.nut?.lastError) return;
    body.prepend(callout('warning', t('dashboard.nutError'), h('p', { text: state.server.nut.lastError })));
  },
};
