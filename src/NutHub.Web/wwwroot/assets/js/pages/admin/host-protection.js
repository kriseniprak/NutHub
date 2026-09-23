// Host protection: when the configured UPSes can no longer power this machine, NutHub sets FSD, waits for NUT
// secondaries, optionally powers the UPS off and shuts this machine down. The copy makes the risk explicit and
// the live status offers "Cancel shutdown".
import { h, replace, setText } from '../../dom.js';
import { icon } from '../../icons.js';
import { t } from '../../i18n.js';
import { api, isAbort } from '../../api.js';
import { state, on } from '../../store.js';
import { hasRole } from '../../auth.js';
import { navigate } from '../../router.js';
import { pageHeader, section, loading, errorState, callout, definitionList } from '../../page.js';
import {
  field, textInput, numberInput, switchField, checkbox, formErrorBox, setFieldError, clearErrors, numberValue,
} from '../../components/fields.js';
import { confirmDialog } from '../../components/dialog.js';
import { severityBadge, pill } from '../../components/badge.js';
import { countdown, dateTime } from '../../format.js';
import { hostStateText, hostStateSeverity } from '../../labels.js';
import { cancelShutdown } from '../../shell.js';
import {
  trackDirty, saveBar, advancedGroup, saveWithErrors, saveButton, checkInt, intValue,
} from './common.js';

const POWER_OFF_COMMANDS = ['shutdown.return', 'shutdown.stayoff', 'load.off.delay', 'load.off'];

function statusPanel(ctx, initial) {
  const body = h('div');
  let timer = null;
  function render(status) {
    clearInterval(timer);
    if (!status) {
      replace(body, h('p', { class: 'muted', text: '–' }));
      return;
    }
    const pending = ['pending', 'waitingForSecondaries', 'shuttingDown'].includes(status.state);
    const countdownEl = h('strong', { class: 'hp-countdown' });
    const tick = () => {
      if (!status.shutdownAt) {
        setText(countdownEl, '–');
        return;
      }
      setText(countdownEl, countdown((Date.parse(status.shutdownAt) - Date.now()) / 1000));
    };
    const cancel = pending && status.state !== 'shuttingDown' && hasRole('operator')
      ? h('button', { type: 'button', class: 'btn btn-danger' }, icon('xCircle'), h('span', { text: t('hp.cancel') }))
      : null;
    cancel?.addEventListener('click', () => cancelShutdown(cancel));
    replace(body,
      h('div', { class: 'row hp-status-row' },
        severityBadge(hostStateSeverity(status.state), hostStateText(status.state)),
        status.dryRun ? pill(t('hp.dryRunPill'), 'info') : null,
        cancel),
      definitionList([
        [t('hp.reason'), status.reason || '–'],
        pending ? [t('hp.shutdownIn'), countdownEl] : null,
        status.shutdownAt ? [t('hp.shutdownAt'), dateTime(status.shutdownAt)] : null,
        [t('hp.monitored'), (status.ups ?? []).join(', ') || '–'],
        [t('hp.criticalUps'), (status.criticalUps ?? []).join(', ') || t('common.none')],
      ]));
    if (pending) {
      tick();
      timer = setInterval(tick, 1000);
    }
  }
  render(initial);
  ctx.onCleanup(() => clearInterval(timer));
  ctx.onCleanup(on('hostProtection', render));
  return section({ title: t('hp.statusTitle'), className: 'hp-status' }, body);
}

export default {
  title: () => t('nav.hostProtection'),
  async render(el, ctx) {
    el.append(pageHeader({ title: t('nav.hostProtection'), subtitle: t('hp.subtitle') }));
    const body = h('div', {}, loading());
    el.appendChild(body);
    let data;
    try {
      data = await api.get('api/admin/host-protection', { signal: ctx.signal });
    } catch (err) {
      if (isAbort(err)) return;
      replace(body, errorState(err, () => navigate('/settings/host-protection')));
      return;
    }
    const s = data.settings ?? {};
    const errors = formErrorBox();

    const enabled = switchField({ label: t('hp.enabled'), checked: s.enabled, name: 'enabled', help: t('hp.enabledHelp') });
    const dryRun = switchField({ label: t('hp.dryRun'), checked: s.dryRun, name: 'dryRun', help: t('hp.dryRunHelp') });

    const upsNames = [...new Set([...state.ups.map((u) => u.name), ...(s.ups ?? [])])];
    const chosen = new Set((s.ups ?? []).map((x) => x.toLowerCase()));
    const upsBoxes = upsNames.map((n) => checkbox({ label: n, value: n, checked: chosen.has(n.toLowerCase()) }));
    const minSupplies = numberInput({ value: s.minimumSupplies ?? 1, min: 1, step: 1 });
    const minField = field({ label: t('hp.minimumSupplies'), control: minSupplies, name: 'minimumSupplies', help: t('hp.minimumSuppliesHelp'), className: 'field-narrow' });
    const upsField = h('div', { class: 'field', dataset: { field: 'ups' } },
      h('span', { class: 'field-label', text: t('hp.upsList') }),
      upsBoxes.length ? h('div', { class: 'check-grid' }, upsBoxes) : h('p', { class: 'muted', text: t('nutUsers.noUps') }));

    const onLow = switchField({ label: t('hp.onLowBattery'), checked: s.onLowBattery, name: 'onLowBattery', help: t('hp.onLowBatteryHelp') });
    const chargeBelow = numberInput({ value: s.batteryChargeBelow ?? '', min: 0, max: 100, step: 'any', placeholder: t('common.off') });
    const runtimeBelow = numberInput({ value: s.runtimeBelowSeconds ?? '', min: 0, step: 1, placeholder: t('common.off') });
    const onBatteryLonger = numberInput({ value: s.onBatteryLongerThanSeconds ?? '', min: 0, step: 1, placeholder: t('common.off') });
    const onFsd = switchField({ label: t('hp.onForcedShutdown'), checked: s.onForcedShutdown, name: 'onForcedShutdown', help: t('hp.onForcedShutdownHelp') });
    const commLost = numberInput({ value: s.communicationLostOnBatterySeconds ?? 15, min: 0, step: 1 });
    const chargeField = field({ label: t('hp.chargeBelow'), control: chargeBelow, name: 'batteryChargeBelow', help: t('hp.optionalHelp'), className: 'field-narrow' });
    const runtimeField = field({ label: t('hp.runtimeBelow'), control: runtimeBelow, name: 'runtimeBelowSeconds', help: t('hp.optionalHelp'), className: 'field-narrow' });
    const longerField = field({ label: t('hp.onBatteryLonger'), control: onBatteryLonger, name: 'onBatteryLongerThanSeconds', help: t('hp.optionalHelp'), className: 'field-narrow' });
    const commField = field({ label: t('hp.commLost'), control: commLost, name: 'communicationLostOnBatterySeconds', help: t('hp.commLostHelp'), className: 'field-narrow' });

    const grace = numberInput({ value: s.shutdownDelaySeconds ?? 0, min: 0, step: 1 });
    const notify = switchField({ label: t('hp.notifySecondaries'), checked: s.notifySecondaries, name: 'notifySecondaries', help: t('hp.notifySecondariesHelp') });
    const secondariesWait = numberInput({ value: s.secondariesTimeoutSeconds ?? 15, min: 0, step: 1 });
    const command = textInput({ value: s.shutdownCommand ?? '', placeholder: data.defaultShutdownCommand ?? '', className: 'input-mono', autocomplete: 'off' });
    const graceField = field({ label: t('hp.grace'), control: grace, name: 'shutdownDelaySeconds', help: t('hp.graceHelp'), className: 'field-narrow' });
    const waitField = field({ label: t('hp.secondariesWait'), control: secondariesWait, name: 'secondariesTimeoutSeconds', help: t('hp.secondariesWaitHelp'), className: 'field-narrow' });

    const powerOff = switchField({ label: t('hp.powerOff'), checked: s.powerOffUps, name: 'powerOffUps', help: t('hp.powerOffHelp') });
    const pcList = `pc-${Math.random().toString(36).slice(2)}`;
    const powerOffCommand = textInput({ value: s.powerOffCommand ?? 'shutdown.return', list: pcList, className: 'input-mono', autocomplete: 'off' });
    const powerOffDelay = numberInput({ value: s.powerOffDelaySeconds ?? 120, min: 0, step: 1 });
    const powerOffDelayField = field({ label: t('hp.powerOffDelay'), control: powerOffDelay, name: 'powerOffDelaySeconds', help: t('hp.powerOffDelayHelp'), className: 'field-narrow' });
    const powerOffFields = h('div', { class: 'form-grid' },
      field({ label: t('hp.powerOffCommand'), control: powerOffCommand, name: 'powerOffCommand', help: t('hp.powerOffCommandHelp') }),
      h('datalist', { id: pcList }, POWER_OFF_COMMANDS.map((c) => h('option', { value: c }))),
      powerOffDelayField);
    const syncPowerOff = () => {
      powerOffFields.hidden = !powerOff.input.checked;
    };
    powerOff.input.addEventListener('change', syncPowerOff);
    syncPowerOff();

    const submit = saveButton();
    const form = h('form', { class: 'form', novalidate: true },
      errors.el,
      section({ title: t('hp.general') }, enabled, dryRun),
      section({ title: t('hp.supplies'), description: t('hp.suppliesHint') }, upsField, minField),
      section({ title: t('hp.conditions'), description: t('hp.conditionsHint') },
        onLow, h('div', { class: 'form-grid' }, chargeField, runtimeField, longerField, commField), onFsd),
      advancedGroup(t('settings.advanced'),
        section({ title: t('hp.sequence'), description: t('hp.sequenceHint') },
          h('div', { class: 'form-grid' }, graceField, waitField), notify,
          field({ label: t('hp.shutdownCommand'), control: command, name: 'shutdownCommand', help: t('hp.shutdownCommandHelp', { command: data.defaultShutdownCommand ?? '' }) })),
        section({ title: t('hp.powerOffTitle'), description: t('hp.powerOffHint') }, powerOff, powerOffFields)),
      saveBar(submit));
    const dirty = trackDirty(form, ctx);

    replace(body,
      // The warning stays in sight; its longer explanation opens on request.
      callout('critical', t('hp.riskTitle'), h('p', { text: t('hp.riskAdvice') }),
        h('details', { class: 'callout-more' }, h('summary', { text: t('common.moreInfo') }), h('p', { text: t('hp.riskText') }))),
      statusPanel(ctx, data.status),
      form);

    form.addEventListener('submit', async (event) => {
      event.preventDefault();
      clearErrors(form);
      const selectedUps = upsBoxes.map((b) => b.querySelector('input')).filter((i) => i.checked).map((i) => i.value);
      let ok = true;
      const checks = [
        [minField, minSupplies, { min: 1, max: Math.max(1, selectedUps.length) }, true],
        [runtimeField, runtimeBelow, { min: 0 }, false],
        [longerField, onBatteryLonger, { min: 0 }, false],
        [commField, commLost, { min: 0 }, true],
        [graceField, grace, { min: 0 }, true],
        [waitField, secondariesWait, { min: 0 }, true],
        [powerOffDelayField, powerOffDelay, { min: 0 }, true],
      ];
      for (const [f, input, range, required] of checks) {
        const message = checkInt(input, { ...range, required });
        setFieldError(f, message);
        if (message) ok = false;
      }
      const charge = numberValue(chargeBelow);
      if (Number.isNaN(charge) || (charge !== null && (charge < 0 || charge > 100))) {
        setFieldError(chargeField, t('validate.range', { min: 0, max: 100 }));
        ok = false;
      }
      if (enabled.input.checked && !selectedUps.length) {
        setFieldError(upsField, t('hp.needUps'));
        ok = false;
      }
      if (!ok) {
        errors.show(t('form.fixErrors'));
        return;
      }
      const payload = {
        enabled: enabled.input.checked,
        ups: selectedUps,
        minimumSupplies: intValue(minSupplies),
        onLowBattery: onLow.input.checked,
        batteryChargeBelow: charge,
        runtimeBelowSeconds: intValue(runtimeBelow),
        onBatteryLongerThanSeconds: intValue(onBatteryLonger),
        onForcedShutdown: onFsd.input.checked,
        communicationLostOnBatterySeconds: intValue(commLost),
        notifySecondaries: notify.input.checked,
        secondariesTimeoutSeconds: intValue(secondariesWait),
        shutdownDelaySeconds: intValue(grace),
        shutdownCommand: command.value.trim() || null,
        powerOffUps: powerOff.input.checked,
        powerOffCommand: powerOffCommand.value.trim() || 'shutdown.return',
        powerOffDelaySeconds: intValue(powerOffDelay),
        dryRun: dryRun.input.checked,
      };
      if (payload.enabled && !payload.dryRun && !(s.enabled && !s.dryRun)) {
        const confirmed = await confirmDialog({
          title: t('hp.armTitle'),
          message: t('hp.armText', { ups: selectedUps.join(', ') }),
          confirmLabel: t('hp.arm'),
          danger: true,
        });
        if (!confirmed) return;
      }
      const saved = await saveWithErrors({
        form, errorBox: errors, button: submit, success: t('settings.saved'),
        request: () => api.put('api/admin/host-protection', payload),
      });
      if (saved) {
        dirty.clean();
        navigate('/settings/host-protection', { replace: true });
      }
    });
  },
};
