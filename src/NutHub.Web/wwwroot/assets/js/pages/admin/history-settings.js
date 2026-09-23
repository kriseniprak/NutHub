// History settings: sampling, retention and the recorded variables.
import { h, replace } from '../../dom.js';
import { t } from '../../i18n.js';
import { api, isAbort } from '../../api.js';
import { navigate } from '../../router.js';
import { pageHeader, section, loading, errorState } from '../../page.js';
import { field, numberInput, switchField, formErrorBox, setFieldError, clearErrors } from '../../components/fields.js';
import { listEditor } from '../../components/listeditor.js';
import { loadSettings, trackDirty, saveBar, saveWithErrors, saveButton, checkInt, intValue } from './common.js';

const SUGGESTIONS = ['battery.charge', 'battery.runtime', 'battery.voltage', 'battery.temperature', 'ups.load', 'ups.realpower',
  'ups.power', 'ups.temperature', 'input.voltage', 'input.frequency', 'input.current', 'output.voltage', 'output.current',
  'output.frequency', 'input.L1-N.voltage', 'input.L2-N.voltage', 'input.L3-N.voltage', 'output.L1.power.percent',
  'output.L2.power.percent', 'output.L3.power.percent'];
const VARIABLE_PATTERN = /^[A-Za-z0-9][A-Za-z0-9_-]*(\.[A-Za-z0-9_-]+)+$/;

export default {
  title: () => t('nav.history'),
  async render(el, ctx) {
    el.append(pageHeader({ title: t('nav.history'), subtitle: t('historySettings.subtitle') }));
    const body = h('div', {}, loading());
    el.appendChild(body);
    let s;
    try {
      s = (await loadSettings(ctx.signal)).history;
    } catch (err) {
      if (isAbort(err)) return;
      replace(body, errorState(err, () => navigate('/settings/history')));
      return;
    }
    const errors = formErrorBox();
    const enabled = switchField({ label: t('historySettings.enabled'), checked: s.enabled, name: 'enabled', help: t('historySettings.enabledHelp') });
    const interval = numberInput({ value: s.sampleIntervalSeconds, min: 1, max: 3600, step: 1 });
    const retention = numberInput({ value: s.retentionDays, min: 1, max: 3650, step: 1 });
    const eventRetention = numberInput({ value: s.eventRetentionDays, min: 1, max: 3650, step: 1 });
    const variables = listEditor({
      values: s.variables ?? [],
      placeholder: 'battery.charge',
      label: t('upsEdit.variable'),
      name: 'variables',
      suggestions: SUGGESTIONS,
      validate: (v) => (VARIABLE_PATTERN.test(v) ? null : t('upsEdit.badVariable')),
      addLabel: t('historySettings.addVariable'),
    });
    const intervalField = field({ label: t('historySettings.interval'), control: interval, name: 'sampleIntervalSeconds', help: t('historySettings.intervalHelp'), className: 'field-narrow' });
    const retentionField = field({ label: t('historySettings.retention'), control: retention, name: 'retentionDays', help: t('historySettings.retentionHelp'), className: 'field-narrow' });
    const eventField = field({ label: t('historySettings.eventRetention'), control: eventRetention, name: 'eventRetentionDays', className: 'field-narrow' });
    const submit = saveButton();
    const form = h('form', { class: 'form', novalidate: true },
      errors.el,
      section({ title: t('historySettings.sampling') }, enabled, h('div', { class: 'form-grid' }, intervalField, retentionField, eventField)),
      section({ title: t('historySettings.variables'), description: t('historySettings.variablesHint') }, variables.el),
      saveBar(submit));
    const dirty = trackDirty(form, ctx);
    replace(body, form);

    form.addEventListener('submit', async (event) => {
      event.preventDefault();
      clearErrors(form);
      let ok = variables.validate();
      for (const [f, input, range] of [[intervalField, interval, { min: 1 }], [retentionField, retention, { min: 1 }], [eventField, eventRetention, { min: 1 }]]) {
        const message = checkInt(input, range);
        setFieldError(f, message);
        if (message) ok = false;
      }
      if (!ok) {
        errors.show(t('form.fixErrors'));
        return;
      }
      const payload = {
        enabled: enabled.input.checked,
        sampleIntervalSeconds: intValue(interval),
        retentionDays: intValue(retention),
        eventRetentionDays: intValue(eventRetention),
        variables: variables.getValues(),
      };
      const saved = await saveWithErrors({
        form, errorBox: errors, button: submit, success: t('settings.saved'),
        request: () => api.put('api/admin/settings/history', payload),
      });
      if (saved) dirty.clean();
    });
  },
};
