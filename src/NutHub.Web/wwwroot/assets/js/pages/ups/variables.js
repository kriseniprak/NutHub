// Variables table of a UPS with search, descriptions and inline editing of writable variables (enumerations,
// range-checked numbers, length-limited strings) for operators.
import { h, seg } from '../../dom.js';
import { icon } from '../../icons.js';
import { t } from '../../i18n.js';
import { api, commandResultText, errorText } from '../../api.js';
import { hasRole } from '../../auth.js';
import { varValue } from '../../format.js';
import { dataTable, textFilter } from '../../components/table.js';
import { pill } from '../../components/badge.js';
import { toast } from '../../components/toast.js';
import { section } from '../../page.js';

function inRanges(value, ranges) {
  if (!ranges?.length) return true;
  return ranges.some((r) => {
    const min = Number(r.min);
    const max = Number(r.max);
    return (!Number.isFinite(min) || value >= min) && (!Number.isFinite(max) || value <= max);
  });
}

function rangesText(ranges) {
  return ranges.map((r) => `${r.min}–${r.max}`).join(', ');
}

/** Variables section; returns { el, update(detail) }. onChanged() asks the page to reload the detail. */
export function variablesSection(upsName, onChanged) {
  const operator = hasRole('operator');
  const search = h('input', { class: 'input', type: 'search', placeholder: t('common.search'), 'aria-label': t('ups.searchVariables') });
  const writableOnly = h('input', { type: 'checkbox', class: 'check', id: `wr-${Math.random().toString(36).slice(2)}` });
  let variables = [];
  let table = null;

  function editor(v, cell) {
    table.lock(v.name, true);
    let control;
    if (v.enumValues?.length) {
      control = h('select', { class: 'input select', 'aria-label': t('ups.newValue', { name: v.name }) },
        v.enumValues.map((e) => h('option', { value: e, text: e })));
      control.value = v.value ?? '';
    } else if (v.type === 'number') {
      const first = v.ranges?.[0];
      control = h('input', {
        class: 'input input-num', type: 'number', step: 'any', value: v.value ?? '',
        min: first && v.ranges.length === 1 ? first.min : null,
        max: first && v.ranges.length === 1 ? first.max : null,
        'aria-label': t('ups.newValue', { name: v.name }),
      });
    } else {
      control = h('input', {
        class: 'input', type: 'text', value: v.value ?? '', maxlength: v.maxLength > 0 ? v.maxLength : null,
        'aria-label': t('ups.newValue', { name: v.name }), spellcheck: 'false',
      });
    }
    const hint = v.ranges?.length ? t('ups.allowedRange', { ranges: rangesText(v.ranges) })
      : v.maxLength > 0 && !v.enumValues?.length ? t('ups.maxLength', { count: v.maxLength }) : '';
    const error = h('p', { class: 'field-error', hidden: true, role: 'alert' });
    const save = h('button', { type: 'submit', class: 'btn btn-primary btn-sm' }, icon('check'), h('span', { text: t('common.save') }));
    const cancel = h('button', { type: 'button', class: 'btn btn-sm' }, h('span', { text: t('common.cancel') }));
    const form = h('form', { class: 'inline-edit', novalidate: true },
      h('div', { class: 'inline-edit-row' }, control, save, cancel),
      hint ? h('p', { class: 'field-help', text: hint }) : null, error);

    function close() {
      table.invalidate(v.name);
      table.lock(v.name, false);
    }
    function fail(message) {
      error.replaceChildren(icon('warning', { className: 'icon-sm' }), h('span', { text: message }));
      error.hidden = false;
      control.setAttribute('aria-invalid', 'true');
      control.focus();
    }
    cancel.addEventListener('click', close);
    form.addEventListener('keydown', (event) => {
      if (event.key === 'Escape') {
        event.preventDefault();
        close();
      }
    });
    form.addEventListener('submit', async (event) => {
      event.preventDefault();
      const value = String(control.value).trim();
      if (v.type === 'number' && !v.enumValues?.length) {
        const n = Number(value.replace(',', '.'));
        if (value === '' || !Number.isFinite(n)) return fail(t('validate.number'));
        if (!inRanges(n, v.ranges)) return fail(t('ups.outOfRange', { ranges: rangesText(v.ranges) }));
      } else if (v.maxLength > 0 && [...value].length > v.maxLength) {
        return fail(t('ups.tooLong', { count: v.maxLength }));
      }
      save.disabled = true;
      save.classList.add('is-busy');
      try {
        const result = await api.put(`api/ups/${seg(upsName)}/variables/${seg(v.name)}`, { value: v.type === 'number' ? value.replace(',', '.') : value });
        if (result?.ok) {
          toast(t('ups.variableSaved', { name: v.name }), { kind: 'success' });
          close();
          onChanged?.();
        } else {
          fail(commandResultText(result));
        }
      } catch (err) {
        fail(errorText(err));
      } finally {
        save.disabled = false;
        save.classList.remove('is-busy');
      }
      return undefined;
    });
    cell.replaceChildren(form);
    control.focus();
    control.select?.();
  }

  table = dataTable({
    caption: t('ups.variables'),
    rowKey: (v) => v.name,
    sort: { key: 'name', dir: 'asc' },
    filter: textFilter('name', 'description', 'value'),
    emptyText: t('ups.noVariables'),
    columns: [
      {
        key: 'name',
        label: t('common.name'),
        render: (v) => h('div', { class: 'var-name' }, h('code', { text: v.name }),
          v.writable ? pill(t('ups.writable'), 'primary') : null,
          v.overridden ? pill(t('ups.overridden'), 'warning') : null),
      },
      {
        key: 'value',
        label: t('common.value'),
        className: 'var-value',
        render: (v) => {
          const content = h('div', { class: 'var-value-cell' }, h('span', { class: 'cell-strong', text: varValue(v.name, v.value) }));
          if (operator && v.writable) {
            const edit = h('button', { type: 'button', class: 'btn btn-icon btn-ghost btn-sm', 'aria-label': t('ups.editVariable', { name: v.name }), title: t('common.edit') }, icon('edit'));
            edit.addEventListener('click', () => editor(v, content.parentElement));
            content.appendChild(edit);
          }
          return content;
        },
      },
      { key: 'description', label: t('common.description'), render: (v) => v.description || h('span', { class: 'faint', text: '–' }) },
    ],
  });

  function applyFilter() {
    table.setRows(writableOnly.checked ? variables.filter((v) => v.writable) : variables);
    table.setFilter(search.value);
  }
  search.addEventListener('input', () => table.setFilter(search.value));
  writableOnly.addEventListener('change', applyFilter);

  const tools = h('div', { class: 'row' },
    h('label', { class: 'check-row', for: writableOnly.id }, writableOnly, h('span', { text: t('ups.writableOnly') })),
    h('div', { class: 'search-input' }, icon('search'), search));
  // The tab already names the table: the card only carries the search tools.
  const el = section({ actions: tools, className: 'variables-section' }, table.el);
  return {
    el,
    update(detail) {
      variables = detail.variables ?? [];
      applyFilter();
    },
  };
}
