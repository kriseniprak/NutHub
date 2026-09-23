// Dropdown menu button following the WAI-ARIA menu button pattern: arrow keys move, Escape closes and returns
// focus, Tab and outside clicks close.
import { h, uid } from '../dom.js';
import { icon } from '../icons.js';

let openMenu = null;

document.addEventListener('pointerdown', (event) => {
  // The open popup hangs from the body, not from the wrap: both have to be checked.
  if (openMenu && !openMenu.wrap.contains(event.target) && !openMenu.popup.contains(event.target)) openMenu.close(false);
});

/**
 * Creates a menu button. `items` is an array or a function returning one (evaluated on each open):
 * { label, icon, onSelect, danger, disabled, checked (radio items), description, heading, separator }.
 */
export function menuButton({ label, text, iconName, items, className, align = 'end', buttonClass = 'btn' }) {
  const menuId = uid('menu');
  const button = h('button', {
    type: 'button',
    class: [buttonClass, className],
    'aria-haspopup': 'menu',
    'aria-expanded': 'false',
    'aria-controls': menuId,
    'aria-label': text ? null : label,
    title: text ? null : label,
  }, iconName ? icon(iconName) : null, text ? h('span', { class: 'btn-label', text }) : null,
  text ? icon('chevronDown', { className: 'icon-sm' }) : null);
  const popup = h('div', { id: menuId, class: ['menu', `menu-${align}`], role: 'menu', hidden: true, 'aria-label': label });
  const wrap = h('div', { class: 'menu-wrap' }, button, popup);

  function focusables() {
    return [...popup.querySelectorAll('[role^="menuitem"]:not([aria-disabled="true"])')];
  }

  function build() {
    popup.replaceChildren();
    const list = typeof items === 'function' ? items() : items;
    for (const item of list) {
      if (!item) continue;
      if (item.separator) {
        popup.appendChild(h('div', { class: 'menu-sep', role: 'separator' }));
        continue;
      }
      if (item.heading) {
        popup.appendChild(h('div', { class: 'menu-heading', role: 'presentation', text: item.heading }));
        continue;
      }
      if (item.content) {
        popup.appendChild(h('div', { class: 'menu-content', role: 'presentation' }, item.content));
        continue;
      }
      const radio = typeof item.checked === 'boolean';
      const el = h('button', {
        type: 'button',
        class: ['menu-item', item.danger && 'danger'],
        role: radio ? 'menuitemradio' : 'menuitem',
        'aria-checked': radio ? String(item.checked) : null,
        'aria-disabled': item.disabled ? 'true' : null,
        tabindex: '-1',
      },
      radio ? h('span', { class: 'menu-check' }, item.checked ? icon('check') : null) : item.icon ? icon(item.icon) : null,
      h('span', { class: 'menu-item-text' },
        h('span', { class: 'menu-item-label', text: item.label }),
        item.description ? h('span', { class: 'menu-item-desc', text: item.description }) : null));
      el.addEventListener('click', () => {
        if (item.disabled) return;
        close(true);
        item.onSelect?.();
      });
      popup.appendChild(el);
    }
  }

  // Places the open popup next to its button, in viewport coordinates: below when there is room, above
  // otherwise, and never past an edge.
  function place() {
    const margin = 8;
    const anchor = button.getBoundingClientRect();
    popup.style.maxHeight = '';
    const size = popup.getBoundingClientRect();
    const below = window.innerHeight - anchor.bottom - margin - 6;
    const above = anchor.top - margin - 6;
    const upwards = size.height > below && above > below;
    const height = Math.min(size.height, Math.max(upwards ? above : below, 120));
    popup.style.maxHeight = `${Math.round(height)}px`;
    const left = align === 'end' ? anchor.right - size.width : anchor.left;
    popup.style.left = `${Math.round(Math.min(Math.max(margin, left), window.innerWidth - size.width - margin))}px`;
    popup.style.top = `${Math.round(upwards ? Math.max(margin, anchor.top - 6 - height) : anchor.bottom + 6)}px`;
  }

  const reposition = () => close(false);

  function open() {
    if (openMenu && openMenu !== api) openMenu.close(false);
    build();
    // Out of the card: a list or a panel that scrolls would otherwise cut the popup off. Inside a modal dialog it
    // stays in the dialog, which the browser draws above the page.
    (button.closest('dialog') ?? document.body).appendChild(popup);
    popup.classList.add('menu-float');
    popup.hidden = false;
    button.setAttribute('aria-expanded', 'true');
    openMenu = api;
    place();
    // preventScroll: focusing an item must not scroll the list behind it, which would move the row under the cursor.
    focusables()[0]?.focus({ preventScroll: true });
    window.addEventListener('scroll', reposition, true);
    window.addEventListener('resize', reposition);
  }

  function close(restoreFocus) {
    if (popup.hidden) return;
    window.removeEventListener('scroll', reposition, true);
    window.removeEventListener('resize', reposition);
    popup.hidden = true;
    popup.classList.remove('menu-float');
    popup.style.removeProperty('left');
    popup.style.removeProperty('top');
    popup.style.removeProperty('max-height');
    wrap.appendChild(popup);
    button.setAttribute('aria-expanded', 'false');
    if (openMenu === api) openMenu = null;
    if (restoreFocus) button.focus({ preventScroll: true });
  }

  button.addEventListener('click', () => (popup.hidden ? open() : close(false)));
  button.addEventListener('keydown', (event) => {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      open();
      if (event.key === 'ArrowUp') focusables().at(-1)?.focus();
    }
  });
  popup.addEventListener('keydown', (event) => {
    const list = focusables();
    const index = list.indexOf(document.activeElement);
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      list[(index + 1) % list.length]?.focus();
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      list[(index - 1 + list.length) % list.length]?.focus();
    } else if (event.key === 'Home') {
      event.preventDefault();
      list[0]?.focus();
    } else if (event.key === 'End') {
      event.preventDefault();
      list.at(-1)?.focus();
    } else if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      close(true);
    } else if (event.key === 'Tab') {
      close(false);
    }
  });

  const api = { el: wrap, button, open, close, wrap, popup };
  return api;
}
