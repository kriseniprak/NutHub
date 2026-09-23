// Clean rows for the lists of the settings pages (UPS devices, accounts, webhooks): a title that opens the item, one
// muted line, a few pills and a single "..." menu for the secondary actions instead of a row of buttons.
import { h } from '../dom.js';
import { t } from '../i18n.js';
import { menuButton } from './menu.js';

/** The "..." menu of a row; `items` as for menuButton (an array or a function). */
export function rowMenu(name, items) {
  return menuButton({ label: t('common.actionsFor', { name }), iconName: 'more', buttonClass: 'btn btn-icon btn-ghost', items });
}

/**
 * One row. `href` or `onOpen` make the title the primary action; `lead` goes before the text (a drag handle),
 * `side` after it (a status pill), `menu` is a rowMenu, `below` spans the whole row (a test result).
 */
export function itemRow({ title, href, onOpen, meta, pills, side, menu, lead, below, className, dataset }) {
  let titleEl;
  if (href) {
    titleEl = h('a', { class: 'item-title', href, text: title });
  } else if (onOpen) {
    titleEl = h('button', { type: 'button', class: 'item-title link-button', text: title });
    titleEl.addEventListener('click', onOpen);
  } else {
    titleEl = h('strong', { class: 'item-title', text: title });
  }
  return h('li', { class: ['item-row', className], dataset },
    lead ?? null,
    h('div', { class: 'item-main' },
      h('div', { class: 'item-title-row' }, titleEl, pills ?? null),
      meta ? h('p', { class: 'item-meta' }, meta) : null),
    side ? h('div', { class: 'item-side' }, side) : null,
    menu ? h('div', { class: 'item-menu' }, menu.el) : null,
    below ? h('div', { class: 'item-below' }, below) : null);
}

/** The list around the rows. */
export function itemList(label, rows = []) {
  return h('ul', { class: 'item-list', 'aria-label': label }, rows);
}
