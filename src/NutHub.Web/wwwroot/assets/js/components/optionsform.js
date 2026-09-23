// Driver options form generated from the DriverOption schema of GET api/admin/drivers: typed controls, defaults
// as placeholders, help texts, choices, bounds, an "Advanced" section, visibleWhen conditions and secrets.
import { h } from '../dom.js';
import { icon } from '../icons.js';
import { t } from '../i18n.js';
import {
  field, textInput, numberInput, selectInput, switchField, secretControl, setFieldError, advancedSection, numberValue,
} from './fields.js';

function isTrue(value) {
  return String(value).toLowerCase() === 'true';
}

/**
 * Builds the form. `values` are the stored options (strings), `secretsSet` the secret keys that have a value,
 * `loadSerialPorts()` resolves to the detected serial ports. Returns
 * { el, getOptions(), validate(), setValues(values), refreshVisibility() }.
 */
export function optionsForm({ options, values = {}, secretsSet = [], loadSerialPorts, onChange }) {
  const controls = new Map();
  const lowerValues = Object.fromEntries(Object.entries(values ?? {}).map(([k, v]) => [k.toLowerCase(), v]));
  const initial = (key) => lowerValues[key.toLowerCase()];
  const secretSet = new Set((secretsSet ?? []).map((k) => k.toLowerCase()));

  function build(option) {
    const stored = initial(option.key);
    const common = { name: `options.${option.key}`, label: option.label, help: option.help, required: option.required };
    let wrapper;
    let read;
    let write;
    switch (option.type) {
      case 'boolean': {
        const def = isTrue(option.default);
        wrapper = switchField({ label: option.label, checked: stored !== undefined ? isTrue(stored) : def, name: common.name, help: option.help, onChange });
        const input = wrapper.input;
        read = () => {
          if (stored === undefined && input.checked === def) return undefined;
          return input.checked ? 'true' : 'false';
        };
        write = (v) => {
          input.checked = v === undefined || v === null ? def : isTrue(v);
        };
        break;
      }
      case 'choice': {
        const choices = option.choices ?? [];
        const defChoice = choices.find((c) => String(c.value) === String(option.default));
        const list = [];
        if (!option.required || option.default !== null && option.default !== undefined) {
          list.push({ value: '', label: defChoice ? t('options.defaultChoice', { value: defChoice.label }) : t('options.notSet') });
        }
        for (const c of choices) list.push({ value: c.value, label: c.label });
        const select = selectInput({ options: list, value: stored ?? '' });
        select.addEventListener('change', () => onChange?.());
        wrapper = field({ ...common, control: select });
        read = () => select.value || undefined;
        write = (v) => {
          select.value = v ?? '';
        };
        break;
      }
      case 'secret': {
        const secret = secretControl({ isSet: secretSet.has(option.key.toLowerCase()), placeholder: option.default ?? '' });
        secret.input.addEventListener('input', () => onChange?.());
        wrapper = field({ ...common, control: secret.el });
        read = () => secret.value();
        write = (v) => {
          if (v !== undefined && v !== null) secret.input.value = v;
        };
        wrapper.secret = secret;
        break;
      }
      case 'integer':
      case 'decimal':
      case 'port': {
        const input = numberInput({
          value: stored ?? '',
          placeholder: option.default ?? '',
          min: option.type === 'port' ? 1 : option.min ?? null,
          max: option.type === 'port' ? 65535 : option.max ?? null,
          step: option.type === 'decimal' ? 'any' : 1,
          inputmode: option.type === 'decimal' ? 'decimal' : 'numeric',
        });
        input.addEventListener('input', () => onChange?.());
        wrapper = field({ ...common, control: input, className: 'field-narrow' });
        read = () => (input.value.trim() === '' ? undefined : input.value.trim().replace(',', '.'));
        write = (v) => {
          input.value = v ?? '';
        };
        wrapper.numberInput = input;
        break;
      }
      case 'serialPort': {
        const input = textInput({ value: stored ?? '', placeholder: option.default ?? (navigator.platform?.startsWith('Win') ? 'COM3' : '/dev/ttyUSB0'), autocomplete: 'off' });
        const picker = selectInput({ options: [{ value: '', label: t('options.detectedPorts') }], 'aria-label': t('options.detectedPorts'), className: 'port-picker' });
        const refresh = h('button', { type: 'button', class: 'btn btn-icon', 'aria-label': t('options.refreshPorts'), title: t('options.refreshPorts') }, icon('refresh'));
        async function fill() {
          if (!loadSerialPorts) return;
          refresh.disabled = true;
          try {
            const ports = await loadSerialPorts(true);
            picker.replaceChildren(h('option', { value: '', text: ports.length ? t('options.detectedPorts') : t('options.noPorts') }),
              ...ports.map((p) => h('option', { value: p, text: p })));
          } catch {
            picker.replaceChildren(h('option', { value: '', text: t('options.portsFailed') }));
          } finally {
            refresh.disabled = false;
          }
        }
        picker.addEventListener('change', () => {
          if (picker.value) {
            input.value = picker.value;
            picker.value = '';
            onChange?.();
            input.focus();
          }
        });
        input.addEventListener('input', () => onChange?.());
        refresh.addEventListener('click', fill);
        fill();
        wrapper = field({ ...common, control: h('div', { class: 'port-combo' }, input, picker, refresh) });
        read = () => input.value.trim() || undefined;
        write = (v) => {
          input.value = v ?? '';
        };
        break;
      }
      default: {
        const input = textInput({
          value: stored ?? '',
          placeholder: option.default ?? '',
          autocomplete: 'off',
          inputmode: option.type === 'host' ? 'url' : null,
        });
        if (option.type === 'filePath' || option.type === 'host') input.classList.add('input-mono');
        input.addEventListener('input', () => onChange?.());
        wrapper = field({ ...common, control: input });
        read = () => input.value.trim() || undefined;
        write = (v) => {
          input.value = v ?? '';
        };
      }
    }
    controls.set(option.key, { option, wrapper, read, write });
    wrapper.addEventListener('input', () => setFieldError(wrapper, null));
    wrapper.addEventListener('change', () => {
      setFieldError(wrapper, null);
      refreshVisibility();
    });
    return wrapper;
  }

  const basic = [];
  const advanced = [];
  for (const option of options ?? []) {
    (option.advanced ? advanced : basic).push(build(option));
  }
  const advancedHasValue = (options ?? []).some((o) => o.advanced && initial(o.key) !== undefined);
  const el = h('div', { class: 'options-form' },
    basic.length ? h('div', { class: 'form-grid' }, basic) : null,
    advanced.length ? advancedSection(t('options.advanced'), h('div', { class: 'form-grid' }, advanced), advancedHasValue) : null,
    !basic.length && !advanced.length ? h('p', { class: 'muted', text: t('options.none') }) : null);

  function effectiveValue(key) {
    const c = [...controls.values()].find((x) => x.option.key.toLowerCase() === key.toLowerCase());
    if (!c) return undefined;
    const v = c.read();
    return v === undefined ? c.option.default ?? undefined : v;
  }

  function isVisible(option) {
    const cond = option.visibleWhen;
    if (!cond) return true;
    const controller = [...controls.values()].find((x) => x.option.key.toLowerCase() === cond.key.toLowerCase());
    if (controller && !isVisible(controller.option)) return false;
    const v = effectiveValue(cond.key);
    return (cond.values ?? []).some((x) => String(x).toLowerCase() === String(v ?? '').toLowerCase());
  }

  function refreshVisibility() {
    for (const c of controls.values()) c.wrapper.hidden = !isVisible(c.option);
  }
  refreshVisibility();

  return {
    el,
    refreshVisibility,
    /** The options to send: visible, non-empty values; secrets omitted to keep them, "" to clear them. */
    getOptions() {
      const out = {};
      for (const c of controls.values()) {
        if (!isVisible(c.option)) continue;
        const v = c.read();
        if (v !== undefined) out[c.option.key] = v;
      }
      return out;
    },
    /** Client-side checks (required, numbers, bounds); marks the fields and returns whether all passed. */
    validate() {
      let ok = true;
      for (const c of controls.values()) {
        if (!isVisible(c.option)) {
          setFieldError(c.wrapper, null);
          continue;
        }
        const { option } = c;
        const v = c.read();
        let message = null;
        const isStoredSecret = option.type === 'secret' && c.wrapper.secret && secretSet.has(option.key.toLowerCase()) && !c.wrapper.secret.cleared();
        if (option.required && (v === undefined || v === '') && !isStoredSecret && (option.default === null || option.default === undefined)) {
          message = t('validate.required');
        } else if (v !== undefined && ['integer', 'decimal', 'port'].includes(option.type)) {
          const n = numberValue(c.wrapper.numberInput);
          if (Number.isNaN(n) || n === null) message = t('validate.number');
          else if (option.type !== 'decimal' && !Number.isInteger(n)) message = t('validate.integer');
          else if (option.type === 'port' && (n < 1 || n > 65535)) message = t('validate.range', { min: 1, max: 65535 });
          else if (option.min !== null && option.min !== undefined && n < option.min) message = t('validate.min', { min: option.min });
          else if (option.max !== null && option.max !== undefined && n > option.max) message = t('validate.max', { max: option.max });
        }
        setFieldError(c.wrapper, message);
        if (message) {
          ok = false;
          const details = c.wrapper.closest('details');
          if (details) details.open = true;
        }
      }
      return ok;
    },
    /** Fills the given options (discovery results) and leaves the others as they are. */
    setValues(next) {
      for (const [key, value] of Object.entries(next ?? {})) {
        const c = [...controls.values()].find((x) => x.option.key.toLowerCase() === key.toLowerCase());
        if (c) {
          c.write(value === null || value === undefined ? undefined : String(value));
          setFieldError(c.wrapper, null);
          if (c.option.advanced) {
            const details = c.wrapper.closest('details');
            if (details) details.open = true;
          }
        }
      }
      refreshVisibility();
    },
  };
}
