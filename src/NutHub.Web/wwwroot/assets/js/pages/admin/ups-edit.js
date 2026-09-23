// Add / edit a UPS: driver choice (cards), device discovery, options generated from the driver schema, general
// settings, low-battery policy and variable overrides. Server validation errors land on their fields.
import { h, replace, seg } from '../../dom.js';
import { icon } from '../../icons.js';
import { t, tEnum } from '../../i18n.js';
import { api, isAbort, errorText } from '../../api.js';
import { navigate } from '../../router.js';
import { pageHeader, loading, errorState, section, callout } from '../../page.js';
import {
  field, textInput, numberInput, switchField, formErrorBox, setFieldError, numberValue, clearErrors,
} from '../../components/fields.js';
import { optionsForm } from '../../components/optionsform.js';
import { kvEditor } from '../../components/kveditor.js';
import { toast } from '../../components/toast.js';
import { loadDrivers, trackDirty, saveBar, advancedGroup, saveWithErrors, saveButton } from './common.js';

const NAME_PATTERN = /^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$/;
const VARIABLE_PATTERN = /^[A-Za-z0-9][A-Za-z0-9_-]*(\.[A-Za-z0-9_-]+)+$/;
const OVERRIDE_SUGGESTIONS = ['battery.charge.low', 'battery.charge.warning', 'battery.runtime.low', 'ups.delay.shutdown',
  'ups.delay.start', 'input.transfer.low', 'input.transfer.high', 'battery.date', 'battery.mfr.date', 'ups.id', 'ups.mfr', 'ups.model'];

let serialPortsPromise = null;
function serialPorts(force) {
  if (force || !serialPortsPromise) {
    serialPortsPromise = api.get('api/admin/serial-ports', { timeout: 15000 }).then((p) => (Array.isArray(p) ? p : []));
    serialPortsPromise.catch(() => {
      serialPortsPromise = null;
    });
  }
  return serialPortsPromise;
}

function platformText(p) {
  return tEnum('platform', p);
}

export default {
  title: (params) => (params.name ? t('upsEdit.editTitle', { name: params.name }) : t('upsEdit.addTitle')),
  async render(el, ctx) {
    const editing = ctx.params.name ?? null;
    el.append(pageHeader({
      title: editing ? t('upsEdit.editTitle', { name: editing }) : t('upsEdit.addTitle'),
      subtitle: editing ? null : t('upsEdit.addSubtitle'),
    }));
    const body = h('div', {}, loading());
    el.appendChild(body);

    let drivers;
    let config = null;
    try {
      [drivers, config] = await Promise.all([
        loadDrivers(ctx.signal),
        editing ? api.get('api/admin/ups', { signal: ctx.signal }).then((list) => {
          const found = list.find((c) => c.name.toLowerCase() === editing.toLowerCase());
          if (!found) throw Object.assign(new Error(t('ups.notFoundText', { name: editing })), { code: 'notFound' });
          return found;
        }) : Promise.resolve(null),
      ]);
    } catch (err) {
      if (isAbort(err)) return;
      replace(body, errorState(err, () => navigate(ctx.params.name ? `/settings/ups/${encodeURIComponent(editing)}/edit` : '/settings/ups/new')));
      return;
    }

    const errors = formErrorBox();
    let driverId = config?.driver ?? drivers.find((d) => d.supported)?.id ?? drivers[0]?.id;
    let opts = null;
    const optionsHost = h('div');
    const discoverHost = h('div', { class: 'discover' });

    // ---- Driver cards ----
    const driverGroup = h('div', { class: 'driver-cards', role: 'radiogroup', 'aria-label': t('upsEdit.driver') });
    for (const d of drivers) {
      const input = h('input', { type: 'radio', name: 'driver', value: d.id, class: 'sr-only', checked: d.id === driverId, disabled: !d.supported && d.id !== config?.driver });
      const reason = d.supported ? null : t('upsEdit.unsupported', { platforms: (d.platforms ?? []).map(platformText).join(', ') || '–' });
      const card = h('label', { class: ['driver-card', !d.supported && 'is-unsupported'] },
        input,
        h('span', { class: 'driver-card-head' },
          h('span', { class: 'driver-card-title', text: d.displayName }),
          h('span', { class: 'driver-card-check', 'aria-hidden': 'true' }, icon('check', { className: 'icon-sm' }))),
        h('span', { class: 'driver-card-desc', text: d.description ?? '' }),
        reason ? h('span', { class: 'driver-card-reason' }, icon('info', { className: 'icon-sm' }), reason) : null,
        d.supportsDiscovery && d.supported ? h('span', { class: 'pill pill-info', text: t('upsEdit.discoverable') }) : null);
      input.addEventListener('change', () => {
        if (input.checked) selectDriver(d.id);
      });
      driverGroup.appendChild(card);
    }

    function currentDriver() {
      return drivers.find((d) => d.id === driverId);
    }

    function buildOptions(carry) {
      const d = currentDriver();
      const sameAsStored = config && config.driver === driverId;
      opts = optionsForm({
        options: d?.options ?? [],
        values: carry ?? (sameAsStored ? config.options : {}),
        secretsSet: sameAsStored ? config.secretsSet : [],
        loadSerialPorts: serialPorts,
        onChange: () => dirty.mark(),
      });
      replace(optionsHost, opts.el);
      renderDiscover();
    }

    function selectDriver(id) {
      if (id === driverId) return;
      const previous = opts?.getOptions() ?? {};
      driverId = id;
      const d = currentDriver();
      const keys = new Set((d?.options ?? []).map((o) => o.key.toLowerCase()));
      const carry = Object.fromEntries(Object.entries(previous).filter(([k]) => keys.has(k.toLowerCase())));
      buildOptions(config && config.driver === id ? { ...config.options, ...carry } : carry);
      dirty.mark();
    }

    // ---- Discovery ----
    let discoverController = null;
    ctx.onCleanup(() => discoverController?.abort());

    function renderDiscover(results) {
      const d = currentDriver();
      if (!d?.supportsDiscovery || !d.supported) {
        replace(discoverHost);
        return;
      }
      const button = h('button', { type: 'button', class: 'btn' }, icon('search'), h('span', { text: t('upsEdit.discover') }));
      button.addEventListener('click', discover);
      const children = [h('div', { class: 'row' }, button, h('span', { class: 'muted small', text: t('upsEdit.discoverHint') }))];
      if (results) children.push(results);
      replace(discoverHost, children);
    }

    async function discover() {
      discoverController?.abort();
      discoverController = new AbortController();
      const id = driverId;
      const cancel = h('button', { type: 'button', class: 'btn btn-sm', text: t('common.cancel') });
      cancel.addEventListener('click', () => discoverController.abort());
      replace(discoverHost, h('div', { class: 'row discover-running', role: 'status' },
        h('span', { class: 'spinner', 'aria-hidden': 'true' }), h('span', { text: t('upsEdit.discovering') }), cancel));
      try {
        const result = await api.post(`api/admin/drivers/${seg(id)}/discover`, undefined, { signal: discoverController.signal, timeout: 45000 });
        if (id !== driverId) return;
        const devices = result?.devices ?? [];
        const list = devices.length
          ? h('ul', { class: 'discover-list', 'aria-label': t('upsEdit.found') }, devices.map((dev) => {
            const pick = h('button', { type: 'button', class: 'discover-item' },
              icon('battery'),
              h('span', { class: 'discover-text' }, h('strong', { text: dev.title }), dev.detail ? h('span', { text: dev.detail }) : null),
              h('span', { class: 'btn btn-sm btn-primary', text: t('upsEdit.use') }));
            pick.addEventListener('click', () => {
              opts.setValues(dev.options ?? {});
              if (!nameInput.value.trim() && dev.suggestedName) nameInput.value = dev.suggestedName;
              if (!descInput.value.trim() && dev.title) descInput.value = dev.title;
              dirty.mark();
              toast(t('upsEdit.filled', { device: dev.title }), { kind: 'success' });
            });
            return h('li', {}, pick);
          }))
          : callout('info', t('upsEdit.noneFound'), h('p', { text: t('upsEdit.noneFoundHint') }));
        renderDiscover(h('div', { class: 'discover-results' }, h('p', { class: 'small muted', text: t('upsEdit.foundCount', { count: devices.length }) }), list));
      } catch (err) {
        if (id !== driverId) return;
        renderDiscover(isAbort(err) ? null : callout('warning', t('upsEdit.discoverFailed'), h('p', { text: errorText(err) })));
      }
    }

    // ---- General ----
    const nameInput = textInput({ value: config?.name ?? '', autocomplete: 'off', maxlength: 64, required: true });
    const descInput = textInput({ value: config?.description ?? '', autocomplete: 'off', maxlength: 200 });
    const enabled = switchField({ label: t('upsEdit.enabled'), checked: config ? config.enabled : true, name: 'enabled', help: t('upsEdit.enabledHelp') });
    const poll = numberInput({ value: config?.pollIntervalSeconds ?? 2, min: 0.5, max: 300, step: 'any', required: true });
    const nameField = field({ label: t('common.name'), control: nameInput, name: 'name', required: true, help: t('upsEdit.nameHelp') });
    const pollField = field({ label: t('upsEdit.poll'), control: poll, name: 'pollIntervalSeconds', help: t('upsEdit.pollHelp'), className: 'field-narrow' });

    // ---- Low battery ----
    const lb = config?.lowBattery ?? {};
    const chargeLow = numberInput({ value: lb.chargePercent ?? '', min: 0, max: 100, step: 'any', placeholder: t('upsEdit.fromDevice') });
    const runtimeLow = numberInput({ value: lb.runtimeSeconds ?? '', min: 0, step: 1, placeholder: t('upsEdit.fromDevice') });
    const ignoreFlag = switchField({ label: t('upsEdit.ignoreDeviceFlag'), checked: !!lb.ignoreDeviceFlag, name: 'lowBattery.ignoreDeviceFlag', help: t('upsEdit.ignoreDeviceFlagHelp') });
    const chargeField = field({ label: t('upsEdit.chargeLow'), control: chargeLow, name: 'lowBattery.chargePercent', help: t('upsEdit.chargeLowHelp'), className: 'field-narrow' });
    const runtimeField = field({ label: t('upsEdit.runtimeLow'), control: runtimeLow, name: 'lowBattery.runtimeSeconds', help: t('upsEdit.runtimeLowHelp'), className: 'field-narrow' });

    // ---- Overrides ----
    const overrides = kvEditor({
      entries: Object.entries(config?.overrides ?? {}),
      keyLabel: t('upsEdit.variable'),
      valueLabel: t('common.value'),
      keyPlaceholder: 'battery.charge.low',
      valuePlaceholder: '30',
      name: 'overrides',
      keySuggestions: OVERRIDE_SUGGESTIONS,
      validateKey: (key) => (VARIABLE_PATTERN.test(key) ? null : t('upsEdit.badVariable')),
      addLabel: t('upsEdit.addOverride'),
    });

    const submit = saveButton(editing ? t('common.save') : t('upsEdit.create'));
    const form = h('form', { class: 'form ups-form', novalidate: true },
      errors.el,
      section({ title: t('upsEdit.driver'), description: t('upsEdit.driverHint') }, driverGroup),
      section({ title: t('upsEdit.connection'), description: t('upsEdit.connectionHint') }, discoverHost, optionsHost),
      section({ title: t('upsEdit.general') }, h('div', { class: 'form-grid' }, nameField,
        field({ label: t('common.description'), control: descInput, name: 'description' }), pollField, enabled)),
      advancedGroup(t('settings.advanced'),
        section({ title: t('upsEdit.lowBattery'), description: t('upsEdit.lowBatteryHint') },
          h('div', { class: 'form-grid' }, chargeField, runtimeField, ignoreFlag)),
        section({ title: t('upsEdit.overrides'), description: t('upsEdit.overridesHint') }, h('div', { dataset: { field: 'overrides' }, class: 'field' }, overrides.el))),
      // A new UPS always shows its Create button; an existing one shows the bar once something changed.
      saveBar(editing ? submit : [h('a', { class: 'btn', href: '#/settings/ups', text: t('common.cancel') }), submit], { always: !editing }));
    replace(body, form);
    const dirty = trackDirty(form, ctx);
    buildOptions(null);

    function validate() {
      clearErrors(form);
      let ok = opts.validate();
      const name = nameInput.value.trim();
      if (!name) {
        setFieldError(nameField, t('validate.required'));
        ok = false;
      } else if (!NAME_PATTERN.test(name)) {
        setFieldError(nameField, t('upsEdit.badName'));
        ok = false;
      }
      const p = numberValue(poll);
      if (p === null || Number.isNaN(p) || p <= 0) {
        setFieldError(pollField, t('validate.positive'));
        ok = false;
      }
      const c = numberValue(chargeLow);
      if (Number.isNaN(c) || (c !== null && (c < 0 || c > 100))) {
        setFieldError(chargeField, t('validate.range', { min: 0, max: 100 }));
        ok = false;
      }
      const r = numberValue(runtimeLow);
      if (Number.isNaN(r) || (r !== null && (r < 0 || !Number.isInteger(r)))) {
        setFieldError(runtimeField, t('validate.nonNegativeInteger'));
        ok = false;
      }
      if (!overrides.validate()) ok = false;
      const d = currentDriver();
      if (!d) {
        errors.show(t('upsEdit.chooseDriver'));
        ok = false;
      }
      if (!ok) {
        form.querySelector('.has-error input, .has-error select')?.focus();
        if (!errors.el.hidden) return false;
        errors.show(t('form.fixErrors'));
      }
      return ok;
    }

    form.addEventListener('submit', async (event) => {
      event.preventDefault();
      if (!validate()) return;
      const payload = {
        name: nameInput.value.trim(),
        description: descInput.value.trim() || null,
        driver: driverId,
        enabled: enabled.input.checked,
        pollIntervalSeconds: numberValue(poll),
        options: opts.getOptions(),
        overrides: Object.fromEntries(overrides.getEntries()),
        lowBattery: {
          chargePercent: numberValue(chargeLow),
          runtimeSeconds: numberValue(runtimeLow),
          ignoreDeviceFlag: ignoreFlag.input.checked,
        },
      };
      const saved = await saveWithErrors({
        form,
        errorBox: errors,
        button: submit,
        request: () => (editing
          ? api.put(`api/admin/ups/${seg(config.name)}`, payload)
          : api.post('api/admin/ups', payload)),
        success: editing ? t('upsEdit.saved', { name: payload.name }) : t('upsEdit.created', { name: payload.name }),
      });
      if (saved) {
        dirty.clean();
        navigate('/settings/ups');
      }
    });
  },
};
