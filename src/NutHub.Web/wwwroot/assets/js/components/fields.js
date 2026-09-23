// Form building blocks with consistent labels, help texts and error display. A field wrapper carries
// data-field="<server path>" so validation errors from the API (fields["options.port"]) land on the right input.
import { h, uid } from '../dom.js';
import { icon } from '../icons.js';
import { t } from '../i18n.js';

/** Help longer than this keeps its first sentence under the field and the rest behind a "?" toggle. */
const INLINE_HELP_MAX = 72;

/** Splits a help text into [short line, longer explanation] (either may be null). */
function splitHelp(help) {
  if (typeof help !== 'string' || help.length <= INLINE_HELP_MAX) return [help || null, null];
  const m = /^(.+?[.!?])\s+(.+)$/s.exec(help);
  if (m && m[1].length <= INLINE_HELP_MAX) return [m[1], m[2]];
  return [null, help];
}

/**
 * The help of a control: a muted line, and a "?" button that shows the longer explanation. Returns
 * { line, more, toggle, ids } where ids go into the control's aria-describedby (hidden text still describes it).
 */
function helpParts(id, help) {
  const [short, long] = splitHelp(help);
  const line = short ? h('p', { id: `${id}-help`, class: 'field-help' }, short) : null;
  if (!long) return { line, more: null, toggle: null, ids: short ? [`${id}-help`] : [] };
  const more = h('p', { id: `${id}-more`, class: 'field-help field-help-more', hidden: true, text: long });
  const toggle = h('button', {
    type: 'button',
    class: 'help-toggle',
    'aria-expanded': 'false',
    'aria-controls': `${id}-more`,
    'aria-label': t('common.moreInfo'),
    title: t('common.moreInfo'),
    text: '?',
  });
  toggle.addEventListener('click', () => {
    more.hidden = !more.hidden;
    toggle.setAttribute('aria-expanded', String(!more.hidden));
  });
  return { line, more, toggle, ids: [short ? `${id}-help` : null, `${id}-more`].filter(Boolean) };
}

/**
 * Wraps a control with its label, help and error line. `name` is the server field path used to match
 * validation errors. Returns the wrapper element.
 */
export function field({ label, control, help, name, required, className, hint }) {
  // The control may be a composite (secret input, port picker): label and describe its first form element.
  const target = control.matches('input, select, textarea') ? control
    : control.querySelector('input, select, textarea') ?? control;
  const id = target.id || uid('f');
  target.id = id;
  const parts = helpParts(id, help);
  if (parts.ids.length) target.setAttribute('aria-describedby', parts.ids.join(' '));
  if (required) target.setAttribute('aria-required', 'true');
  const wrapper = h('div', { class: ['field', className], dataset: { field: name } },
    label ? h('div', { class: 'field-label-row' },
      h('label', { class: 'field-label', for: id },
        h('span', { text: label }),
        required ? h('span', { class: 'req', 'aria-hidden': 'true', text: ' *' }) : null,
        hint ? h('span', { class: 'field-hint', text: hint }) : null),
      parts.toggle) : null,
    control,
    parts.line,
    parts.more);
  return wrapper;
}

/** A switch (checkbox) with its label on the right and optional help. */
export function switchField({ label, checked, name, help, onChange, disabled }) {
  const id = uid('sw');
  const input = h('input', { id, type: 'checkbox', class: 'switch', checked: !!checked, disabled, role: 'switch' });
  if (onChange) input.addEventListener('change', () => onChange(input.checked));
  const parts = helpParts(id, help);
  if (parts.ids.length) input.setAttribute('aria-describedby', parts.ids.join(' '));
  const wrapper = h('div', { class: 'field field-switch', dataset: { field: name } },
    h('div', { class: 'switch-row' }, input, h('label', { for: id, class: 'switch-label', text: label }), parts.toggle),
    parts.line,
    parts.more);
  wrapper.input = input;
  return wrapper;
}

/** A checkbox with a label (lists of choices). */
export function checkbox({ label, checked, value, name, description, disabled }) {
  const id = uid('cb');
  const input = h('input', { id, type: 'checkbox', class: 'check', checked: !!checked, value, name, disabled });
  return h('label', { class: ['check-row', disabled && 'is-disabled'], for: id }, input,
    h('span', { class: 'check-text' }, h('span', { text: label }),
      description ? h('span', { class: 'check-desc', text: description }) : null));
}

/** A radio button with a label and optional description. */
export function radio({ label, name, value, checked, description, disabled }) {
  const id = uid('rb');
  const input = h('input', { id, type: 'radio', class: 'check', name, value, checked: !!checked, disabled });
  return h('label', { class: ['check-row', disabled && 'is-disabled'], for: id }, input,
    h('span', { class: 'check-text' }, h('span', { text: label }),
      description ? h('span', { class: 'check-desc', text: description }) : null));
}

/** Text input. */
export function textInput(props = {}) {
  return h('input', { type: 'text', class: ['input', props.className], spellcheck: 'false', ...props, className: null });
}

/** Number input returning numbers through numberValue(). */
export function numberInput(props = {}) {
  return h('input', { type: 'number', class: ['input', 'input-num', props.className], inputmode: 'decimal', ...props, className: null });
}

/** Password / secret input. */
export function passwordInput(props = {}) {
  return h('input', { type: 'password', class: ['input', props.className], autocomplete: 'new-password', spellcheck: 'false', ...props, className: null });
}

/** Multi-line text. */
export function textArea(props = {}) {
  return h('textarea', { class: ['input', 'textarea', props.className], spellcheck: 'false', ...props, className: null });
}

/** Select from [{ value, label, disabled }]. */
export function selectInput({ options, value, className, ...rest }) {
  const el = h('select', { class: ['input', 'select', className], ...rest });
  for (const option of options) {
    el.appendChild(h('option', { value: option.value, disabled: option.disabled, text: option.label }));
  }
  if (value !== undefined && value !== null) el.value = String(value);
  return el;
}

/**
 * Reads a number input: null when empty, NaN when not a number. Accepts a comma as decimal separator since
 * some browsers leave localized input to the page.
 */
export function numberValue(input) {
  const raw = String(input.value ?? '').trim().replace(',', '.');
  if (raw === '') {
    // Firefox leaves value empty for unparseable text in type=number; validity tells the difference.
    return input.validity && input.validity.badInput ? NaN : null;
  }
  const n = Number(raw);
  return Number.isFinite(n) ? n : NaN;
}

/** Shows (or clears with null) an error on a field wrapper. */
export function setFieldError(wrapper, message) {
  if (!wrapper) return;
  let el = wrapper.querySelector(':scope > .field-error');
  const control = wrapper.querySelector('input, select, textarea');
  if (!message) {
    el?.remove();
    wrapper.classList.remove('has-error');
    if (control) {
      control.removeAttribute('aria-invalid');
      const described = (control.getAttribute('aria-describedby') || '').split(' ').filter((x) => x && !x.endsWith('-err'));
      if (described.length) control.setAttribute('aria-describedby', described.join(' '));
      else control.removeAttribute('aria-describedby');
    }
    return;
  }
  const errId = `${control?.id || uid('f')}-err`;
  if (!el) {
    el = h('p', { class: 'field-error', id: errId });
    wrapper.appendChild(el);
  }
  el.replaceChildren(icon('warning', { className: 'icon-sm' }), h('span', { text: message }));
  wrapper.classList.add('has-error');
  if (control) {
    control.setAttribute('aria-invalid', 'true');
    const described = (control.getAttribute('aria-describedby') || '').split(' ').filter((x) => x && x !== errId);
    described.push(errId);
    control.setAttribute('aria-describedby', described.join(' '));
  }
}

/** Removes every field error and the form error box inside a root element. */
export function clearErrors(root) {
  for (const wrapper of root.querySelectorAll('.field.has-error')) setFieldError(wrapper, null);
  for (const box of root.querySelectorAll('.form-error[data-owned]')) {
    box.hidden = true;
    box.replaceChildren();
  }
}

function normalizePath(path) {
  return String(path)
    .replace(/^(ups|nutUsers|webUsers|webhooks|commands)\[\d+\]\./i, '')
    .replace(/^(server|nut|web|history|hostProtection|notifications|settings)\./i, '')
    .toLowerCase();
}

/**
 * Puts API validation errors on the matching fields (by data-field, ignoring case and list/section prefixes)
 * and returns the entries that matched no field, to show in the form error box. Focuses the first bad field.
 */
export function applyFieldErrors(root, fields) {
  const unmatched = [];
  let first = null;
  const wrappers = [...root.querySelectorAll('[data-field]')];
  for (const [path, message] of Object.entries(fields ?? {})) {
    const wanted = [String(path).toLowerCase(), normalizePath(path)];
    const wrapper = wrappers.find((w) => wanted.includes(String(w.dataset.field).toLowerCase()) && !w.closest('[hidden]'));
    if (wrapper) {
      setFieldError(wrapper, message);
      const details = wrapper.closest('details');
      if (details) details.open = true;
      first ??= wrapper;
    } else {
      unmatched.push([path, message]);
    }
  }
  first?.querySelector('input, select, textarea')?.focus();
  return unmatched;
}

/** An error summary box for a form; call show(err | text | [[path, message]]) or show(null). */
export function formErrorBox() {
  const el = h('div', { class: 'form-error', role: 'alert', hidden: true, dataset: { owned: '1' } });
  function show(content, { scroll = true } = {}) {
    if (!content || (Array.isArray(content) && !content.length)) {
      el.hidden = true;
      el.replaceChildren();
      return;
    }
    let body;
    if (Array.isArray(content)) {
      body = h('div', {}, h('p', { text: t('form.fixErrors') }),
        h('ul', {}, content.map(([path, message]) => h('li', {}, h('code', { text: path }), ` ${message}`))));
    } else {
      body = h('span', { text: String(content) });
    }
    el.replaceChildren(icon('warning'), body);
    el.hidden = false;
    if (!scroll) return;
    // Take the user to the first field in error when there is one; otherwise to the summary.
    const firstBad = el.closest('form')?.querySelector('.has-error input, .has-error select, .has-error textarea');
    if (firstBad) firstBad.focus();
    else el.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
  }
  return { el, show };
}

/** A collapsible section (<details>) for advanced settings. */
export function advancedSection(title, children, open = false) {
  return h('details', { class: 'advanced', open: open ? true : null },
    h('summary', {}, icon('chevronRight', { className: 'icon-sm chev' }), h('span', { text: title })),
    h('div', { class: 'advanced-body' }, children));
}

/** A labelled group of fields (fieldset with legend). */
export function fieldset(legend, children, { className, description } = {}) {
  return h('fieldset', { class: ['fieldset', className] },
    h('legend', { text: legend }),
    description ? h('p', { class: 'fieldset-desc', text: description }) : null,
    children);
}

/** A secret input: shows "set — leave empty to keep" when a value is stored, with a clear toggle. */
export function secretControl({ isSet, placeholder, autocomplete = 'new-password' }) {
  const input = passwordInput({ placeholder: isSet ? t('secret.keep') : placeholder ?? '', autocomplete });
  let cleared = false;
  const clearBtn = isSet
    ? h('button', { type: 'button', class: 'btn btn-sm btn-ghost', text: t('secret.clear') })
    : null;
  const status = isSet ? h('span', { class: 'secret-status', text: t('secret.isSet') }) : null;
  clearBtn?.addEventListener('click', () => {
    cleared = !cleared;
    clearBtn.textContent = cleared ? t('secret.undoClear') : t('secret.clear');
    status.textContent = cleared ? t('secret.willClear') : t('secret.isSet');
    status.classList.toggle('is-cleared', cleared);
    input.disabled = cleared;
    input.placeholder = cleared ? '' : t('secret.keep');
    if (cleared) input.value = '';
  });
  const el = h('div', { class: 'secret-control' }, input, status || clearBtn ? h('div', { class: 'secret-meta' }, status, clearBtn) : null);
  return {
    el,
    input,
    /** undefined = keep the stored value, "" = clear, other = replace. */
    value() {
      if (cleared) return '';
      return input.value === '' ? undefined : input.value;
    },
    cleared: () => cleared,
  };
}
