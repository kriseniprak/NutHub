// Web panel settings: address, HTTP/HTTPS ports and certificate, anonymous read access, sessions and allowed
// networks. Changes that can cut off the browser are confirmed first; after saving the panel follows the notice.
import { h, replace } from '../../dom.js';
import { t } from '../../i18n.js';
import { api, isAbort } from '../../api.js';
import { navigate } from '../../router.js';
import { refreshAuth } from '../../auth.js';
import { pageHeader, section, loading, errorState } from '../../page.js';
import {
  field, textInput, numberInput, switchField, formErrorBox, setFieldError, clearErrors, secretControl,
} from '../../components/fields.js';
import { listEditor, validateCidr } from '../../components/listeditor.js';
import { confirmDialog, openDialog } from '../../components/dialog.js';
import {
  loadSettings, trackDirty, saveBar, advancedGroup, saveWithErrors, saveButton, checkInt, intValue,
} from './common.js';

const RISKY = ['enabled', 'bindAddress', 'httpEnabled', 'httpPort', 'httpsEnabled', 'httpsPort', 'redirectHttpToHttps', 'certificatePath'];

/** Shows the server notice (new address) and moves there after a short countdown. */
function followNotice(notice) {
  const match = /(https?:\/\/[^\s"'<>]+)/i.exec(notice ?? '');
  const url = match ? match[1].replace(/[.,;)]+$/, '') : null;
  const remaining = h('strong', { text: '8' });
  let seconds = 8;
  const content = [h('p', { text: notice })];
  if (url) content.push(h('p', {}, t('web.redirecting'), ' ', remaining, ' s'), h('p', {}, h('a', { href: url, text: url })));
  const ctl = openDialog({
    title: t('web.noticeTitle'),
    content,
    size: 'sm',
    actions: url ? [{ label: t('web.stay'), value: 'stay' }, { label: t('web.goNow'), kind: 'primary', value: 'go' }]
      : [{ label: t('common.ok'), kind: 'primary', value: 'stay' }],
  });
  let timer = null;
  if (url) {
    timer = setInterval(() => {
      seconds -= 1;
      remaining.textContent = String(Math.max(0, seconds));
      if (seconds <= 0) ctl.close('go');
    }, 1000);
  }
  ctl.result.then((choice) => {
    clearInterval(timer);
    if (url && choice === 'go') window.location.href = url;
  });
}

export default {
  title: () => t('nav.webPanel'),
  async render(el, ctx) {
    el.append(pageHeader({ title: t('nav.webPanel'), subtitle: t('web.subtitle') }));
    const body = h('div', {}, loading());
    el.appendChild(body);
    let s;
    try {
      s = (await loadSettings(ctx.signal)).web;
    } catch (err) {
      if (isAbort(err)) return;
      replace(body, errorState(err, () => navigate('/settings/web')));
      return;
    }
    const errors = formErrorBox();
    const enabled = switchField({ label: t('web.enabled'), checked: s.enabled, name: 'enabled', help: t('web.enabledHelp') });
    const bind = textInput({ value: s.bindAddress ?? '*', placeholder: '*', className: 'input-mono', autocomplete: 'off' });
    const httpEnabled = switchField({ label: t('web.httpEnabled'), checked: s.httpEnabled, name: 'httpEnabled' });
    const httpPort = numberInput({ value: s.httpPort, min: 1, max: 65535, step: 1 });
    const httpsEnabled = switchField({ label: t('web.httpsEnabled'), checked: s.httpsEnabled, name: 'httpsEnabled', help: t('web.httpsHelp') });
    const httpsPort = numberInput({ value: s.httpsPort, min: 1, max: 65535, step: 1 });
    const certPath = textInput({ value: s.certificatePath ?? '', placeholder: t('web.certPlaceholder'), className: 'input-mono', autocomplete: 'off' });
    const certPassword = secretControl({ isSet: !!s.certificatePasswordSet });
    const redirect = switchField({ label: t('web.redirect'), checked: s.redirectHttpToHttps, name: 'redirectHttpToHttps', help: t('web.redirectHelp') });
    const anonymous = switchField({ label: t('web.anonymous'), checked: s.allowAnonymousRead, name: 'allowAnonymousRead', help: t('web.anonymousHelp') });
    const sessionHours = numberInput({ value: s.sessionHours, min: 1, max: 8760, step: 1 });
    const networks = listEditor({ values: s.allowedNetworks, placeholder: '192.168.1.0/24', label: t('nut.network'), name: 'allowedNetworks', validate: validateCidr, addLabel: t('nut.addNetwork') });

    const httpPortField = field({ label: t('web.httpPort'), control: httpPort, name: 'httpPort', className: 'field-narrow' });
    const httpsPortField = field({ label: t('web.httpsPort'), control: httpsPort, name: 'httpsPort', className: 'field-narrow' });
    const sessionField = field({ label: t('web.sessionHours'), control: sessionHours, name: 'sessionHours', help: t('web.sessionHoursHelp'), className: 'field-narrow' });
    const httpsFields = h('div', { class: 'form-grid' },
      httpsPortField,
      field({ label: t('nut.certPath'), control: certPath, name: 'certificatePath', help: t('web.certPathHelp'), className: 'field-wide' }),
      field({ label: t('nut.certPassword'), control: certPassword.el, name: 'certificatePassword' }),
      redirect);
    const sync = () => {
      httpsFields.hidden = !httpsEnabled.input.checked;
      httpPortField.hidden = !httpEnabled.input.checked;
    };
    httpsEnabled.input.addEventListener('change', sync);
    httpEnabled.input.addEventListener('change', sync);
    sync();

    const submit = saveButton();
    const form = h('form', { class: 'form', novalidate: true },
      errors.el,
      section({ title: t('web.listening'), description: t('web.riskText') },
        enabled, httpEnabled, httpPortField, httpsEnabled, httpsFields),
      section({ title: t('web.access') }, anonymous,
        field({ label: t('nut.allowedNetworks'), control: networks.el, name: 'allowedNetworks', help: t('web.allowedNetworksHelp') })),
      advancedGroup(t('settings.advanced'), section({},
        h('div', { class: 'form-grid' }, field({ label: t('web.bind'), control: bind, name: 'bindAddress', help: t('web.bindHelp') }), sessionField))),
      saveBar(submit));
    const dirty = trackDirty(form, ctx);
    replace(body, form);

    form.addEventListener('submit', async (event) => {
      event.preventDefault();
      clearErrors(form);
      let ok = networks.validate();
      for (const [f, input, range, active] of [[httpPortField, httpPort, { min: 1, max: 65535 }, httpEnabled.input.checked],
        [httpsPortField, httpsPort, { min: 1, max: 65535 }, httpsEnabled.input.checked], [sessionField, sessionHours, { min: 1 }, true]]) {
        const message = active ? checkInt(input, range) : null;
        setFieldError(f, message);
        if (message) ok = false;
      }
      if (!httpEnabled.input.checked && !httpsEnabled.input.checked && enabled.input.checked) {
        errors.show(t('web.needProtocol'));
        return;
      }
      if (!ok) {
        errors.show(t('form.fixErrors'));
        return;
      }
      const payload = {
        enabled: enabled.input.checked,
        bindAddress: bind.value.trim() || '*',
        httpEnabled: httpEnabled.input.checked,
        httpPort: intValue(httpPort) ?? s.httpPort,
        httpsEnabled: httpsEnabled.input.checked,
        httpsPort: intValue(httpsPort) ?? s.httpsPort,
        certificatePath: certPath.value.trim() || null,
        redirectHttpToHttps: redirect.input.checked,
        allowAnonymousRead: anonymous.input.checked,
        sessionHours: intValue(sessionHours),
        allowedNetworks: networks.getValues(),
      };
      const pw = certPassword.value();
      if (pw !== undefined) payload.certificatePassword = pw;
      const risky = RISKY.some((k) => (payload[k] ?? null) !== (s[k] ?? null))
        || JSON.stringify(payload.allowedNetworks) !== JSON.stringify(s.allowedNetworks ?? []);
      if (risky) {
        const confirmed = await confirmDialog({
          title: t('web.confirmTitle'),
          message: payload.enabled ? t('web.confirmText') : t('web.confirmDisable'),
          confirmLabel: t('common.save'),
          danger: true,
        });
        if (!confirmed) return;
      }
      const result = await saveWithErrors({
        form, errorBox: errors, button: submit, success: t('settings.saved'),
        request: () => api.put('api/admin/settings/web', payload),
      });
      if (result) {
        dirty.clean();
        s = { ...s, ...payload, ...(result.settings?.web ?? result.settings ?? {}) };
        refreshAuth().catch(() => {});
        if (result.notice) followNotice(result.notice);
      }
    });
  },
};
