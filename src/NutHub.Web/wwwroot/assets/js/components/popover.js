// Disclosure popover: a button that shows a small panel of information under it (the status summary of the app
// bar). Unlike a menu it holds ordinary content, so focus stays on the button and Tab walks into the panel, which
// follows the button in the document. Escape and clicks outside close it.
import { h, uid } from '../dom.js';

let openPopover = null;

document.addEventListener('pointerdown', (event) => {
  if (openPopover && !openPopover.el.contains(event.target)) openPopover.close(false);
});

/**
 * Creates a popover around `button`. `render()` returns the panel content and runs on every open and refresh().
 * Returns { el, button, panel, open, close(restoreFocus), isOpen(), refresh() }.
 */
export function popover({ button, render, label, align = 'end', className }) {
  const panelId = uid('pop');
  const panel = h('div', { id: panelId, class: ['popover', `popover-${align}`, className], role: 'region', 'aria-label': label, hidden: true });
  button.setAttribute('aria-expanded', 'false');
  button.setAttribute('aria-controls', panelId);
  const el = h('div', { class: 'popover-wrap' }, button, panel);

  function refresh() {
    if (!panel.hidden) panel.replaceChildren(...[render()].flat().filter(Boolean));
  }

  function open() {
    if (openPopover && openPopover !== api) openPopover.close(false);
    panel.hidden = false;
    refresh();
    button.setAttribute('aria-expanded', 'true');
    openPopover = api;
  }

  function close(restoreFocus) {
    if (panel.hidden) return;
    panel.hidden = true;
    button.setAttribute('aria-expanded', 'false');
    if (openPopover === api) openPopover = null;
    if (restoreFocus) button.focus();
  }

  button.addEventListener('click', () => (panel.hidden ? open() : close(false)));
  el.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && !panel.hidden) {
      event.preventDefault();
      close(true);
    }
  });
  // Leaving the panel with the keyboard closes it, like a menu.
  el.addEventListener('focusout', (event) => {
    if (!panel.hidden && event.relatedTarget && !el.contains(event.relatedTarget)) close(false);
  });
  panel.addEventListener('click', (event) => {
    if (event.target.closest('a[href]')) close(false);
  });

  const api = { el, button, panel, open, close, isOpen: () => !panel.hidden, refresh };
  return api;
}
