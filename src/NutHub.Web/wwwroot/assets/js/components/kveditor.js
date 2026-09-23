// Key/value list editor (variable overrides, webhook headers). With `secretValues`, entries loaded with a null
// value are stored secrets: they show "set — leave empty to keep" and are sent back as null to keep them.
import { h, uid } from '../dom.js';
import { icon } from '../icons.js';
import { t } from '../i18n.js';
import { setFieldError } from './fields.js';

/**
 * entries: [[key, value|null], ...]. validateKey(key) returns an error message or null. Returns
 * { el, getEntries() -> [[key, value|null]], validate() -> bool, setEntries(entries) }.
 */
export function kvEditor({ entries = [], keyLabel, valueLabel, keyPlaceholder = '', valuePlaceholder = '',
  validateKey, secretValues = false, addLabel = t('common.add'), onChange, name, keySuggestions }) {
  const list = h('div', { class: 'kv-list' });
  const listId = keySuggestions ? uid('kvs') : null;
  const datalist = keySuggestions ? h('datalist', { id: listId }, keySuggestions.map((k) => h('option', { value: k }))) : null;
  const addButton = h('button', { type: 'button', class: 'btn btn-sm' }, icon('plus'), h('span', { text: addLabel }));
  const el = h('div', { class: 'kv-editor', dataset: { field: name } }, list, datalist, addButton);

  function addRow(key = '', value = '', stored = false) {
    const keyInput = h('input', { class: 'input input-mono', value: key, placeholder: keyPlaceholder, 'aria-label': keyLabel,
      spellcheck: 'false', autocomplete: 'off', list: listId });
    const valueInput = h('input', {
      class: 'input',
      type: secretValues ? 'password' : 'text',
      value: stored ? '' : value ?? '',
      placeholder: stored ? t('secret.keep') : valuePlaceholder,
      'aria-label': valueLabel,
      spellcheck: 'false',
      autocomplete: secretValues ? 'new-password' : 'off',
    });
    const remove = h('button', { type: 'button', class: 'btn btn-icon btn-ghost', 'aria-label': t('common.remove'), title: t('common.remove') },
      icon('trash'));
    const keyField = h('div', { class: 'field kv-key' }, keyInput);
    const row = h('div', { class: 'kv-row' }, keyField, h('div', { class: 'field kv-value' }, valueInput), remove);
    row.stored = stored;
    remove.addEventListener('click', () => {
      row.remove();
      onChange?.();
    });
    keyInput.addEventListener('input', () => {
      setFieldError(keyField, null);
      onChange?.();
    });
    valueInput.addEventListener('input', () => onChange?.());
    list.appendChild(row);
    return row;
  }

  function setEntries(next) {
    list.replaceChildren();
    for (const [k, v] of next) addRow(k, v, secretValues && v === null);
  }

  addButton.addEventListener('click', () => {
    const row = addRow();
    row.querySelector('input').focus();
    onChange?.();
  });

  setEntries(entries);

  return {
    el,
    setEntries,
    getEntries() {
      const out = [];
      for (const row of list.children) {
        const [keyInput, valueInput] = row.querySelectorAll('input');
        const key = keyInput.value.trim();
        if (!key) continue;
        const value = valueInput.value;
        out.push([key, row.stored && value === '' ? null : value]);
      }
      return out;
    },
    validate() {
      let ok = true;
      const seen = new Set();
      for (const row of list.children) {
        const keyField = row.querySelector('.kv-key');
        const key = row.querySelector('input').value.trim();
        let message = null;
        if (!key) {
          if (row.querySelectorAll('input')[1].value !== '') message = t('kv.keyRequired');
        } else if (seen.has(key.toLowerCase())) {
          message = t('kv.duplicate');
        } else if (validateKey) {
          message = validateKey(key);
        }
        seen.add(key.toLowerCase());
        setFieldError(keyField, message);
        if (message) ok = false;
      }
      return ok;
    },
  };
}
