// Server identity: the name and location shown in the panel, notifications and NUT clients.
import { h, replace } from '../../dom.js';
import { t } from '../../i18n.js';
import { api, isAbort } from '../../api.js';
import { navigate } from '../../router.js';
import { loadOverview } from '../../store.js';
import { refreshAuth } from '../../auth.js';
import { pageHeader, section, loading, errorState } from '../../page.js';
import { field, textInput, formErrorBox, setFieldError, clearErrors } from '../../components/fields.js';
import { loadSettings, trackDirty, saveBar, saveWithErrors, saveButton } from './common.js';

export default {
  title: () => t('nav.serverIdentity'),
  async render(el, ctx) {
    el.append(pageHeader({ title: t('nav.serverIdentity'), subtitle: t('identity.subtitle') }));
    const body = h('div', {}, loading());
    el.appendChild(body);
    let s;
    try {
      s = (await loadSettings(ctx.signal)).server;
    } catch (err) {
      if (isAbort(err)) return;
      replace(body, errorState(err, () => navigate('/settings/identity')));
      return;
    }
    const errors = formErrorBox();
    const name = textInput({ value: s.name ?? '', maxlength: 100, autocomplete: 'off', required: true });
    const location = textInput({ value: s.location ?? '', maxlength: 200, autocomplete: 'off', placeholder: t('identity.locationPlaceholder') });
    const nameField = field({ label: t('common.name'), control: name, name: 'name', required: true, help: t('identity.nameHelp') });
    const submit = saveButton();
    const form = h('form', { class: 'form', novalidate: true },
      errors.el,
      section({}, h('div', { class: 'form-grid' }, nameField,
        field({ label: t('identity.location'), control: location, name: 'location', help: t('identity.locationHelp') }))),
      saveBar(submit));
    const dirty = trackDirty(form, ctx);
    replace(body, form);
    form.addEventListener('submit', async (event) => {
      event.preventDefault();
      clearErrors(form);
      if (!name.value.trim()) {
        setFieldError(nameField, t('validate.required'));
        name.focus();
        return;
      }
      const saved = await saveWithErrors({
        form, errorBox: errors, button: submit, success: t('settings.saved'),
        request: () => api.put('api/admin/settings/server', { name: name.value.trim(), location: location.value.trim() || null }),
      });
      if (saved) {
        dirty.clean();
        loadOverview().catch(() => {});
        refreshAuth().catch(() => {});
      }
    });
  },
};
