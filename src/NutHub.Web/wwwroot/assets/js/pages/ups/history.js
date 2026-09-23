// History tab of one UPS: a range switch and the charts. The server returns the configured history variables; known
// variables are grouped into meaningful charts, any other recorded series gets a chart of its own.
import { h, seg, replace, setText } from '../../dom.js';
import { t } from '../../i18n.js';
import { api, isAbort, errorText } from '../../api.js';
import { num, duration, varUnit } from '../../format.js';
import { lineChart } from '../../components/linechart.js';
import { segmented } from '../../components/segmented.js';

const RANGES = ['1h', '6h', '24h', '7d', '30d', '90d'];
const RANGE_KEY = 'nuthub.historyRange';
const MORE_KEY = 'nuthub.historyMore';
// Shown first; the other recorded series (temperature, power, frequency...) wait behind "Show more charts".
const PRIMARY = new Set(['charge', 'runtime', 'load', 'voltage']);

function chartGroups() {
  return [
    { id: 'charge', title: t('metric.charge'), series: [{ key: 'battery.charge', label: t('metric.charge') }], unit: '%', fixedRange: [0, 100] },
    {
      id: 'runtime',
      title: t('metric.runtime'),
      series: [{ key: 'battery.runtime', label: t('metric.runtime') }],
      unit: 'min',
      scale: 1 / 60,
      format: (v) => duration(v * 60),
    },
    { id: 'load', title: t('metric.load'), series: [{ key: 'ups.load', label: t('metric.load') }], unit: '%', fixedRange: [0, 100] },
    {
      id: 'voltage',
      title: t('chart.voltage'),
      series: [{ key: 'input.voltage', label: t('metric.input') }, { key: 'output.voltage', label: t('metric.output') }],
      unit: 'V',
    },
    { id: 'batteryVoltage', title: t('metric.batteryVoltage'), series: [{ key: 'battery.voltage', label: t('metric.batteryVoltage') }], unit: 'V' },
    { id: 'temperature', title: t('metric.temperature'), series: [{ key: 'ups.temperature', label: t('metric.temperature') }], unit: '°C' },
    { id: 'realpower', title: t('metric.realPower'), series: [{ key: 'ups.realpower', label: t('metric.realPower') }], unit: 'W' },
    { id: 'power', title: t('metric.apparentPower'), series: [{ key: 'ups.power', label: t('metric.apparentPower') }], unit: 'VA' },
    { id: 'frequency', title: t('metric.frequency'), series: [{ key: 'input.frequency', label: t('metric.frequency') }], unit: 'Hz' },
  ];
}

function storedRange() {
  try {
    const v = window.localStorage.getItem(RANGE_KEY);
    return RANGES.includes(v) ? v : '24h';
  } catch {
    return '24h';
  }
}

/** Builds the history tab; returns { el }. */
export function historySection(name, ctx) {
  let range = storedRange();
  const charts = new Map();
  const grid = h('div', { class: 'charts charts-main' });
  const status = h('p', { class: 'muted small', role: 'status' });
  const selector = segmented({
    label: t('chart.range'),
    options: RANGES.map((r) => ({ value: r, label: t(`range.${r}`) })),
    value: range,
    onChange: (value) => {
      range = value;
      try {
        window.localStorage.setItem(RANGE_KEY, value);
      } catch {
        // Only a convenience.
      }
      load();
    },
  });
  let showMore = false;
  try {
    showMore = window.localStorage.getItem(MORE_KEY) === '1';
  } catch {
    // Only a convenience.
  }
  const extraGrid = h('div', { class: 'charts', hidden: true });
  const moreButton = h('button', { type: 'button', class: 'btn btn-ghost btn-sm history-more', hidden: true, 'aria-expanded': 'false' });
  moreButton.addEventListener('click', () => {
    showMore = !showMore;
    try {
      window.localStorage.setItem(MORE_KEY, showMore ? '1' : '0');
    } catch {
      // Only a convenience.
    }
    if (lastHistory) render(lastHistory);
  });
  const el = h('div', { class: 'history-tab' }, h('div', { class: 'tab-toolbar' }, selector.el), status, grid, moreButton, extraGrid);
  let controller = null;
  let timer = null;
  let lastHistory = null;

  function scaled(points, factor) {
    if (!factor || factor === 1) return points;
    return points.map((p) => [p[0], p[1] * factor, Number.isFinite(p[2]) ? p[2] * factor : p[2], Number.isFinite(p[3]) ? p[3] * factor : p[3]]);
  }

  function render(history) {
    lastHistory = history;
    const series = history?.series ?? {};
    const groups = chartGroups();
    const known = new Set(groups.flatMap((g) => g.series.map((s) => s.key)));
    for (const key of Object.keys(series)) {
      if (!known.has(key)) {
        const u = varUnit(key);
        groups.push({ id: `var:${key}`, title: key, series: [{ key, label: key }], unit: u === 's' ? '' : u });
      }
    }
    const withData = groups.filter((g) => g.series.some((s) => (series[s.key] ?? []).length > 0));
    // A UPS without any of the main series shows what it has directly.
    const primaryIds = withData.some((g) => PRIMARY.has(g.id)) ? PRIMARY : new Set(withData.map((g) => g.id));
    const visible = [];
    const extra = [];
    for (const group of groups) {
      const hasData = group.series.some((s) => (series[s.key] ?? []).length > 0);
      if (!hasData) {
        charts.get(group.id)?.destroy();
        charts.delete(group.id);
        continue;
      }
      let chart = charts.get(group.id);
      if (!chart) {
        chart = lineChart({
          title: group.title,
          series: group.series,
          unit: group.unit,
          fixedRange: group.fixedRange,
          format: group.format ?? ((v) => (group.unit ? `${num(v, v >= 100 ? 0 : 1)} ${group.unit}` : num(v, 2))),
        });
        charts.set(group.id, chart);
      }
      const data = {};
      for (const s of group.series) data[s.key] = scaled(series[s.key] ?? [], group.scale);
      const primary = primaryIds.has(group.id);
      (primary ? visible : extra).push(chart.el);
      // Set after insertion so the chart measures its real width; hidden charts are drawn when they are shown.
      if (primary || showMore) {
        queueMicrotask(() => chart.setData(data, { from: history.from, to: history.to, stepSeconds: history.stepSeconds }));
      }
    }
    replace(grid, visible);
    replace(extraGrid, extra);
    extraGrid.hidden = !showMore || extra.length === 0;
    moreButton.hidden = extra.length === 0;
    moreButton.setAttribute('aria-expanded', String(showMore));
    setText(moreButton, showMore ? t('chart.fewer') : t('chart.more', { count: extra.length }));
    status.textContent = visible.length ? '' : t('chart.noHistory');
  }

  async function load() {
    controller?.abort();
    controller = new AbortController();
    const signal = controller.signal;
    if (ctx.signal.aborted) return;
    for (const chart of charts.values()) chart.setLoading(true);
    try {
      const history = await api.get(`api/ups/${seg(name)}/history?range=${range}`, { signal });
      render(history);
    } catch (err) {
      if (isAbort(err)) return;
      status.textContent = `${t('chart.loadFailed')}: ${errorText(err)}`;
    } finally {
      for (const chart of charts.values()) chart.setLoading(false);
    }
    clearTimeout(timer);
    timer = setTimeout(load, range === '1h' ? 60000 : 300000);
  }

  ctx.onCleanup(() => {
    clearTimeout(timer);
    controller?.abort();
    for (const chart of charts.values()) chart.destroy();
  });
  load();
  return { el };
}
