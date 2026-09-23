// Events: a quiet list grouped by day, one search box, and the UPS / severity / category filters behind a "Filters"
// button (a badge counts the active ones). Paging with beforeId ("load more" and infinite scroll) and live
// prepending of new events that match the filters.
import { h, replace, setText, debounce } from '../dom.js';
import { icon } from '../icons.js';
import { t } from '../i18n.js';
import { api, isAbort, errorText } from '../api.js';
import { state, on } from '../store.js';
import { pageHeader } from '../page.js';
import { field, selectInput } from '../components/fields.js';
import { eventDays, severityRank } from '../components/eventlist.js';
import { eventText } from '../labels.js';

const PAGE_SIZE = 100;
const SEVERITIES = ['info', 'notice', 'warning', 'critical'];
const CATEGORIES = ['power', 'device', 'communication', 'command', 'shutdown', 'audit', 'system'];
// Informational entries (sign-ins, NUT client logins, trim/boost...) are hidden unless asked for: the log opens on
// what matters.
const DEFAULT_SEVERITY = 'notice';

export default {
  title: () => t('nav.events'),
  render(el, ctx) {
    const filters = {
      ups: ctx.query.get('ups') ?? '',
      minSeverity: ctx.query.get('minSeverity') ?? DEFAULT_SEVERITY,
      category: ctx.query.get('category') ?? '',
      search: ctx.query.get('search') ?? '',
    };

    const upsNames = [...new Set([...state.ups.map((u) => u.name), filters.ups].filter(Boolean))];
    const upsSelect = selectInput({
      options: [{ value: '', label: t('events.allUps') }, ...upsNames.map((n) => ({ value: n, label: n }))],
      value: filters.ups,
    });
    const sevSelect = selectInput({
      options: SEVERITIES.map((s) => ({ value: s, label: s === 'info' ? t('events.anySeverity') : t('events.atLeast', { severity: t(`severity.${s}`) }) })),
      value: filters.minSeverity,
    });
    const catSelect = selectInput({
      options: [{ value: '', label: t('events.allCategories') }, ...CATEGORIES.map((c) => ({ value: c, label: t(`category.${c}`) }))],
      value: filters.category,
    });
    const search = h('input', { class: 'input', type: 'search', value: filters.search, placeholder: t('events.searchPlaceholder'), 'aria-label': t('common.search') });
    const reset = h('button', { type: 'button', class: 'btn btn-ghost btn-sm' }, h('span', { text: t('events.clearFilters') }));

    const panel = h('div', { class: 'card filters-panel', id: 'event-filters', hidden: true },
      h('div', { class: 'filters-grid' },
        field({ label: t('events.filterUps'), control: upsSelect }),
        field({ label: t('events.filterSeverity'), control: sevSelect }),
        field({ label: t('events.filterCategory'), control: catSelect })),
      h('div', { class: 'filters-foot' }, reset));
    const filterCount = h('span', { class: 'count-badge', hidden: true });
    const filterButton = h('button', { type: 'button', class: 'btn', 'aria-expanded': 'false', 'aria-controls': 'event-filters' },
      icon('filter', { className: 'icon-sm' }), h('span', { text: t('events.filters') }), filterCount);
    filterButton.addEventListener('click', () => {
      panel.hidden = !panel.hidden;
      filterButton.setAttribute('aria-expanded', String(!panel.hidden));
      if (!panel.hidden) upsSelect.focus();
    });

    const days = eventDays();
    const status = h('div', { class: 'events-status', role: 'status' });
    const more = h('button', { type: 'button', class: 'btn', hidden: true }, h('span', { text: t('events.loadMore') }));
    const sentinel = h('div', { class: 'sentinel', 'aria-hidden': 'true' });
    const newBanner = h('button', { type: 'button', class: 'btn btn-primary btn-sm new-events', hidden: true });

    el.append(
      pageHeader({ title: t('nav.events'), subtitle: t('events.subtitle') }),
      h('div', { class: 'events-toolbar' }, h('div', { class: 'search-input grow' }, icon('search'), search), filterButton),
      panel,
      h('div', { class: 'card events-card' }, newBanner, days.el, status, h('div', { class: 'events-more' }, more), sentinel),
    );

    let items = [];
    let hasMore = false;
    let loadingPage = false;
    let controller = null;
    let generation = 0;

    function query(beforeId) {
      const params = new URLSearchParams();
      if (filters.ups) params.set('ups', filters.ups);
      if (filters.minSeverity) params.set('minSeverity', filters.minSeverity);
      if (filters.category) params.set('category', filters.category);
      if (filters.search) params.set('search', filters.search);
      if (beforeId) params.set('beforeId', String(beforeId));
      params.set('limit', String(PAGE_SIZE));
      return `api/events?${params}`;
    }

    function syncUrl() {
      const params = new URLSearchParams();
      for (const [k, v] of Object.entries(filters)) if (v && !(k === 'minSeverity' && v === DEFAULT_SEVERITY)) params.set(k, v);
      const qs = params.toString();
      window.history.replaceState(null, '', `#/events${qs ? `?${qs}` : ''}`);
      const active = [filters.ups, filters.minSeverity !== DEFAULT_SEVERITY, filters.category].filter(Boolean).length;
      filterCount.hidden = !active;
      setText(filterCount, String(active));
      filterButton.setAttribute('aria-label', active ? `${t('events.filters')} (${t('events.activeFilters', { count: active })})` : t('events.filters'));
      reset.disabled = !active;
    }

    function setStatus(text, kind) {
      replace(status, text ? h('p', { class: ['muted', kind === 'error' && 'danger-text'], text }) : null);
    }

    async function loadPage(fresh) {
      if (loadingPage && !fresh) return;
      if (fresh) {
        controller?.abort();
        generation += 1;
        items = [];
        days.clear();
        hasMore = false;
        newBanner.hidden = true;
      }
      const gen = generation;
      controller = new AbortController();
      const signal = controller.signal;
      const onAbort = () => controller.abort();
      ctx.signal.addEventListener('abort', onAbort, { once: true });
      loadingPage = true;
      more.hidden = true;
      setStatus(t('common.loading'));
      try {
        const before = items.length ? items[items.length - 1].id : null;
        const page = await api.get(query(before), { signal });
        if (gen !== generation) return;
        const added = (page?.items ?? []).filter((ev) => !items.some((x) => x.id === ev.id));
        items.push(...added);
        days.append(added);
        hasMore = !!page?.hasMore;
        setStatus(items.length ? (hasMore ? '' : t('events.end')) : t('events.noMatch'));
        more.hidden = !hasMore;
      } catch (err) {
        if (isAbort(err)) return;
        setStatus(errorText(err), 'error');
        more.hidden = !items.length;
      } finally {
        ctx.signal.removeEventListener('abort', onAbort);
        if (gen === generation) loadingPage = false;
      }
    }

    function matches(ev) {
      if (filters.ups && (ev.ups ?? '').toLowerCase() !== filters.ups.toLowerCase()) return false;
      if (filters.minSeverity && severityRank(ev.severity) < severityRank(filters.minSeverity)) return false;
      if (filters.category && ev.category !== filters.category) return false;
      if (filters.search) {
        const needle = filters.search.toLowerCase();
        const hay = `${ev.message ?? ''} ${eventText(ev).text} ${ev.ups ?? ''} ${ev.type}`.toLowerCase();
        if (!hay.includes(needle)) return false;
      }
      return true;
    }

    const applyFilters = () => {
      filters.ups = upsSelect.value;
      filters.minSeverity = sevSelect.value;
      filters.category = catSelect.value;
      filters.search = search.value.trim();
      syncUrl();
      loadPage(true);
    };
    const debounced = debounce(applyFilters, 350);
    ctx.onCleanup(() => debounced.cancel());
    for (const select of [upsSelect, sevSelect, catSelect]) select.addEventListener('change', applyFilters);
    search.addEventListener('input', debounced);
    search.addEventListener('keydown', (event) => {
      if (event.key === 'Enter') {
        debounced.cancel();
        applyFilters();
      }
    });
    reset.addEventListener('click', () => {
      upsSelect.value = '';
      sevSelect.value = DEFAULT_SEVERITY;
      catSelect.value = '';
      applyFilters();
    });
    more.addEventListener('click', () => loadPage(false));

    // Infinite scroll: load the next page when the end of the list comes into view.
    const observer = new IntersectionObserver((entries) => {
      if (entries.some((e) => e.isIntersecting) && hasMore && !loadingPage) loadPage(false);
    }, { rootMargin: '400px' });
    observer.observe(sentinel);
    ctx.onCleanup(() => observer.disconnect());

    // Live events: prepend when at the top, otherwise offer to show them (so the list never jumps under the reader).
    let pending = [];
    function flushPending() {
      // pending is newest first: prepend from the oldest so the newest ends on top.
      for (const ev of pending.slice().reverse()) {
        items.unshift(ev);
        days.prepend(ev);
      }
      pending = [];
      newBanner.hidden = true;
      setStatus(items.length ? (hasMore ? '' : t('events.end')) : t('events.noMatch'));
    }
    newBanner.addEventListener('click', () => {
      flushPending();
      days.el.scrollIntoView({ behavior: 'smooth', block: 'start' });
    });
    ctx.onCleanup(on('event', (ev) => {
      if (!matches(ev) || items.some((x) => x.id === ev.id) || pending.some((x) => x.id === ev.id)) return;
      pending.unshift(ev);
      if (window.scrollY < 200) {
        flushPending();
      } else {
        replace(newBanner, icon('arrowUp', { className: 'icon-sm' }), h('span', { text: t('events.newCount', { count: pending.length }) }));
        newBanner.hidden = false;
      }
    }));

    // Filters that came with the address (a link from a UPS page) stay in sight, so the shorter list makes sense.
    if (filters.ups || filters.minSeverity !== DEFAULT_SEVERITY || filters.category) {
      panel.hidden = false;
      filterButton.setAttribute('aria-expanded', 'true');
    }
    syncUrl();
    loadPage(true);
  },
};
