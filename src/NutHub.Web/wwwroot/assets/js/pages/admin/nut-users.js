// NUT accounts (the equivalent of upsd.users): monitor role, SET/FSD actions, instant commands, allowed UPSes.
import { h, replace, seg } from '../../dom.js';
import { icon } from '../../icons.js';
import { t } from '../../i18n.js';
import { api, isAbort, errorText } from '../../api.js';
import { state } from '../../store.js';
import { navigate } from '../../router.js';
import { pageHeader, loading, errorState } from '../../page.js';
import {
  field, textInput, passwordInput, checkbox, radio, fieldset, setFieldError, applyFieldErrors, clearErrors,
} from '../../components/fields.js';
import { itemRow, itemList, rowMenu } from '../../components/itemlist.js';
import { openDialog, confirmDialog } from '../../components/dialog.js';
import { toast } from '../../components/toast.js';
import { pill } from '../../components/badge.js';
import { monitorRoleText } from '../../labels.js';

const USER_PATTERN = /^[A-Za-z0-9][A-Za-z0-9_.@-]{0,63}$/;

let knownCommands = null;
async function loadKnownCommands() {
  if (knownCommands) return knownCommands;
  const results = await Promise.allSettled(state.ups.map((u) => api.get(`api/ups/${seg(u.name)}`, { quiet: true, timeout: 8000 })));
  const names = new Map();
  for (const r of results) {
    if (r.status !== 'fulfilled') continue;
    for (const c of r.value?.commands ?? []) if (!names.has(c.name)) names.set(c.name, c);
  }
  knownCommands = [...names.values()].sort((a, b) => a.name.localeCompare(b.name));
  return knownCommands;
}

async function editUser(user, onSaved) {
  const creating = !user;
  const u = user ?? { name: '', monitor: 'secondary', actions: [], instantCommands: [], allowedUps: [] };
  const name = textInput({ value: u.name, autocomplete: 'off', maxlength: 64 });
  const password = passwordInput({ placeholder: creating ? '' : t('secret.keep') });
  const nameField = field({ label: t('common.name'), control: name, name: 'name', required: true, help: t('nutUsers.nameHelp') });
  const passwordField = field({ label: t('auth.password'), control: password, name: 'password', required: creating, help: t('nutUsers.passwordHelp') });
  const radioName = `mon-${Math.random().toString(36).slice(2)}`;
  const roles = ['none', 'secondary', 'primary'].map((role) => radio({
    label: monitorRoleText(role), name: radioName, value: role, checked: u.monitor === role, description: t(`nutUsers.roleHelp.${role}`),
  }));
  const actionBoxes = ['SET', 'FSD'].map((a) => checkbox({ label: a, value: a, checked: (u.actions ?? []).map((x) => x.toUpperCase()).includes(a), description: t(`nutUsers.action.${a}`) }));

  const cmdMode = `cmd-${Math.random().toString(36).slice(2)}`;
  const all = (u.instantCommands ?? []).some((c) => c.toUpperCase() === 'ALL');
  const selectedCmds = new Set((u.instantCommands ?? []).filter((c) => c.toUpperCase() !== 'ALL'));
  const modeAll = radio({ label: t('nutUsers.cmdAll'), name: cmdMode, value: 'all', checked: all });
  const modeSome = radio({ label: t('nutUsers.cmdSome'), name: cmdMode, value: 'some', checked: !all && selectedCmds.size > 0 });
  const modeNone = radio({ label: t('nutUsers.cmdNone'), name: cmdMode, value: 'none', checked: !all && selectedCmds.size === 0 });
  const cmdList = h('div', { class: 'check-grid cmd-list' }, h('p', { class: 'muted small', text: t('common.loading') }));
  const extra = textInput({ placeholder: 'beeper.toggle, test.panel.start', className: 'input-mono', autocomplete: 'off' });
  const cmdBox = h('div', { class: 'stack-sm cmd-box' }, cmdList, field({ label: t('nutUsers.otherCommands'), control: extra, help: t('nutUsers.otherCommandsHelp') }));
  const syncMode = () => {
    cmdBox.hidden = !modeSome.querySelector('input').checked;
  };
  for (const r of [modeAll, modeSome, modeNone]) r.querySelector('input').addEventListener('change', syncMode);
  syncMode();
  loadKnownCommands().then((cmds) => {
    const known = new Set(cmds.map((c) => c.name));
    replace(cmdList, cmds.length ? cmds.map((c) => checkbox({ label: c.name, value: c.name, checked: selectedCmds.has(c.name), description: c.description })) : h('p', { class: 'muted small', text: t('nutUsers.noKnownCommands') }));
    extra.value = [...selectedCmds].filter((c) => !known.has(c)).join(', ');
  }).catch(() => {
    replace(cmdList, h('p', { class: 'muted small', text: t('nutUsers.noKnownCommands') }));
    extra.value = [...selectedCmds].join(', ');
  });

  const allowed = new Set((u.allowedUps ?? []).map((x) => x.toLowerCase()));
  const upsNames = [...new Set([...state.ups.map((x) => x.name), ...(u.allowedUps ?? [])])];
  const upsBoxes = upsNames.map((n) => checkbox({ label: n, value: n, checked: allowed.has(n.toLowerCase()) }));

  const content = h('form', { class: 'form', novalidate: true },
    h('div', { class: 'form-grid' }, nameField, passwordField),
    fieldset(t('nutUsers.monitor'), roles, { description: t('nutUsers.monitorHelp') }),
    fieldset(t('nutUsers.actions'), h('div', { class: 'check-grid' }, actionBoxes)),
    fieldset(t('nutUsers.instantCommands'), [modeNone, modeAll, modeSome, cmdBox]),
    fieldset(t('nutUsers.allowedUps'), upsBoxes.length ? h('div', { class: 'check-grid' }, upsBoxes) : h('p', { class: 'muted small', text: t('nutUsers.noUps') }),
      { description: t('nutUsers.allowedUpsHelp') }));
  content.addEventListener('submit', (e) => e.preventDefault());

  const ctl = openDialog({
    title: creating ? t('nutUsers.addTitle') : t('nutUsers.editTitle', { name: u.name }),
    content,
    size: 'lg',
    actions: [
      { label: t('common.cancel') },
      {
        label: creating ? t('common.create') : t('common.save'),
        kind: 'primary',
        onClick: async (dialog) => {
          clearErrors(content);
          const n = name.value.trim();
          let ok = true;
          if (!USER_PATTERN.test(n)) {
            setFieldError(nameField, n ? t('nutUsers.badName') : t('validate.required'));
            ok = false;
          }
          if ((creating || password.value) && [...password.value].length < 8) {
            setFieldError(passwordField, t('password.ruleLength', { count: 8 }));
            ok = false;
          }
          if (!ok) return false;
          const mode = content.querySelector(`input[name="${radioName}"]:checked`)?.value ?? 'none';
          const cmdChoice = content.querySelector(`input[name="${cmdMode}"]:checked`)?.value ?? 'none';
          let instantCommands = [];
          if (cmdChoice === 'all') instantCommands = ['ALL'];
          else if (cmdChoice === 'some') {
            instantCommands = [...cmdList.querySelectorAll('input:checked')].map((i) => i.value);
            for (const c of extra.value.split(/[\s,]+/).map((x) => x.trim()).filter(Boolean)) if (!instantCommands.includes(c)) instantCommands.push(c);
          }
          const body = {
            name: n,
            monitor: mode,
            actions: actionBoxes.map((b) => b.querySelector('input')).filter((i) => i.checked).map((i) => i.value),
            instantCommands,
            allowedUps: upsBoxes.map((b) => b.querySelector('input')).filter((i) => i.checked).map((i) => i.value),
          };
          if (password.value) body.password = password.value;
          try {
            const saved = creating
              ? await api.post('api/admin/nut-users', body)
              : await api.put(`api/admin/nut-users/${seg(u.name)}`, body);
            toast(creating ? t('nutUsers.created', { name: n }) : t('nutUsers.saved', { name: n }), { kind: 'success' });
            onSaved(saved);
            return true;
          } catch (err) {
            if (err.code === 'validation' && err.fields) {
              const rest = applyFieldErrors(content, err.fields);
              if (rest.length) dialog.showError(rest.map(([k, v]) => `${k}: ${v}`).join('\n'));
              return false;
            }
            if (err.code === 'conflict') {
              setFieldError(nameField, t('error.conflictName'));
              return false;
            }
            throw err;
          }
        },
      },
    ],
  });
  name.focus();
  return ctl.result;
}

export default {
  title: () => t('nav.nutUsers'),
  async render(el, ctx) {
    const add = h('button', { type: 'button', class: 'btn btn-primary' }, icon('plus'), h('span', { text: t('nutUsers.add') }));
    el.append(pageHeader({ title: t('nav.nutUsers'), subtitle: t('nutUsers.subtitle'), actions: add }));
    const body = h('div', {}, loading());
    el.appendChild(body);
    let users;
    try {
      users = await api.get('api/admin/nut-users', { signal: ctx.signal });
    } catch (err) {
      if (isAbort(err)) return;
      replace(body, errorState(err, () => navigate('/settings/nut-users')));
      return;
    }
    const list = itemList(t('nav.nutUsers'));
    const reload = async () => {
      try {
        users = await api.get('api/admin/nut-users', { signal: ctx.signal });
        render();
      } catch (err) {
        if (!isAbort(err)) toast(errorText(err), { kind: 'error' });
      }
    };

    async function remove(u) {
      const ok = await confirmDialog({
        title: t('nutUsers.deleteTitle', { name: u.name }),
        message: t('nutUsers.deleteText'),
        confirmLabel: t('common.delete'),
        danger: true,
      });
      if (!ok) return;
      try {
        await api.del(`api/admin/nut-users/${seg(u.name)}`);
        toast(t('nutUsers.deleted', { name: u.name }), { kind: 'success' });
        reload();
      } catch (err) {
        toast(errorText(err), { kind: 'error' });
      }
    }

    /** One muted line with what the account may do: actions, commands and UPSes. */
    function summary(u) {
      const commands = (u.instantCommands ?? []).some((c) => c.toUpperCase() === 'ALL') ? t('nutUsers.cmdAllShort')
        : (u.instantCommands ?? []).length ? t('nutUsers.commandCount', { count: u.instantCommands.length }) : null;
      return [
        (u.actions ?? []).length ? u.actions.join(', ') : null,
        commands,
        (u.allowedUps ?? []).length ? u.allowedUps.join(', ') : t('nutUsers.allUps'),
      ].filter(Boolean).join(' · ');
    }

    function row(u) {
      return itemRow({
        title: u.name,
        onOpen: () => editUser(u, reload),
        pills: pill(monitorRoleText(u.monitor), u.monitor === 'primary' ? 'primary' : 'neutral'),
        meta: summary(u),
        menu: rowMenu(u.name, [
          { label: t('common.edit'), onSelect: () => editUser(u, reload) },
          { separator: true },
          { label: t('common.delete'), danger: true, onSelect: () => remove(u) },
        ]),
      });
    }

    function render() {
      const sorted = users.slice().sort((a, b) => a.name.localeCompare(b.name));
      replace(list, sorted.length ? sorted.map(row) : h('li', { class: 'item-empty muted', text: t('nutUsers.empty') }));
    }

    render();
    add.addEventListener('click', () => editUser(null, reload));
    replace(body, h('div', { class: 'card list-card' }, list), h('p', { class: 'muted small list-hint', text: t('nutUsers.listHint') }));
  },
};
