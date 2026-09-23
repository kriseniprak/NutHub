// Segmented control (a row of toggle buttons with one selected), used for chart ranges and filters.
import { h } from '../dom.js';

/** Creates a segmented control; returns { el, set(value) }. */
export function segmented({ label, options, value, onChange, size }) {
  const buttons = new Map();
  const el = h('div', { class: ['segmented', size && `segmented-${size}`], role: 'group', 'aria-label': label });
  let current = value;
  for (const option of options) {
    const b = h('button', {
      type: 'button',
      class: 'segment',
      'aria-pressed': String(option.value === value),
      text: option.label,
      title: option.title ?? null,
    });
    b.addEventListener('click', () => {
      if (current === option.value) return;
      set(option.value);
      onChange?.(option.value);
    });
    buttons.set(option.value, b);
    el.appendChild(b);
  }
  function set(v) {
    current = v;
    for (const [key, b] of buttons) b.setAttribute('aria-pressed', String(key === v));
  }
  return { el, set };
}
