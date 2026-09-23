// Server log viewer: level filter, search, optional auto-refresh (paused while the tab is hidden).
import { h, replace, debounce } from '../../dom.js';
import { icon } from '../../icons.js';
import { t } from '../../i18n.js';
import { api, isAbort, errorText } from '../../api.js';
import { pageHeader } from '../../page.js';
import { selectInput } from '../../components/fields.js';
import { dateTime, timeOfDay } from '../../format.js';

const LEVELS = ['trace', 'debug', 'information', 'warning', 'error', 'critical'];
const LEVEL_KEY = 'nuthub.logLevel';

function storedLevel() {
  try {
    const v = window.localStorage.getItem(LEVEL_KEY);
    return LEVELS.includes(v) ? v : 'information';
  } catch {
    return 'information';
  }
}

export default {
  title: () => t('nav.logs'),
  render(el, ctx) {
    const level = selectInput({ options: LEVELS.map((l) => ({ value: l, label: t('logs.atLeast', { level: t(`logLevel.${l}`) }) })), value: storedLevel(), 'aria-label': t('logs.level') });
    const limit = selectInput({ options: [200, 500, 1000, 2000].map((n) => ({ value: String(n), label: t('logs.lines', { count: n }) })), value: '500', 'aria-label': t('logs.limit') });
    const search = h('input', { class: 'input', type: 'search', placeholder: t('logs.search'), 'aria-label': t('common.search') });
    const autoId = `auto-${Math.random().toString(36).slice(2)}`;
    const auto = h('input', { type: 'checkbox', class: 'switch', id: autoId, role: 'switch' });
    const refresh = h('button', { type: 'button', class: 'btn' }, icon('refresh'), h('span', { text: t('common.refresh') }));
    const status = h('p', { class: 'muted small', role: 'status' });
    const list = h('div', { class: 'log-list', role: 'log', 'aria-live': 'off' });

    el.append(
      pageHeader({ title: t('nav.logs'), subtitle: t('logs.subtitle') }),
      h('div', { class: 'card filters' }, h('div', { class: 'filters-row' },
        h('div', { class: 'search-input grow' }, icon('search'), search), level, limit,
        h('label', { class: 'switch-row small', for: autoId }, auto, h('span', { text: t('logs.auto') })),
        refresh)),
      status,
      h('div', { class: 'card log-card' }, list));

    let controller = null;
    async function load() {
      controller?.abort();
      controller = new AbortController();
      const params = new URLSearchParams({ minLevel: level.value, limit: limit.value });
      if (search.value.trim()) params.set('search', search.value.trim());
      refresh.disabled = true;
      try {
        const entries = await api.get(`api/admin/logs?${params}`, { signal: controller.signal });
        render(entries ?? []);
        status.textContent = t('logs.count', { count: (entries ?? []).length, time: timeOfDay(new Date(), true) });
      } catch (err) {
        if (!isAbort(err)) status.textContent = errorText(err);
      } finally {
        refresh.disabled = false;
      }
    }

    function render(entries) {
      replace(list, entries.length ? entries.map((e) => {
        const lvl = e.level ?? 'information';
        return h('div', { class: ['log-entry', `log-${lvl}`] },
          h('span', { class: 'log-time', title: dateTime(e.timestamp), text: timeOfDay(e.timestamp, true) }),
          h('span', { class: 'log-level', text: t(`logLevel.${lvl}`) }),
          h('div', { class: 'log-body' },
            h('span', { class: 'log-category', text: e.category ?? '' }),
            h('p', { class: 'log-message', text: e.message ?? '' }),
            e.exception ? h('details', { class: 'log-exception' }, h('summary', { text: t('logs.exception') }), h('pre', { text: e.exception })) : null));
      }) : h('p', { class: 'muted log-empty', text: t('logs.none') }));
    }

    const debounced = debounce(load, 400);
    search.addEventListener('input', debounced);
    level.addEventListener('change', () => {
      try {
        window.localStorage.setItem(LEVEL_KEY, level.value);
      } catch {
        // Only a convenience.
      }
      load();
    });
    limit.addEventListener('change', load);
    refresh.addEventListener('click', load);
    const timer = setInterval(() => {
      if (auto.checked && document.visibilityState === 'visible' && !list.querySelector('details[open]')) load();
    }, 5000);
    ctx.onCleanup(() => {
      clearInterval(timer);
      debounced.cancel();
      controller?.abort();
    });
    load();
  },
};
