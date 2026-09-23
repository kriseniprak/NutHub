// Helpers shared by the administration pages.
import { h } from '../../dom.js';
import { icon } from '../../icons.js';
import { t } from '../../i18n.js';
import { api, errorText } from '../../api.js';
import { clearErrors, applyFieldErrors } from '../../components/fields.js';
import { toast } from '../../components/toast.js';
import { refresh } from '../../router.js';

let driversCache = null;

/** The driver catalogue (cached for the session; drivers do not change while the server runs). */
export async function loadDrivers(signal) {
  if (!driversCache) {
    driversCache = api.get('api/admin/drivers', { signal }).catch((err) => {
      driversCache = null;
      throw err;
    });
  }
  return driversCache;
}

/**
 * Marks the page dirty on any edit inside `root` so leaving asks for confirmation; the "is-dirty" class shows the
 * save bar of the form (see saveBar) only while there is something to save.
 */
export function trackDirty(root, ctx) {
  const set = (value) => {
    ctx.setDirty(value);
    root.classList.toggle('is-dirty', value);
  };
  root.addEventListener('input', () => set(true));
  root.addEventListener('change', () => set(true));
  return {
    mark: () => set(true),
    clean: () => set(false),
  };
}

/**
 * The sticky save bar at the end of a settings form: "Unsaved changes", a Discard button (reloads the saved values)
 * and the given actions. It shows only while the form is dirty, unless `always` (a form that creates something).
 */
export function saveBar(actions, { always = false, note } = {}) {
  const discard = always ? null : h('button', { type: 'button', class: 'btn btn-ghost', text: t('settings.discard') });
  discard?.addEventListener('click', () => refresh());
  return h('div', { class: ['save-bar', always && 'is-always'] },
    h('div', { class: 'save-bar-text' }, always ? null : h('strong', { text: t('settings.unsaved') }), note ?? null),
    h('div', { class: 'save-bar-actions' }, discard, actions));
}

/** A collapsed "Advanced" block holding whole sections of a settings form (validation errors open it). */
export function advancedGroup(title, ...sections) {
  return h('details', { class: 'advanced-group' },
    h('summary', {}, icon('chevronRight', { className: 'icon-sm chev' }), h('span', { text: title })),
    h('div', { class: 'advanced-group-body' }, sections));
}

/**
 * Saves with standard error handling: clears old errors, puts validation errors on their fields and the rest
 * in the error box, toasts success. Returns the answer, or undefined on failure.
 */
export async function saveWithErrors({ form, errorBox, button, request, success }) {
  clearErrors(form);
  errorBox?.show(null);
  if (button) {
    button.disabled = true;
    button.classList.add('is-busy');
  }
  try {
    const result = await request();
    if (success) toast(success, { kind: 'success' });
    return result ?? true;
  } catch (err) {
    if (err?.name === 'AbortError') return undefined;
    if (err.code === 'validation' && err.fields) {
      const rest = applyFieldErrors(form, err.fields);
      // When a field got the error it also got the focus: do not scroll away from it.
      const matched = rest.length < Object.keys(err.fields).length;
      errorBox?.show(rest.length ? rest : t('form.fixErrors'), { scroll: !matched });
    } else {
      errorBox?.show(errorText(err));
    }
    return undefined;
  } finally {
    if (button) {
      button.disabled = false;
      button.classList.remove('is-busy');
    }
  }
}

/** A primary "Save" submit button. */
export function saveButton(label = t('common.save')) {
  return h('button', { type: 'submit', class: 'btn btn-primary' }, icon('check'), h('span', { text: label }));
}

/** Parses an integer field value: null when empty, NaN when invalid. */
export function intValue(input) {
  const raw = String(input.value ?? '').trim();
  if (raw === '') return null;
  const n = Number(raw);
  return Number.isInteger(n) ? n : NaN;
}

/** Validates an integer input within bounds; returns an error message or null. */
export function checkInt(input, { min, max, required = true } = {}) {
  const v = intValue(input);
  if (v === null) return required ? t('validate.required') : null;
  if (Number.isNaN(v)) return t('validate.integer');
  if (min !== undefined && v < min) return t('validate.min', { min });
  if (max !== undefined && v > max) return t('validate.max', { max });
  return null;
}

/** Web settings, NUT settings... from GET api/admin/settings. */
export function loadSettings(signal) {
  return api.get('api/admin/settings', { signal });
}
