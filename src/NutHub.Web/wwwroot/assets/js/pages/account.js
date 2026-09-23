// Account page: who is signed in and the password change, which is forced after an administrator reset or on
// the first sign-in of the initial admin account.
import { h, replace } from '../dom.js';
import { icon } from '../icons.js';
import { t } from '../i18n.js';
import { auth, refreshAuth, displayName, logout } from '../auth.js';
import { api, errorText } from '../api.js';
import { navigate } from '../router.js';
import { pageHeader, section, callout, definitionList } from '../page.js';
import { field, passwordInput, setFieldError, formErrorBox, applyFieldErrors } from '../components/fields.js';
import { toast } from '../components/toast.js';
import { roleText } from '../labels.js';

export const MIN_PASSWORD = 8;

/** Live checklist of the password rules; returns { el, check() -> bool }. */
export function passwordRules(newInput, confirmInput, currentInput) {
  const items = {
    length: h('li'),
    different: currentInput ? h('li') : null,
    match: h('li'),
  };
  const texts = {
    length: t('password.ruleLength', { count: MIN_PASSWORD }),
    different: t('password.ruleDifferent'),
    match: t('password.ruleMatch'),
  };
  const el = h('ul', { class: 'rules', 'aria-live': 'polite' }, Object.values(items));
  function mark(key, ok) {
    const li = items[key];
    if (!li) return;
    li.className = ok ? 'rule-ok' : 'rule-pending';
    li.replaceChildren(icon(ok ? 'check' : 'xCircle', { className: 'icon-sm' }),
      h('span', { text: texts[key] }), h('span', { class: 'sr-only', text: ok ? t('password.met') : t('password.notMet') }));
  }
  function check() {
    const value = newInput.value;
    const length = [...value].length >= MIN_PASSWORD;
    const different = currentInput ? value !== '' && value !== currentInput.value : true;
    const match = value !== '' && value === confirmInput.value;
    mark('length', length);
    mark('different', different);
    mark('match', match);
    return length && different && match;
  }
  for (const input of [newInput, confirmInput, currentInput]) input?.addEventListener('input', check);
  check();
  return { el, check };
}

export default {
  title: () => t('nav.account'),
  render(el) {
    const forced = !!auth.user?.mustChangePassword;
    const current = passwordInput({ autocomplete: 'current-password', required: true });
    const next = passwordInput({ autocomplete: 'new-password', required: true });
    const confirm = passwordInput({ autocomplete: 'new-password', required: true });
    const rules = passwordRules(next, confirm, current);
    const errors = formErrorBox();
    const submit = h('button', { type: 'submit', class: 'btn btn-primary' }, icon('key'), h('span', { text: t('password.change') }));

    const currentField = field({ label: t('password.current'), control: current, name: 'currentPassword', required: true });
    const nextField = field({ label: t('password.new'), control: next, name: 'newPassword', required: true });
    const confirmField = field({ label: t('password.confirm'), control: confirm, name: 'confirmPassword', required: true });
    const form = h('form', { class: 'form form-narrow', novalidate: true },
      errors.el,
      h('input', { type: 'text', name: 'username', autocomplete: 'username', value: auth.user?.name ?? '', hidden: true, 'aria-hidden': 'true', tabindex: '-1' }),
      currentField, nextField, rules.el, confirmField,
      h('div', { class: 'form-actions' }, submit,
        forced ? h('button', { type: 'button', class: 'btn btn-ghost', onClick: async () => { await logout(); navigate('/login'); } },
          icon('logout'), h('span', { text: t('menu.signOut') })) : null));

    form.addEventListener('submit', async (event) => {
      event.preventDefault();
      errors.show(null);
      for (const f of [currentField, nextField, confirmField]) setFieldError(f, null);
      if (!current.value) {
        setFieldError(currentField, t('validate.required'));
        current.focus();
        return;
      }
      if (!rules.check()) {
        if (next.value !== confirm.value) setFieldError(confirmField, t('password.ruleMatch'));
        else setFieldError(nextField, t('password.rulesNotMet'));
        next.focus();
        return;
      }
      submit.disabled = true;
      submit.classList.add('is-busy');
      try {
        await api.post('api/auth/password', { currentPassword: current.value, newPassword: next.value }, { quiet: true });
        toast(t('password.changed'), { kind: 'success' });
        current.value = '';
        next.value = '';
        confirm.value = '';
        rules.check();
        await refreshAuth();
        if (forced) navigate('/');
      } catch (err) {
        if (err.code === 'invalidCredentials') {
          setFieldError(currentField, t('password.wrongCurrent'));
          current.focus();
        } else if (err.code === 'validation') {
          errors.show(applyFieldErrors(form, err.fields));
        } else {
          errors.show(errorText(err));
        }
      } finally {
        submit.disabled = false;
        submit.classList.remove('is-busy');
      }
    });

    replace(el,
      pageHeader({ title: forced ? t('password.forcedTitle') : t('nav.account'), subtitle: forced ? null : displayName() }),
      forced ? callout('warning', t('password.forcedHeading'), h('p', { text: t('password.forcedText') })) : null,
      forced ? null : section({ title: t('account.profile') }, definitionList([
        [t('account.username'), auth.user?.name],
        [t('account.displayName'), auth.user?.displayName || '–'],
        [t('account.role'), roleText(auth.user?.role)],
      ])),
      section({ title: t('password.change'), description: t('password.changeHint') }, form),
    );
    if (forced) {
      el.querySelector('.callout')?.classList.add('mb');
      current.focus();
    }
  },
};
