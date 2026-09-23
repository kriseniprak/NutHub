// Tabs following the WAI-ARIA tabs pattern (arrow keys, Home and End move between tabs; only the selected tab is in
// the Tab order). Panels are created on first selection, so a tab that is never opened costs nothing (history charts
// also need to be visible to measure their width).
import { h, uid } from '../dom.js';

/**
 * Creates tabs from [{ id, label, render() -> Node }]; returns { el, list, select(id), setVisible(id, bool),
 * selected() }. `onChange(id)` runs after a selection made by the user.
 */
export function tabs({ items, selected, label, onChange }) {
  const base = uid('tabs');
  const list = h('div', { class: 'tabs', role: 'tablist', 'aria-label': label });
  const panels = h('div', { class: 'tab-panels' });
  const entries = new Map();
  let current = null;

  for (const item of items) {
    const tabId = `${base}-${item.id}-tab`;
    const panelId = `${base}-${item.id}-panel`;
    const tab = h('button', {
      type: 'button',
      class: 'tab',
      role: 'tab',
      id: tabId,
      'aria-controls': panelId,
      'aria-selected': 'false',
      tabindex: '-1',
      text: item.label,
    });
    const panel = h('div', { class: 'tab-panel', role: 'tabpanel', id: panelId, 'aria-labelledby': tabId, hidden: true });
    tab.addEventListener('click', () => {
      if (select(item.id)) onChange?.(item.id);
    });
    entries.set(item.id, { item, tab, panel, built: false });
    list.appendChild(tab);
    panels.appendChild(panel);
  }

  function visibleTabs() {
    return [...entries.values()].filter((e) => !e.tab.hidden);
  }

  list.addEventListener('keydown', (event) => {
    const tabsNow = visibleTabs();
    const index = tabsNow.findIndex((e) => e.tab === document.activeElement);
    if (index < 0) return;
    let next = null;
    if (event.key === 'ArrowRight') next = tabsNow[(index + 1) % tabsNow.length];
    else if (event.key === 'ArrowLeft') next = tabsNow[(index - 1 + tabsNow.length) % tabsNow.length];
    else if (event.key === 'Home') next = tabsNow[0];
    else if (event.key === 'End') next = tabsNow.at(-1);
    if (!next) return;
    event.preventDefault();
    next.tab.focus();
    if (select(next.item.id)) onChange?.(next.item.id);
  });

  /** Selects a tab; returns whether the selection changed. */
  function select(id) {
    const entry = entries.get(id);
    if (!entry || entry.tab.hidden || current === id) return false;
    for (const [key, e] of entries) {
      const on = key === id;
      e.tab.setAttribute('aria-selected', String(on));
      e.tab.tabIndex = on ? 0 : -1;
      e.panel.hidden = !on;
    }
    current = id;
    if (!entry.built) {
      entry.built = true;
      entry.panel.append(...[entry.item.render()].flat().filter(Boolean));
    }
    return true;
  }

  /** Shows or hides a tab (a hidden selected tab falls back to the first one). */
  function setVisible(id, visible) {
    const entry = entries.get(id);
    if (!entry || entry.tab.hidden === !visible) return;
    entry.tab.hidden = !visible;
    if (!visible && current === id) {
      current = null;
      select(visibleTabs()[0]?.item.id);
      onChange?.(current);
    }
  }

  select(entries.has(selected) && !entries.get(selected).tab.hidden ? selected : items[0]?.id);
  return { el: h('div', { class: 'tabs-wrap' }, list, panels), list, select, setVisible, selected: () => current };
}
