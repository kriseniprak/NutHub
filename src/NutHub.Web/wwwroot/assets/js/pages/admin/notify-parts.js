// Building blocks of the notifications page: the event checklist, per-channel event filters, and the webhook and
// command dialogs (with presets for common services).
import { h } from '../../dom.js';
import { icon } from '../../icons.js';
import { t } from '../../i18n.js';
import {
  field, textInput, numberInput, selectInput, switchField, textArea, checkbox, radio, setFieldError, clearErrors,
} from '../../components/fields.js';
import { kvEditor } from '../../components/kveditor.js';
import { openDialog } from '../../components/dialog.js';
import { eventTypeText, categoryText } from '../../labels.js';

export const EVENT_GROUPS = {
  power: ['onBattery', 'online', 'lowBattery', 'lowBatteryCleared', 'forcedShutdown', 'forcedShutdownCleared'],
  device: ['replaceBattery', 'replaceBatteryCleared', 'overload', 'overloadCleared', 'bypass', 'bypassCleared', 'off',
    'offCleared', 'calibration', 'calibrationEnded', 'trim', 'trimEnded', 'boost', 'boostEnded', 'alarm',
    'alarmCleared', 'testStarted', 'testEnded'],
  communication: ['communicationLost', 'communicationRestored', 'noCommunication', 'driverStarted', 'driverStopped', 'driverFailed'],
  command: ['commandExecuted', 'commandFailed', 'variableChanged'],
  shutdown: ['shutdownPending', 'shutdownCancelled', 'shutdownStarted'],
  audit: ['configurationChanged', 'userLogin', 'userLoginFailed', 'nutClientLogin', 'nutClientLogout'],
  system: ['serverStarted', 'serverStopping'],
};

/** Events notified on a fresh installation (UpsEventCatalog.DefaultNotified). */
export const DEFAULT_EVENTS = ['onBattery', 'online', 'lowBattery', 'forcedShutdown', 'replaceBattery', 'overload',
  'communicationLost', 'communicationRestored', 'noCommunication', 'shutdownPending', 'shutdownStarted', 'shutdownCancelled'];

export const PLACEHOLDERS = ['ups', 'type', 'notifytype', 'severity', 'category', 'message', 'timestamp', 'server',
  'location', 'status', 'charge', 'runtime', 'load', 'actor'];

/** Grouped checklist of event types; returns { el, values(), set(values) }. */
export function eventPicker(selected, { onChange, compact = false } = {}) {
  const chosen = new Set(selected ?? []);
  const boxes = [];
  const groups = Object.entries(EVENT_GROUPS).map(([category, types]) => {
    const groupBoxes = types.map((type) => {
      const cb = checkbox({ label: eventTypeText(type), value: type, checked: chosen.has(type) });
      boxes.push(cb.querySelector('input'));
      return cb;
    });
    const all = h('button', { type: 'button', class: 'btn btn-sm btn-ghost', text: t('notify.all') });
    const none = h('button', { type: 'button', class: 'btn btn-sm btn-ghost', text: t('notify.none') });
    const setAll = (on) => {
      for (const cb of groupBoxes) cb.querySelector('input').checked = on;
      onChange?.();
    };
    all.addEventListener('click', () => setAll(true));
    none.addEventListener('click', () => setAll(false));
    return h('fieldset', { class: 'fieldset event-group' },
      h('legend', { text: categoryText(category) }),
      h('div', { class: 'event-group-tools' }, all, none),
      h('div', { class: 'check-grid' }, groupBoxes));
  });
  const el = h('div', { class: ['event-picker', compact && 'is-compact'] }, groups);
  el.addEventListener('change', () => onChange?.());
  return {
    el,
    values: () => boxes.filter((b) => b.checked).map((b) => b.value),
    set(values) {
      const s = new Set(values);
      for (const b of boxes) b.checked = s.has(b.value);
    },
  };
}

/** Per-channel filter: null = the general list, otherwise a custom list. Returns { el, value() }. */
export function channelEvents(value, onChange) {
  const name = `ce-${Math.random().toString(36).slice(2)}`;
  const same = radio({ label: t('notify.sameEvents'), name, value: 'same', checked: value === null || value === undefined });
  const custom = radio({ label: t('notify.customEvents'), name, value: 'custom', checked: Array.isArray(value) });
  const picker = eventPicker(value ?? DEFAULT_EVENTS, { onChange, compact: true });
  const sync = () => {
    picker.el.hidden = !custom.querySelector('input').checked;
  };
  same.querySelector('input').addEventListener('change', () => {
    sync();
    onChange?.();
  });
  custom.querySelector('input').addEventListener('change', () => {
    sync();
    onChange?.();
  });
  sync();
  const el = h('fieldset', { class: 'fieldset' }, h('legend', { text: t('notify.channelEvents') }), same, custom, picker.el);
  return {
    el,
    value: () => (custom.querySelector('input').checked ? picker.values() : null),
  };
}

const PRESETS = [
  {
    id: 'ntfy', label: 'ntfy', url: 'https://ntfy.sh/', contentType: 'application/json',
    template: '{"topic": "nuthub-alerts", "title": "{server}: {ups}", "message": "{message}", "tags": ["{severity}"]}',
    headers: [],
  },
  {
    id: 'telegram', label: 'Telegram', url: 'https://api.telegram.org/bot<bot-token>/sendMessage', contentType: 'application/json',
    template: '{"chat_id": "<chat-id>", "text": "{server}: {message}"}',
    headers: [],
  },
  {
    id: 'slack', label: 'Slack', url: 'https://hooks.slack.com/services/<T000>/<B000>/<secret>', contentType: 'application/json',
    template: '{"text": "*{server}* — {message}"}',
    headers: [],
  },
  {
    id: 'teams', label: 'Microsoft Teams', url: 'https://<tenant>.webhook.office.com/<workflow-url>', contentType: 'application/json',
    template: '{"type": "message", "attachments": [{"contentType": "application/vnd.microsoft.card.adaptive", "content": {"type": "AdaptiveCard", "version": "1.4", "body": [{"type": "TextBlock", "weight": "Bolder", "text": "{server}: {ups}"}, {"type": "TextBlock", "wrap": true, "text": "{message}"}]}}]}',
    headers: [],
  },
  {
    id: 'discord', label: 'Discord', url: 'https://discord.com/api/webhooks/<id>/<token>', contentType: 'application/json',
    template: '{"username": "NutHub", "content": "**{server}** — {message}"}',
    headers: [],
  },
  {
    id: 'gotify', label: 'Gotify', url: 'https://<gotify-host>/message', contentType: 'application/json',
    template: '{"title": "{server}: {ups}", "message": "{message}", "priority": 5}',
    headers: [['X-Gotify-Key', '']],
  },
];

function insertAtCursor(textarea, text) {
  const start = textarea.selectionStart ?? textarea.value.length;
  const end = textarea.selectionEnd ?? textarea.value.length;
  textarea.value = textarea.value.slice(0, start) + text + textarea.value.slice(end);
  textarea.focus();
  textarea.setSelectionRange(start + text.length, start + text.length);
  textarea.dispatchEvent(new Event('input', { bubbles: true }));
}

/** Opens the webhook editor; resolves to the edited webhook or null. */
export function webhookDialog(hook) {
  const creating = !hook;
  const w = hook ?? { id: null, name: '', enabled: true, url: '', method: 'POST', contentType: 'application/json', bodyTemplate: null, headers: {}, events: null };
  const name = textInput({ value: w.name, autocomplete: 'off' });
  const url = textInput({ value: w.url, type: 'url', placeholder: 'https://', className: 'input-mono', autocomplete: 'off' });
  const method = selectInput({ options: ['POST', 'PUT', 'PATCH', 'GET'].map((m) => ({ value: m, label: m })), value: w.method || 'POST' });
  const ctList = `ct-${Math.random().toString(36).slice(2)}`;
  const contentType = textInput({ value: w.contentType ?? 'application/json', list: ctList, className: 'input-mono', autocomplete: 'off' });
  const ctOptions = h('datalist', { id: ctList }, ['application/json', 'text/plain', 'application/x-www-form-urlencoded'].map((v) => h('option', { value: v })));
  const enabled = switchField({ label: t('common.enabled'), checked: w.enabled !== false, name: 'enabled' });
  const template = textArea({ rows: 6, value: w.bodyTemplate ?? '', placeholder: t('notify.templateDefault') });
  const headers = kvEditor({
    entries: Object.entries(w.headers ?? {}),
    keyLabel: t('notify.headerName'),
    valueLabel: t('notify.headerValue'),
    keyPlaceholder: 'Authorization',
    valuePlaceholder: 'Bearer …',
    secretValues: true,
    addLabel: t('notify.addHeader'),
    validateKey: (k) => (/^[A-Za-z0-9!#$%&'*+.^_`|~-]+$/.test(k) ? null : t('notify.badHeader')),
  });
  const events = channelEvents(w.events ?? null);
  const chips = h('div', { class: 'chips', role: 'group', 'aria-label': t('notify.placeholders') },
    PLACEHOLDERS.map((p) => {
      const b = h('button', { type: 'button', class: 'chip', text: `{${p}}`, title: t(`notify.ph.${p}`) });
      b.addEventListener('click', () => insertAtCursor(template, `{${p}}`));
      return b;
    }));
  const presets = h('div', { class: 'row presets' }, h('span', { class: 'muted small', text: t('notify.presets') }),
    PRESETS.map((p) => {
      const b = h('button', { type: 'button', class: 'btn btn-sm', text: p.label });
      b.addEventListener('click', () => {
        if (!name.value.trim()) name.value = p.label;
        url.value = p.url;
        method.value = 'POST';
        contentType.value = p.contentType;
        template.value = p.template;
        const existing = headers.getEntries();
        const merged = [...existing];
        for (const [k, v] of p.headers) if (!merged.some(([x]) => x.toLowerCase() === k.toLowerCase())) merged.push([k, v]);
        headers.setEntries(merged);
        url.focus();
        url.setSelectionRange(0, url.value.length);
      });
      return b;
    }));

  const nameField = field({ label: t('common.name'), control: name, name: 'name', required: true });
  const urlField = field({ label: 'URL', control: url, name: 'url', required: true, help: t('notify.urlHelp'), className: 'field-wide' });
  const content = h('form', { class: 'form', novalidate: true },
    presets,
    h('div', { class: 'form-grid' }, nameField, enabled, urlField,
      field({ label: t('notify.method'), control: method, name: 'method' }),
      field({ label: t('notify.contentType'), control: contentType, name: 'contentType' }), ctOptions),
    field({ label: t('notify.template'), control: template, name: 'bodyTemplate', help: t('notify.templateHelp') }),
    chips,
    h('div', { class: 'field', dataset: { field: 'headers' } }, h('span', { class: 'field-label', text: t('notify.headers') }),
      h('p', { class: 'field-help', text: t('notify.headersHelp') }), headers.el),
    events.el);
  content.addEventListener('submit', (e) => e.preventDefault());

  const ctl = openDialog({
    title: creating ? t('notify.addWebhook') : t('notify.editWebhook', { name: w.name }),
    content,
    size: 'lg',
    actions: [
      { label: t('common.cancel') },
      {
        label: t('common.apply'),
        kind: 'primary',
        onClick: () => {
          clearErrors(content);
          let ok = true;
          if (!name.value.trim()) {
            setFieldError(nameField, t('validate.required'));
            ok = false;
          }
          const u = url.value.trim();
          if (!/^https?:\/\/\S+$/i.test(u)) {
            setFieldError(urlField, t('validate.url'));
            ok = false;
          }
          if (!headers.validate()) ok = false;
          return ok;
        },
      },
    ],
  });
  name.focus();
  return ctl.result.then((confirmed) => {
    if (!confirmed) return null;
    return {
      ...w,
      name: name.value.trim(),
      enabled: enabled.input.checked,
      url: url.value.trim(),
      method: method.value,
      contentType: contentType.value.trim() || 'application/json',
      bodyTemplate: template.value.trim() ? template.value : null,
      headers: Object.fromEntries(headers.getEntries()),
      events: events.value(),
    };
  });
}

/** Opens the command hook editor; resolves to the edited hook or null. */
export function commandDialog(hook) {
  const creating = !hook;
  const c = hook ?? { id: null, name: '', enabled: true, command: '', arguments: null, timeoutSeconds: 30, events: null };
  const name = textInput({ value: c.name, autocomplete: 'off' });
  const command = textInput({ value: c.command, className: 'input-mono', placeholder: '/usr/local/bin/ups-alert.sh', autocomplete: 'off' });
  const args = textInput({ value: c.arguments ?? '', className: 'input-mono', placeholder: '--ups {ups}', autocomplete: 'off' });
  const timeout = numberInput({ value: c.timeoutSeconds ?? 30, min: 1, max: 3600, step: 1 });
  const enabled = switchField({ label: t('common.enabled'), checked: c.enabled !== false, name: 'enabled' });
  const events = channelEvents(c.events ?? null);
  const nameField = field({ label: t('common.name'), control: name, name: 'name', required: true });
  const commandField = field({ label: t('notify.command'), control: command, name: 'command', required: true, help: t('notify.commandHelp'), className: 'field-wide' });
  const timeoutField = field({ label: t('notify.timeout'), control: timeout, name: 'timeoutSeconds', className: 'field-narrow' });
  const content = h('form', { class: 'form', novalidate: true },
    h('div', { class: 'form-grid' }, nameField, enabled, commandField,
      field({ label: t('notify.arguments'), control: args, name: 'arguments', help: t('notify.argumentsHelp'), className: 'field-wide' }),
      timeoutField),
    h('p', { class: 'muted small' }, icon('info', { className: 'icon-sm inline-icon' }), t('notify.envHelp')),
    events.el);
  content.addEventListener('submit', (e) => e.preventDefault());
  const ctl = openDialog({
    title: creating ? t('notify.addCommand') : t('notify.editCommand', { name: c.name }),
    content,
    size: 'lg',
    actions: [
      { label: t('common.cancel') },
      {
        label: t('common.apply'),
        kind: 'primary',
        onClick: () => {
          clearErrors(content);
          let ok = true;
          if (!name.value.trim()) {
            setFieldError(nameField, t('validate.required'));
            ok = false;
          }
          if (!command.value.trim()) {
            setFieldError(commandField, t('validate.required'));
            ok = false;
          }
          const n = Number(timeout.value);
          if (!Number.isInteger(n) || n < 1) {
            setFieldError(timeoutField, t('validate.min', { min: 1 }));
            ok = false;
          }
          return ok;
        },
      },
    ],
  });
  name.focus();
  return ctl.result.then((confirmed) => {
    if (!confirmed) return null;
    return {
      ...c,
      name: name.value.trim(),
      enabled: enabled.input.checked,
      command: command.value.trim(),
      arguments: args.value.trim() || null,
      timeoutSeconds: Number(timeout.value),
      events: events.value(),
    };
  });
}
