// Sign-in page. After a successful login the auth listener re-routes (to the forced password change, or to the
// page the visitor came from).
import { h } from '../dom.js';
import { icon } from '../icons.js';
import { t } from '../i18n.js';
import { auth, login } from '../auth.js';
import { errorText } from '../api.js';
import { field, textInput, passwordInput } from '../components/fields.js';

export default {
  title: () => t('auth.signIn'),
  render(el, ctx) {
    const username = textInput({ name: 'username', autocomplete: 'username', autocapitalize: 'none', required: true });
    const password = passwordInput({ name: 'password', autocomplete: 'current-password', required: true });
    const errorBox = h('div', { class: 'form-error', role: 'alert', hidden: true });
    const submit = h('button', { type: 'submit', class: 'btn btn-primary btn-block' }, icon('login'), h('span', { text: t('auth.signIn') }));
    let lockTimer = null;
    ctx.onCleanup(() => clearInterval(lockTimer));

    function showError(text) {
      errorBox.replaceChildren(icon('warning'), h('span', { text }));
      errorBox.hidden = !text;
    }

    function lockFor(seconds) {
      let left = Math.max(1, Math.ceil(seconds));
      submit.disabled = true;
      const tick = () => {
        showError(t('error.rateLimitedFor', { seconds: left }));
        left -= 1;
        if (left < 0) {
          clearInterval(lockTimer);
          lockTimer = null;
          submit.disabled = false;
          showError('');
        }
      };
      tick();
      clearInterval(lockTimer);
      lockTimer = setInterval(tick, 1000);
    }

    const form = h('form', { class: 'form', novalidate: true },
      errorBox,
      field({ label: t('auth.username'), control: username }),
      field({ label: t('auth.password'), control: password }),
      submit);

    form.addEventListener('submit', async (event) => {
      event.preventDefault();
      showError('');
      if (!username.value.trim() || !password.value) {
        showError(t('auth.enterBoth'));
        (username.value.trim() ? password : username).focus();
        return;
      }
      submit.disabled = true;
      submit.classList.add('is-busy');
      try {
        await login(username.value.trim(), password.value);
      } catch (err) {
        password.value = '';
        if (err.code === 'rateLimited' && err.retryAfter) {
          lockFor(err.retryAfter);
        } else {
          showError(err.code === 'invalidCredentials' ? t('auth.invalid') : errorText(err));
          password.focus();
        }
      } finally {
        submit.classList.remove('is-busy');
        if (!lockTimer) submit.disabled = false;
      }
    });

    const anonymous = auth.anonymousRead
      ? h('p', { class: 'login-alt' }, h('a', { href: '#/' }, t('auth.continueAnonymous')))
      : null;

    el.appendChild(h('div', { class: 'login-page' },
      h('div', { class: 'card login-card' },
        h('div', { class: 'login-brand' },
          h('img', { src: 'assets/img/logo.svg', alt: '', width: 56, height: 56 }),
          h('h1', { text: t('auth.signInTo', { name: auth.serverName || 'NutHub' }) }),
          h('p', { class: 'muted', text: t('auth.signInHint') })),
        form,
        anonymous),
      h('p', { class: 'login-foot faint', text: auth.version ? `NutHub ${auth.version}` : 'NutHub' })));
    username.focus();
  },
};
