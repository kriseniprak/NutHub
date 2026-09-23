// Status and severity pills: a coloured dot always comes with a text, colour is never the only carrier.
import { h, setText } from '../dom.js';
import { upsStatusText, upsStatusShort, severityText } from '../labels.js';

/** A status pill for a severity with an optional custom text. */
export function severityBadge(severity, text, { compact = false } = {}) {
  const sev = severity || 'offline';
  return h('span', { class: ['badge', `sev-${sev}`, compact && 'badge-compact'] },
    h('span', { class: 'badge-dot', 'aria-hidden': 'true' }),
    h('span', { class: 'badge-text', text: text ?? severityText(sev) }));
}

/**
 * The status pill of a UPS ("On line · Charging"), coloured by its severity. `short` keeps only the main word (the
 * dashboard cards); the full text stays in the tooltip.
 */
export function statusBadge(summary, { short = false } = {}) {
  const el = severityBadge(summary?.severity, short ? upsStatusShort(summary) : upsStatusText(summary));
  el.classList.add('status-badge');
  if (short) el.dataset.short = '1';
  el.title = upsStatusText(summary);
  return el;
}

/** Updates a status pill in place (no flicker, no re-creation). */
export function updateStatusBadge(el, summary) {
  const sev = summary?.severity || 'offline';
  const current = [...el.classList].find((c) => c.startsWith('sev-'));
  if (current !== `sev-${sev}`) {
    if (current) el.classList.remove(current);
    el.classList.add(`sev-${sev}`);
  }
  const text = upsStatusText(summary);
  setText(el.querySelector('.badge-text'), el.dataset.short ? upsStatusShort(summary) : text);
  if (el.title !== text) el.title = text;
}

/** A neutral pill ("Admin", "TLS"). */
export function pill(text, kind = 'neutral') {
  return h('span', { class: ['pill', `pill-${kind}`], text });
}
