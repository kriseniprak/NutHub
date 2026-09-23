// Sortable, filterable table with keyed rows: an update only re-renders rows whose data changed, and rows
// marked as locked (an inline editor is open) are left alone, so live refreshes never steal focus or input.
import { h } from '../dom.js';
import { icon } from '../icons.js';
import { t, locale } from '../i18n.js';

/**
 * columns: [{ key, label, render(row) -> Node|string, sortValue(row), sortable (default true), className,
 * align: "end", hideLabel }]. Options: rowKey(row), emptyText, sort { key, dir: "asc"|"desc" },
 * filter(row, text) -> bool, caption, stack (cards on phones, default true), rowClass(row).
 * Returns { el, setRows(rows), setFilter(text), lock(key, bool), rowElement(key) }.
 */
export function dataTable({ columns, rowKey = (r, i) => i, emptyText = t('table.empty'), sort, filter, caption,
  stack = true, rowClass, compact = false }) {
  let rows = [];
  let filterText = '';
  let sortState = sort ? { ...sort } : null;
  const rendered = new Map();
  const locked = new Set();
  const collator = new Intl.Collator(locale(), { numeric: true, sensitivity: 'base' });

  const thead = h('thead');
  const tbody = h('tbody');
  const emptyRow = h('tr', { class: 'table-empty' }, h('td', { colspan: columns.length, text: emptyText }));
  const table = h('table', { class: ['table', stack && 'table-stack', compact && 'table-compact'] },
    caption ? h('caption', { class: 'sr-only', text: caption }) : null, thead, tbody);
  const el = h('div', { class: 'table-wrap' }, table);

  function headerRow() {
    const tr = h('tr');
    for (const col of columns) {
      const sortable = col.sortable !== false && (col.sortValue || col.key);
      const active = sortState && sortState.key === col.key;
      const th = h('th', {
        scope: 'col',
        class: [col.className, col.align === 'end' && 'align-end'],
        'aria-sort': active ? (sortState.dir === 'asc' ? 'ascending' : 'descending') : sortable ? 'none' : null,
      });
      const labelEl = col.hideLabel ? h('span', { class: 'sr-only', text: col.label }) : col.label;
      if (sortable) {
        const btn = h('button', { type: 'button', class: 'th-sort' }, labelEl,
          icon(active ? (sortState.dir === 'asc' ? 'arrowUp' : 'arrowDown') : 'chevronDown', { className: ['icon-sm', active ? '' : 'sort-idle'].join(' ') }));
        btn.addEventListener('click', () => {
          sortState = active && sortState.dir === 'asc' ? { key: col.key, dir: 'desc' } : { key: col.key, dir: 'asc' };
          thead.replaceChildren(headerRow());
          render();
        });
        th.appendChild(btn);
      } else {
        th.append(labelEl);
      }
      tr.appendChild(th);
    }
    return tr;
  }

  function cellValue(col, row) {
    if (col.sortValue) return col.sortValue(row);
    return row?.[col.key];
  }

  function buildRow(row, index) {
    const tr = h('tr', { class: rowClass ? rowClass(row) : null });
    for (const col of columns) {
      const content = col.render ? col.render(row, index) : row?.[col.key];
      tr.appendChild(h('td', {
        class: [col.className, col.align === 'end' && 'align-end'],
        dataset: { label: col.hideLabel ? '' : col.label },
      }, content ?? ''));
    }
    return tr;
  }

  function signature(row) {
    try {
      return JSON.stringify(row);
    } catch {
      return String(Math.random());
    }
  }

  function render() {
    let list = rows.map((row, index) => ({ row, index, key: String(rowKey(row, index)) }));
    if (filterText && filter) list = list.filter((item) => filter(item.row, filterText));
    if (sortState) {
      const col = columns.find((c) => c.key === sortState.key);
      if (col) {
        const dir = sortState.dir === 'desc' ? -1 : 1;
        list.sort((a, b) => {
          const va = cellValue(col, a.row);
          const vb = cellValue(col, b.row);
          if (va === vb) return a.index - b.index;
          if (va === null || va === undefined || va === '') return 1;
          if (vb === null || vb === undefined || vb === '') return -1;
          if (typeof va === 'number' && typeof vb === 'number') return (va - vb) * dir;
          return collator.compare(String(va), String(vb)) * dir;
        });
      }
    }

    const seen = new Set();
    let position = 0;
    for (const item of list) {
      seen.add(item.key);
      const sig = signature(item.row);
      let entry = rendered.get(item.key);
      if (!entry || (entry.sig !== sig && !locked.has(item.key))) {
        const tr = buildRow(item.row, item.index);
        tr.dataset.key = item.key;
        if (entry) entry.tr.replaceWith(tr);
        entry = { tr, sig };
        rendered.set(item.key, entry);
      }
      const at = tbody.children[position];
      if (at !== entry.tr) tbody.insertBefore(entry.tr, at ?? null);
      position += 1;
    }
    for (const [key, entry] of rendered) {
      if (!seen.has(key)) {
        entry.tr.remove();
        if (!rows.some((r, i) => String(rowKey(r, i)) === key)) rendered.delete(key);
      }
    }
    if (!list.length) {
      emptyRow.firstChild.textContent = filterText && rows.length ? t('table.noMatch') : emptyText;
      tbody.appendChild(emptyRow);
    } else if (emptyRow.parentNode) {
      emptyRow.remove();
    }
  }

  thead.appendChild(headerRow());
  render();

  return {
    el,
    setRows(next) {
      rows = Array.isArray(next) ? next : [];
      render();
    },
    setFilter(text) {
      filterText = String(text ?? '').trim().toLowerCase();
      render();
    },
    /** Keeps a row as it is (an inline editor is open in it) until unlocked. */
    lock(key, on) {
      if (on) locked.add(String(key));
      else {
        locked.delete(String(key));
        render();
      }
    },
    /** Forces a row to be rebuilt at the next render. */
    invalidate(key) {
      const entry = rendered.get(String(key));
      if (entry) entry.sig = null;
    },
    rowElement(key) {
      return rendered.get(String(key))?.tr ?? null;
    },
    rows: () => rows,
  };
}

/** Default text filter: any of the given fields contains the text. */
export function textFilter(...keys) {
  return (row, text) => keys.some((key) => {
    const v = typeof key === 'function' ? key(row) : row?.[key];
    return v !== null && v !== undefined && String(v).toLowerCase().includes(text);
  });
}
