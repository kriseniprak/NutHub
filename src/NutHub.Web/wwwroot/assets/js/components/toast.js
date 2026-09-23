// Toast notifications in a live region: polite for information, assertive for errors.
import { h } from '../dom.js';
import { icon } from '../icons.js';
import { t } from '../i18n.js';

let region = null;
const MAX_TOASTS = 4;

function ensureRegion() {
  if (region && document.body.contains(region)) return region;
  region = h('div', { class: 'toasts', role: 'region', 'aria-label': t('toast.region') });
  document.body.appendChild(region);
  return region;
}

const ICONS = { success: 'checkCircle', error: 'critical', warning: 'warning', info: 'info' };

/** Shows a toast. kind: success | error | warning | info. Errors stay longer. Returns a dismiss function. */
export function toast(message, { kind = 'info', timeout } = {}) {
  const container = ensureRegion();
  const ms = timeout ?? (kind === 'error' ? 9000 : 4500);
  const text = h('div', { class: 'toast-text', role: kind === 'error' ? 'alert' : 'status', text: message });
  const el = h('div', { class: ['toast', `toast-${kind}`] },
    icon(ICONS[kind] ?? 'info'),
    text,
    h('button', { type: 'button', class: 'btn btn-icon btn-ghost btn-sm', 'aria-label': t('common.dismiss'), onClick: () => dismiss() },
      icon('close')));
  let timer = null;
  function dismiss() {
    clearTimeout(timer);
    el.classList.add('leaving');
    setTimeout(() => el.remove(), 180);
  }
  container.appendChild(el);
  while (container.children.length > MAX_TOASTS) container.firstElementChild.remove();
  if (ms > 0) {
    timer = setTimeout(dismiss, ms);
    el.addEventListener('mouseenter', () => clearTimeout(timer));
    el.addEventListener('mouseleave', () => {
      timer = setTimeout(dismiss, 2500);
    });
  }
  return dismiss;
}
