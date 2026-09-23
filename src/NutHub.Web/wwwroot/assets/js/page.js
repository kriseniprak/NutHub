// Common page building blocks: headers, sections, loading and error states.
import { h } from './dom.js';
import { icon } from './icons.js';
import { t } from './i18n.js';
import { errorText } from './api.js';

/** Page header with title, optional subtitle, back link and actions. */
export function pageHeader({ title, subtitle, actions, back, badge }) {
  return h('header', { class: 'page-head' },
    h('div', { class: 'page-title' },
      back ? h('a', { class: 'back-link', href: back.href }, icon('arrowLeft', { className: 'icon-sm' }), back.label) : null,
      h('div', { class: 'page-title-row' }, h('h1', { tabindex: '-1', text: title }), badge ?? null),
      subtitle ? h('p', { class: 'page-subtitle' }, subtitle) : null),
    actions ? h('div', { class: 'page-actions' }, actions) : null);
}

/** A card section with a heading. */
export function section({ title, description, actions, className, id }, ...children) {
  return h('section', { class: ['card', 'section', className], id, 'aria-label': title },
    title || actions ? h('div', { class: 'section-head' },
      h('div', {}, title ? h('h2', { class: 'section-title', text: title }) : null,
        description ? h('p', { class: 'section-desc' }, description) : null),
      actions ? h('div', { class: 'section-actions' }, actions) : null) : null,
    children);
}

/** A loading placeholder. */
export function loading(text = t('common.loading')) {
  return h('div', { class: 'loading', role: 'status' }, h('span', { class: 'spinner', 'aria-hidden': 'true' }), h('span', { text }));
}

/** An error state with a retry button. */
export function errorState(err, retry) {
  return h('div', { class: 'error-state', role: 'alert' },
    icon('warning'),
    h('p', { text: errorText(err) }),
    retry ? h('button', { type: 'button', class: 'btn', onClick: retry }, icon('refresh'), h('span', { text: t('common.retry') })) : null);
}

/** A callout (information / warning box). */
export function callout(kind, title, ...body) {
  const icons = { info: 'info', warning: 'warning', critical: 'critical', ok: 'checkCircle' };
  return h('div', { class: ['callout', `callout-${kind}`] },
    icon(icons[kind] ?? 'info'),
    h('div', { class: 'callout-body' }, title ? h('strong', { class: 'callout-title', text: title }) : null, body));
}

/** A read-only definition list [[label, value], ...]. */
export function definitionList(items, className) {
  return h('dl', { class: ['deflist', className] },
    items.filter(Boolean).map(([label, value]) => h('div', { class: 'deflist-row' }, h('dt', { text: label }), h('dd', {}, value ?? '–'))));
}
