// Web panel accounts: role, disabled state, password reset and forced password change. The server refuses to
// remove the last administrator or to lock yourself out; those answers get explicit messages.
import { h, replace, seg } from '../../dom.js';
import { icon } from '../../icons.js';
import { t } from '../../i18n.js';
import { api, isAbort, errorText } from '../../api.js';
import { auth } from '../../auth.js';
import { navigate } from '../../router.js';
import { pageHeader, loading, errorState } from '../../page.js';
import {
  field, textInput, passwordInput, selectInput, switchField, setFieldError, applyFieldErrors, clearErrors,
} from '../../components/fields.js';
import { itemRow, itemList, rowMenu } from '../../components/itemlist.js';
import { openDialog, confirmDialog } from '../../components/dialog.js';
import { toast } from '../../components/toast.js';
import { pill } from '../../components/badge.js';
import { roleText } from '../../labels.js';

const USER_PATTERN = /^[A-Za-z0-9][A-Za-z0-9_.@-]{0,63}$/;

function accountError(err) {
  if (err?.code === 'lastAdmin') return t('webUsers.lastAdmin');
  if (err?.code === 'self') return t('webUsers.self');
  if (err?.code === 'conflict') return t('error.conflictName');
  return errorText(err);
}

async function editUser(user, onSaved) {
  const creating = !user;
  const u = user ?? { name: '', displayName: '', role: 'viewer', disabled: false, mustChangePassword: true };
  const isSelf = !creating && u.name.toLowerCase() === (auth.user?.name ?? '').toLowerCase();
  const name = textInput({ value: u.name, autocomplete: 'off', maxlength: 64, disabled: !creating });
  const display = textInput({ value: u.displayName ?? '', autocomplete: 'off', maxlength: 100 });
  const role = selectInput({
    options: ['viewer', 'operator', 'admin'].map((r) => ({ value: r, label: `${roleText(r)} — ${t(`webUsers.roleHelp.${r}`)}` })),
    value: u.role,
    disabled: isSelf,
  });
  const password = passwordInput({ placeholder: creating ? '' : t('webUsers.passwordKeep') });
  const disabled = switchField({ label: t('webUsers.disabled'), checked: u.disabled, name: 'disabled', help: t('webUsers.disabledHelp'), disabled: isSelf });
  const mustChange = switchField({ label: t('webUsers.mustChange'), checked: u.mustChangePassword, name: 'mustChangePassword', help: t('webUsers.mustChangeHelp') });
  const nameField = field({ label: t('account.username'), control: name, name: 'name', required: creating });
  const passwordField = field({ label: creating ? t('auth.password') : t('webUsers.resetPassword'), control: password, name: 'password', required: creating, help: t('password.ruleLength', { count: 8 }) });
  const content = h('form', { class: 'form', novalidate: true },
    h('div', { class: 'form-grid' },
      nameField,
      field({ label: t('account.displayName'), control: display, name: 'displayName' }),
      field({ label: t('account.role'), control: role, name: 'role', help: isSelf ? t('webUsers.selfRole') : null, className: 'field-wide' }),
      passwordField),
    disabled, mustChange);
  content.addEventListener('submit', (e) => e.preventDefault());

  const ctl = openDialog({
    title: creating ? t('webUsers.addTitle') : t('webUsers.editTitle', { name: u.name }),
    content,
    actions: [
      { label: t('common.cancel') },
      {
        label: creating ? t('common.create') : t('common.save'),
        kind: 'primary',
        onClick: async (dialog) => {
          clearErrors(content);
          const n = name.value.trim();
          let ok = true;
          if (creating && !USER_PATTERN.test(n)) {
            setFieldError(nameField, n ? t('nutUsers.badName') : t('validate.required'));
            ok = false;
          }
          if ((creating || password.value) && [...password.value].length < 8) {
            setFieldError(passwordField, t('password.ruleLength', { count: 8 }));
            ok = false;
          }
          if (!ok) return false;
          const body = {
            name: creating ? n : u.name,
            displayName: display.value.trim() || null,
            role: role.value,
            disabled: disabled.input.checked,
            mustChangePassword: mustChange.input.checked,
          };
          if (password.value) body.password = password.value;
          try {
            const saved = creating
              ? await api.post('api/admin/web-users', body)
              : await api.put(`api/admin/web-users/${seg(u.name)}`, body);
            toast(creating ? t('webUsers.created', { name: body.name }) : t('webUsers.saved', { name: body.name }), { kind: 'success' });
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
            dialog.showError(accountError(err));
            return false;
          }
        },
      },
    ],
  });
  (creating ? name : display).focus();
  return ctl.result;
}

export default {
  title: () => t('nav.webUsers'),
  async render(el, ctx) {
    const add = h('button', { type: 'button', class: 'btn btn-primary' }, icon('plus'), h('span', { text: t('webUsers.add') }));
    el.append(pageHeader({ title: t('nav.webUsers'), subtitle: t('webUsers.subtitle'), actions: add }));
    const body = h('div', {}, loading());
    el.appendChild(body);
    let users;
    try {
      users = await api.get('api/admin/web-users', { signal: ctx.signal });
    } catch (err) {
      if (isAbort(err)) return;
      replace(body, errorState(err, () => navigate('/settings/web-users')));
      return;
    }
    const list = itemList(t('nav.webUsers'));
    const reload = async () => {
      try {
        users = await api.get('api/admin/web-users', { signal: ctx.signal });
        render();
      } catch (err) {
        if (!isAbort(err)) toast(errorText(err), { kind: 'error' });
      }
    };

    async function remove(u) {
      const ok = await confirmDialog({
        title: t('webUsers.deleteTitle', { name: u.name }),
        message: t('webUsers.deleteText'),
        confirmLabel: t('common.delete'),
        danger: true,
      });
      if (!ok) return;
      try {
        await api.del(`api/admin/web-users/${seg(u.name)}`);
        toast(t('webUsers.deleted', { name: u.name }), { kind: 'success' });
        reload();
      } catch (err) {
        toast(accountError(err), { kind: 'error' });
      }
    }

    function row(u) {
      const isSelf = u.name.toLowerCase() === (auth.user?.name ?? '').toLowerCase();
      return itemRow({
        title: u.name,
        onOpen: () => editUser(u, reload),
        pills: [isSelf ? pill(t('webUsers.you'), 'primary') : null,
          u.disabled ? pill(t('webUsers.disabledShort'), 'critical') : null,
          u.mustChangePassword ? pill(t('webUsers.mustChangeShort'), 'warning') : null],
        meta: [u.displayName, roleText(u.role)].filter(Boolean).join(' · '),
        menu: rowMenu(u.name, [
          { label: t('common.edit'), onSelect: () => editUser(u, reload) },
          { separator: true },
          { label: t('common.delete'), description: isSelf ? t('webUsers.self') : null, danger: !isSelf, disabled: isSelf, onSelect: () => remove(u) },
        ]),
      });
    }

    function render() {
      const sorted = users.slice().sort((a, b) => a.name.localeCompare(b.name));
      replace(list, sorted.length ? sorted.map(row) : h('li', { class: 'item-empty muted', text: t('webUsers.empty') }));
    }

    render();
    add.addEventListener('click', () => editUser(null, reload));
    replace(body, h('div', { class: 'card list-card' }, list), h('p', { class: 'muted small list-hint', text: t('webUsers.listHint') }));
  },
};
