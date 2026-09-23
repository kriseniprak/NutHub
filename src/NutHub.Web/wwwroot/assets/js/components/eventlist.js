// Event lists shared by the events page and the Events tab of a UPS: one line per event (time, severity dot, UPS,
// message) grouped by day. The rest (type, category, who, the exact time and, in other languages, the server's
// original sentence) is one click away, so the list stays quiet.
import { h, uid } from '../dom.js';
import { icon } from '../icons.js';
import { link } from '../router.js';
import { dateTime, timeOfDay, longDate } from '../format.js';
import { eventText, eventTypeText, severityText, categoryText } from '../labels.js';
import { t } from '../i18n.js';

const SEVERITY_RANK = { info: 0, notice: 1, warning: 2, critical: 3 };

/** Numeric rank of an event severity, for minimum-severity filters. */
export function severityRank(severity) {
  return SEVERITY_RANK[severity] ?? 0;
}

function startOfDay(date) {
  return new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime();
}

function dayKey(value) {
  return String(startOfDay(new Date(value)));
}

/** "Today", "Yesterday" or the full date of an event day. */
function dayTitle(value) {
  const days = Math.round((startOfDay(new Date()) - startOfDay(new Date(value))) / 86400000);
  if (days === 0) return t('events.today');
  if (days === 1) return t('events.yesterday');
  return longDate(value);
}

function fact(label, value) {
  return h('div', { class: 'event-fact' }, h('dt', { text: label }), h('dd', { text: value }));
}

/** One event as a list item. */
function eventRow(ev, { showUps = true, isNew = false } = {}) {
  const { text, detail } = eventText(ev);
  const sev = ev.severity || 'info';
  const when = new Date(ev.timestamp);
  const moreId = uid('ev');
  const toggle = h('button', {
    type: 'button',
    class: 'btn btn-icon btn-ghost btn-sm event-toggle',
    'aria-expanded': 'false',
    'aria-controls': moreId,
    'aria-label': t('events.details'),
    title: t('events.details'),
  }, icon('chevronDown', { className: 'icon-sm' }));
  const more = h('div', { id: moreId, class: 'event-more', hidden: true },
    h('dl', { class: 'event-facts' },
      fact(t('events.when'), dateTime(ev.timestamp)),
      fact(t('events.type'), eventTypeText(ev.type) || ev.type),
      fact(t('events.category'), categoryText(ev.category)),
      fact(t('events.severity'), severityText(sev)),
      ev.actor && ev.actor !== 'system' ? fact(t('events.actor'), ev.actor) : null),
    detail ? h('p', { class: 'event-detail', text: detail }) : null);
  const row = h('li', { class: ['event-row', `sev-${sev}`, isNew && 'is-new'], dataset: { id: ev.id } },
    h('div', { class: 'event-line' },
      h('time', {
        class: 'event-time',
        datetime: Number.isNaN(when.getTime()) ? null : when.toISOString(),
        title: dateTime(ev.timestamp),
        text: timeOfDay(ev.timestamp),
      }),
      h('span', { class: 'event-dot', role: 'img', 'aria-label': severityText(sev), title: severityText(sev) }),
      showUps
        ? (ev.ups ? h('a', { class: 'event-ups', href: link('/ups', ev.ups), text: ev.ups }) : h('span', { class: 'event-ups is-server', text: t('events.server') }))
        : null,
      h('span', { class: 'event-text', text, title: text }),
      toggle),
    more);
  toggle.addEventListener('click', () => {
    const open = more.hidden;
    more.hidden = !open;
    row.classList.toggle('is-open', open);
    toggle.setAttribute('aria-expanded', String(open));
  });
  return row;
}

/**
 * A list of events grouped by day, newest first. Returns { el, append(events) for older pages, prepend(event) for
 * live ones, clear(), trim(max) }.
 */
export function eventDays({ showUps = true } = {}) {
  const el = h('div', { class: 'event-days' });
  const groups = new Map();

  function group(ev, atTop) {
    const key = dayKey(ev.timestamp);
    let g = groups.get(key);
    if (!g) {
      const list = h('ul', { class: 'event-list' });
      g = { section: h('section', { class: 'event-day' }, h('h2', { class: 'event-day-title', text: dayTitle(ev.timestamp) }), list), list };
      groups.set(key, g);
      if (atTop) el.prepend(g.section);
      else el.appendChild(g.section);
    }
    return g;
  }

  return {
    el,
    append(events) {
      for (const ev of events) group(ev, false).list.appendChild(eventRow(ev, { showUps }));
    },
    prepend(ev) {
      group(ev, true).list.prepend(eventRow(ev, { showUps, isNew: true }));
    },
    clear() {
      groups.clear();
      el.replaceChildren();
    },
    /** Keeps only the newest `max` rows (a short list that grows with live events). */
    trim(max) {
      const rows = el.querySelectorAll('.event-row');
      for (let i = rows.length - 1; i >= max; i -= 1) rows[i].remove();
      for (const [key, g] of groups) {
        if (!g.list.children.length) {
          g.section.remove();
          groups.delete(key);
        }
      }
    },
  };
}
