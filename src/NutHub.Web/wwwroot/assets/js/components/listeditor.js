// Editor for a list of strings (allowed networks, e-mail recipients, history variables).
import { h, uid } from '../dom.js';
import { icon } from '../icons.js';
import { t } from '../i18n.js';
import { setFieldError } from './fields.js';

/**
 * Returns { el, getValues(), validate(), setValues(values) }. validate(value) returns an error message or null.
 * `name` is the server path prefix: row i gets data-field "<name>[i]" so API errors land on it.
 */
export function listEditor({ values = [], placeholder = '', validate, addLabel = t('common.add'), label, name,
  inputType = 'text', suggestions, onChange, mono = true }) {
  const list = h('div', { class: 'list-editor-rows' });
  const listId = suggestions ? uid('ls') : null;
  const datalist = suggestions ? h('datalist', { id: listId }, suggestions.map((v) => h('option', { value: v }))) : null;
  const addButton = h('button', { type: 'button', class: 'btn btn-sm' }, icon('plus'), h('span', { text: addLabel }));
  const el = h('div', { class: 'list-editor', dataset: { field: name } }, list, datalist, addButton);

  function renumber() {
    [...list.children].forEach((row, i) => {
      if (name) row.dataset.field = `${name}[${i}]`;
    });
  }

  function addRow(value = '') {
    const input = h('input', { class: ['input', mono && 'input-mono'], type: inputType, value, placeholder, 'aria-label': label,
      spellcheck: 'false', autocomplete: 'off', list: listId });
    const remove = h('button', { type: 'button', class: 'btn btn-icon btn-ghost', 'aria-label': t('common.remove'), title: t('common.remove') },
      icon('trash'));
    const row = h('div', { class: 'field list-editor-row' }, h('div', { class: 'list-editor-line' }, input, remove));
    remove.addEventListener('click', () => {
      row.remove();
      renumber();
      onChange?.();
    });
    input.addEventListener('input', () => {
      setFieldError(row, null);
      onChange?.();
    });
    list.appendChild(row);
    renumber();
    return input;
  }

  function setValues(next) {
    list.replaceChildren();
    for (const v of next ?? []) addRow(v);
  }

  addButton.addEventListener('click', () => {
    addRow().focus();
    onChange?.();
  });
  setValues(values);

  return {
    el,
    setValues,
    getValues() {
      return [...list.querySelectorAll('input')].map((i) => i.value.trim()).filter(Boolean);
    },
    validate() {
      let ok = true;
      for (const row of list.children) {
        const value = row.querySelector('input').value.trim();
        const message = value && validate ? validate(value) : null;
        setFieldError(row, message);
        if (message) ok = false;
      }
      return ok;
    },
  };
}

/** Light client-side check of an IPv4/IPv6 address or CIDR network; the server validates for real. */
export function validateCidr(value) {
  const [addr, prefix, extra] = value.split('/');
  if (extra !== undefined) return t('validate.cidr');
  const v4 = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(addr);
  if (v4) {
    if (v4.slice(1).some((p) => Number(p) > 255)) return t('validate.cidr');
    if (prefix !== undefined && !(/^\d{1,2}$/.test(prefix) && Number(prefix) <= 32)) return t('validate.cidr');
    return null;
  }
  if (addr.includes(':') && /^[0-9a-fA-F:.]+$/.test(addr)) {
    if (prefix !== undefined && !(/^\d{1,3}$/.test(prefix) && Number(prefix) <= 128)) return t('validate.cidr');
    return null;
  }
  return t('validate.cidr');
}

/** Light e-mail address check. */
export function validateEmail(value) {
  return /^[^\s@<>]+@[^\s@<>]+$/.test(value) ? null : t('validate.email');
}
