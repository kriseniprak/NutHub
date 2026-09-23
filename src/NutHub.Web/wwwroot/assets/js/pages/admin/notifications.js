// Notifications: which events are sent, the e-mail channel, webhooks, commands, per-channel tests and the log of
// recent deliveries. Everything is saved together with one PUT; tests use the saved settings.
import { h, replace } from '../../dom.js';
import { icon } from '../../icons.js';
import { t } from '../../i18n.js';
import { api, isAbort, errorText, commandResultText } from '../../api.js';
import { navigate } from '../../router.js';
import { pageHeader, section, loading, errorState, callout } from '../../page.js';
import {
  field, textInput, numberInput, selectInput, switchField, formErrorBox, secretControl, setFieldError, clearErrors,
} from '../../components/fields.js';
import { listEditor, validateEmail } from '../../components/listeditor.js';
import { dataTable } from '../../components/table.js';
import { itemRow, itemList, rowMenu } from '../../components/itemlist.js';
import { confirmDialog } from '../../components/dialog.js';
import { toast } from '../../components/toast.js';
import { pill } from '../../components/badge.js';
import { relTime } from '../../format.js';
import { eventTypeText } from '../../labels.js';
import {
  trackDirty, saveBar, advancedGroup, saveWithErrors, saveButton, checkInt, intValue,
} from './common.js';
import { eventPicker, channelEvents, DEFAULT_EVENTS, webhookDialog, commandDialog } from './notify-parts.js';

export default {
  title: () => t('nav.notifications'),
  async render(el, ctx) {
    el.append(pageHeader({ title: t('nav.notifications'), subtitle: t('notify.subtitle') }));
    const body = h('div', {}, loading());
    el.appendChild(body);
    let settings;
    try {
      settings = await api.get('api/admin/notifications', { signal: ctx.signal });
    } catch (err) {
      if (isAbort(err)) return;
      replace(body, errorState(err, () => navigate('/settings/notifications')));
      return;
    }

    const errors = formErrorBox();
    let webhooks = structuredClone(settings.webhooks ?? []);
    let commands = structuredClone(settings.commands ?? []);
    let savedIds = new Set([...webhooks, ...commands].map((x) => x.id));
    let dirtyState = false;
    const unsavedNote = h('span', { class: 'muted small', hidden: true, text: t('notify.saveFirst') });

    // ---- Events ----
    const picker = eventPicker(settings.events ?? DEFAULT_EVENTS);
    const restore = h('button', { type: 'button', class: 'btn btn-sm' }, icon('refresh'), h('span', { text: t('notify.restoreDefaults') }));
    restore.addEventListener('click', () => {
      picker.set(DEFAULT_EVENTS);
      dirty.mark();
      markDirty();
    });

    // ---- E-mail ----
    const email = settings.email ?? {};
    const emailEnabled = switchField({ label: t('notify.emailEnabled'), checked: email.enabled, name: 'email.enabled' });
    const smtpHost = textInput({ value: email.host ?? '', placeholder: 'smtp.example.com', className: 'input-mono', autocomplete: 'off' });
    const smtpPort = numberInput({ value: email.port ?? 587, min: 1, max: 65535, step: 1 });
    const security = selectInput({
      options: ['auto', 'none', 'startTls', 'sslOnConnect'].map((v) => ({ value: v, label: t(`notify.security.${v}`) })),
      value: email.security ?? 'auto',
    });
    const smtpUser = textInput({ value: email.username ?? '', autocomplete: 'off' });
    const smtpPassword = secretControl({ isSet: !!email.passwordSet });
    const from = textInput({ value: email.from ?? '', type: 'email', placeholder: 'nuthub@example.com', autocomplete: 'off' });
    const to = listEditor({ values: email.to ?? [], placeholder: 'admin@example.com', label: t('notify.recipient'), name: 'email.to', validate: validateEmail, inputType: 'email', addLabel: t('notify.addRecipient'), mono: false });
    const prefix = textInput({ value: email.subjectPrefix ?? '[NutHub]', autocomplete: 'off' });
    const emailEvents = channelEvents(email.events ?? null, () => markDirty());
    const hostField = field({ label: t('notify.smtpHost'), control: smtpHost, name: 'email.host' });
    const portField = field({ label: t('nut.port'), control: smtpPort, name: 'email.port', className: 'field-narrow' });
    const fromField = field({ label: t('notify.from'), control: from, name: 'email.from' });
    const emailFields = h('div', { class: 'stack' },
      h('div', { class: 'form-grid' }, hostField, portField,
        field({ label: t('notify.security'), control: security, name: 'email.security', help: t('notify.securityHelp') }),
        field({ label: t('notify.smtpUser'), control: smtpUser, name: 'email.username' }),
        field({ label: t('notify.smtpPassword'), control: smtpPassword.el, name: 'email.password' }),
        fromField,
        field({ label: t('notify.subjectPrefix'), control: prefix, name: 'email.subjectPrefix' })),
      field({ label: t('notify.recipients'), control: to.el, name: 'email.to' }),
      emailEvents.el);
    const syncEmail = () => {
      emailFields.classList.toggle('is-muted', !emailEnabled.input.checked);
    };
    emailEnabled.input.addEventListener('change', syncEmail);
    syncEmail();
    const emailResult = h('div', { class: 'test-result', role: 'status' });
    const emailTestButton = h('button', { type: 'button', class: 'btn btn-sm' }, icon('send'), h('span', { text: t('notify.sendTest') }));
    emailTestButton.addEventListener('click', () => runTest('email', null, emailResult, emailTestButton));

    // ---- Webhooks and commands ----
    const webhookList = itemList(t('notify.webhooks'));
    const commandList = itemList(t('notify.commands'));
    const addWebhook = h('button', { type: 'button', class: 'btn btn-sm' }, icon('plus'), h('span', { text: t('notify.addWebhook') }));
    const addCommand = h('button', { type: 'button', class: 'btn btn-sm' }, icon('plus'), h('span', { text: t('notify.addCommand') }));
    addWebhook.addEventListener('click', async () => {
      const w = await webhookDialog(null);
      if (w) {
        webhooks.push(w);
        markDirty();
        renderChannels();
      }
    });
    addCommand.addEventListener('click', async () => {
      const c = await commandDialog(null);
      if (c) {
        commands.push(c);
        markDirty();
        renderChannels();
      }
    });

    /** Sends a test through a saved channel and shows the answer in `target` (or as a toast). */
    async function runTest(channel, id, target, button) {
      if (button) {
        button.disabled = true;
        button.classList.add('is-busy');
      }
      try {
        const result = await api.post('api/admin/notifications/test', { channel, id }, { timeout: 60000 });
        const text = result?.ok ? t('notify.testOk') : `${t('notify.testFailed')}: ${commandResultText(result)}`;
        if (target) replace(target, h('span', { class: ['test-msg', result?.ok ? 'ok' : 'fail'] }, icon(result?.ok ? 'checkCircle' : 'warning', { className: 'icon-sm' }), text));
        else toast(text, { kind: result?.ok ? 'success' : 'error' });
        loadDeliveries();
      } catch (err) {
        const text = errorText(err);
        if (target) replace(target, h('span', { class: 'test-msg fail' }, icon('warning', { className: 'icon-sm' }), text));
        else toast(text, { kind: 'error' });
      } finally {
        if (button) {
          button.classList.remove('is-busy');
          syncTests();
        }
      }
    }

    function channelItem(item, kind, index) {
      const list = kind === 'webhook' ? webhooks : commands;
      const unsaved = !item.id || !savedIds.has(item.id);
      const result = h('div', { class: 'test-result', role: 'status' });
      const edit = async () => {
        const next = kind === 'webhook' ? await webhookDialog(item) : await commandDialog(item);
        if (next) {
          list[index] = next;
          markDirty();
          renderChannels();
        }
      };
      const remove = async () => {
        const ok = await confirmDialog({ title: t('notify.removeTitle', { name: item.name }), message: t('notify.removeText'), confirmLabel: t('common.remove'), danger: true });
        if (!ok) return;
        list.splice(index, 1);
        markDirty();
        renderChannels();
      };
      const detail = kind === 'webhook' ? `${item.method} ${item.url}` : [item.command, item.arguments].filter(Boolean).join(' ');
      return itemRow({
        title: item.name || '–',
        onOpen: edit,
        className: !item.enabled && 'is-disabled',
        pills: [item.enabled ? null : pill(t('common.disabled')),
          item.events ? pill(t('notify.customEventsShort', { count: item.events.length }), 'info') : null,
          unsaved ? pill(t('notify.unsaved'), 'warning') : null],
        meta: h('span', { class: 'mono', text: detail }),
        // Evaluated on each open, so the test entry follows the unsaved state of the page.
        menu: rowMenu(item.name || '–', () => [
          {
            label: t('notify.sendTest'),
            description: dirtyState || unsaved ? t('notify.saveFirst') : null,
            disabled: dirtyState || unsaved,
            onSelect: () => runTest(kind, item.id, result),
          },
          { label: t('common.edit'), onSelect: edit },
          { separator: true },
          { label: t('common.remove'), danger: true, onSelect: remove },
        ]),
        below: result,
      });
    }

    function renderChannels() {
      replace(webhookList, webhooks.length ? webhooks.map((w, i) => channelItem(w, 'webhook', i)) : h('li', { class: 'item-empty muted', text: t('notify.noWebhooks') }));
      replace(commandList, commands.length ? commands.map((c, i) => channelItem(c, 'command', i)) : h('li', { class: 'item-empty muted', text: t('notify.noCommands') }));
      syncTests();
    }

    function syncTests() {
      emailTestButton.disabled = dirtyState;
      emailTestButton.title = dirtyState ? t('notify.saveFirst') : '';
      unsavedNote.hidden = !dirtyState;
    }

    function markDirty() {
      dirtyState = true;
      dirty.mark();
      syncTests();
    }

    // ---- Deliveries ----
    const deliveries = dataTable({
      caption: t('notify.deliveries'),
      rowKey: (d, i) => `${d.timestamp}-${d.channel}-${d.target}-${i}`,
      emptyText: t('notify.noDeliveries'),
      sort: { key: 'timestamp', dir: 'desc' },
      compact: true,
      columns: [
        { key: 'timestamp', label: t('common.time'), render: (d) => relTime(d.timestamp) },
        { key: 'channel', label: t('notify.channel'), render: (d) => t(`notify.channelName.${d.channel}`) },
        { key: 'target', label: t('notify.target'), render: (d) => h('span', { class: 'cell-mono', text: d.target ?? '–' }) },
        { key: 'eventType', label: t('notify.event'), render: (d) => eventTypeText(d.eventType) || (d.eventType ?? '–') },
        { key: 'ups', label: 'UPS', render: (d) => d.ups ?? '–' },
        {
          key: 'success',
          label: t('notify.result'),
          sortValue: (d) => (d.success ? 1 : 0),
          render: (d) => (d.success ? pill(t('notify.delivered'), 'ok')
            : h('div', {}, pill(t('notify.failed'), 'critical'), d.error ? h('p', { class: 'small muted', text: d.error }) : null)),
        },
      ],
    });
    const refreshDeliveries = h('button', { type: 'button', class: 'btn btn-sm' }, icon('refresh'), h('span', { text: t('common.refresh') }));
    async function loadDeliveries() {
      try {
        deliveries.setRows(await api.get('api/admin/notifications/deliveries', { signal: ctx.signal, quiet: true }));
      } catch (err) {
        if (!isAbort(err)) toast(errorText(err), { kind: 'error' });
      }
    }
    refreshDeliveries.addEventListener('click', loadDeliveries);

    const submit = saveButton();
    const form = h('form', { class: 'form', novalidate: true },
      errors.el,
      section({ title: t('notify.events'), description: t('notify.eventsHint'), actions: restore }, picker.el),
      section({ title: t('notify.email'), description: t('notify.emailHint'), actions: emailTestButton }, emailEnabled, emailFields, emailResult),
      section({ title: t('notify.webhooks'), description: t('notify.webhooksHint'), actions: addWebhook, className: 'list-section' }, webhookList),
      section({ title: t('notify.commands'), description: t('notify.commandsHint'), actions: addCommand, className: 'list-section' }, commandList),
      saveBar(submit, { note: unsavedNote }));
    const dirty = trackDirty(form, ctx);
    form.addEventListener('input', () => {
      dirtyState = true;
      syncTests();
    });
    form.addEventListener('change', () => {
      dirtyState = true;
      syncTests();
    });
    // The delivery log answers "did it arrive?" after a test or an outage: folded until needed.
    replace(body, form, advancedGroup(t('notify.deliveries'),
      section({ description: t('notify.deliveriesHint'), actions: refreshDeliveries }, deliveries.el)));
    renderChannels();
    loadDeliveries();

    form.addEventListener('submit', async (event) => {
      event.preventDefault();
      clearErrors(form);
      let ok = to.validate();
      if (emailEnabled.input.checked) {
        if (!smtpHost.value.trim()) {
          setFieldError(hostField, t('validate.required'));
          ok = false;
        }
        const portMessage = checkInt(smtpPort, { min: 1, max: 65535 });
        setFieldError(portField, portMessage);
        if (portMessage) ok = false;
        if (validateEmail(from.value.trim())) {
          setFieldError(fromField, t('validate.email'));
          ok = false;
        }
        if (!to.getValues().length) {
          setFieldError(to.el.closest('.field'), t('notify.needRecipient'));
          ok = false;
        }
      }
      if (!ok) {
        errors.show(t('form.fixErrors'));
        return;
      }
      const emailBody = {
        enabled: emailEnabled.input.checked,
        host: smtpHost.value.trim(),
        port: intValue(smtpPort) ?? 587,
        security: security.value,
        username: smtpUser.value.trim() || null,
        from: from.value.trim(),
        to: to.getValues(),
        subjectPrefix: prefix.value,
        events: emailEvents.value(),
      };
      const pw = smtpPassword.value();
      if (pw !== undefined) emailBody.password = pw;
      // New channels have no id yet: leave it out so the server assigns one.
      const withId = ({ id, ...rest }) => (id ? { id, ...rest } : rest);
      const payload = { events: picker.values(), email: emailBody, webhooks: webhooks.map(withId), commands: commands.map(withId) };
      const saved = await saveWithErrors({
        form, errorBox: errors, button: submit, success: t('settings.saved'),
        request: () => api.put('api/admin/notifications', payload),
      });
      if (saved) {
        dirty.clean();
        dirtyState = false;
        // Reload so ids assigned by the server and the stored-secret markers are current.
        navigate('/settings/notifications', { replace: true });
      }
    });
    if (!(settings.webhooks ?? []).length && !(settings.email?.enabled)) {
      body.prepend(callout('info', t('notify.startTitle'), h('p', { text: t('notify.startText') })));
    }
    savedIds = new Set([...(settings.webhooks ?? []), ...(settings.commands ?? [])].map((x) => x.id));
  },
};
