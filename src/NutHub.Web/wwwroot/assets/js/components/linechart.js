// SVG line chart for history series: average line, min/max band, time axis, gaps where data is missing, and a
// hover / keyboard cursor with a tooltip. Redraws at the real width through a ResizeObserver (no scaling blur).
import { h, s } from '../dom.js';
import { num, dateTime, timeOfDay, dayMonth } from '../format.js';
import { t } from '../i18n.js';

const MARGIN = { l: 48, r: 12, t: 12, b: 26 };
const MINUTE = 60000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;
const TIME_STEPS = [MINUTE, 2 * MINUTE, 5 * MINUTE, 10 * MINUTE, 15 * MINUTE, 30 * MINUTE, HOUR, 2 * HOUR,
  3 * HOUR, 6 * HOUR, 12 * HOUR, DAY, 2 * DAY, 7 * DAY, 14 * DAY];

function niceTicks(lo, hi, count) {
  if (!(hi > lo)) {
    const pad = Math.abs(lo) > 1 ? Math.abs(lo) * 0.05 : 1;
    lo -= pad;
    hi += pad;
  }
  const raw = (hi - lo) / Math.max(1, count);
  const mag = 10 ** Math.floor(Math.log10(raw));
  const norm = raw / mag;
  const step = (norm < 1.5 ? 1 : norm < 3 ? 2 : norm < 7 ? 5 : 10) * mag;
  const start = Math.floor(lo / step) * step;
  const end = Math.ceil(hi / step) * step;
  const ticks = [];
  for (let v = start; v <= end + step / 2; v += step) ticks.push(Number(v.toPrecision(12)));
  return { ticks, step };
}

function timeTicks(x0, x1, maxTicks) {
  const span = x1 - x0;
  const step = TIME_STEPS.find((st) => span / st <= maxTicks) ?? TIME_STEPS.at(-1);
  const offset = new Date(x0).getTimezoneOffset() * MINUTE;
  const ticks = [];
  let v = Math.ceil((x0 - offset) / step) * step + offset;
  for (; v <= x1; v += step) ticks.push(v);
  return { ticks, step };
}

function nearest(points, ms) {
  let lo = 0;
  let hi = points.length - 1;
  if (hi < 0) return -1;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    if (points[mid][0] < ms) lo = mid + 1;
    else hi = mid;
  }
  if (lo > 0 && Math.abs(points[lo - 1][0] - ms) < Math.abs(points[lo][0] - ms)) return lo - 1;
  return lo;
}

/**
 * Creates a chart. series: [{ key, label }]. Options: unit, format(v) for values, fixedRange [lo, hi] (percent
 * charts), height. Returns { el, setData(seriesMap, { from, to, stepSeconds }), destroy() }.
 */
export function lineChart({ title, series, unit = '', format, fixedRange, height = 200 }) {
  const fmt = format ?? ((v) => (unit ? `${num(v, 1)} ${unit}` : num(v, 2)));
  const legend = h('div', { class: 'chart-legend' },
    series.length > 1 ? series.map((sr, i) => h('span', { class: 'legend-item' }, h('span', { class: `legend-swatch series-${i}` }), sr.label)) : null);
  const svg = s('svg', { class: 'chart-svg', 'aria-hidden': 'true', focusable: 'false' });
  const tooltip = h('div', { class: 'chart-tooltip', hidden: true, 'aria-hidden': 'true' });
  const empty = h('div', { class: 'chart-empty', hidden: true, text: t('chart.noData') });
  const live = h('div', { class: 'sr-only', 'aria-live': 'polite' });
  const plot = h('div', { class: 'chart-plot', tabindex: '0', role: 'img', 'aria-label': title }, svg, tooltip, empty, live);
  plot.style.setProperty('--chart-height', `${height}px`);
  const el = h('figure', { class: 'chart' },
    h('figcaption', { class: 'chart-head' }, h('h3', { class: 'chart-title', text: title }), legend), plot);

  let data = { series: {}, from: null, to: null, step: 30 };
  let width = 0;
  let geometry = null;
  let cursorIndex = -1;

  const observer = new ResizeObserver((entries) => {
    const w = Math.floor(entries[0].contentRect.width);
    if (w > 0 && w !== width) {
      width = w;
      draw();
    }
  });
  observer.observe(plot);

  function seriesPoints(key) {
    return (data.series[key] ?? []).filter((p) => Array.isArray(p) && Number.isFinite(p[0]) && Number.isFinite(p[1]));
  }

  function draw() {
    svg.replaceChildren();
    tooltip.hidden = true;
    geometry = null;
    if (!width) return;
    const H = height;
    const W = width;
    svg.setAttribute('viewBox', `0 0 ${W} ${H}`);
    svg.setAttribute('width', String(W));
    svg.setAttribute('height', String(H));

    const all = series.map((sr) => seriesPoints(sr.key));
    const total = all.reduce((n, pts) => n + pts.length, 0);
    empty.hidden = total > 0;
    if (!total) {
      plot.setAttribute('aria-label', `${title}: ${t('chart.noData')}`);
      return;
    }

    let x0 = data.from ? Date.parse(data.from) : Infinity;
    let x1 = data.to ? Date.parse(data.to) : -Infinity;
    let lo = Infinity;
    let hi = -Infinity;
    for (const pts of all) {
      for (const p of pts) {
        if (!data.from) x0 = Math.min(x0, p[0]);
        if (!data.to) x1 = Math.max(x1, p[0]);
        const pMin = Number.isFinite(p[2]) ? p[2] : p[1];
        const pMax = Number.isFinite(p[3]) ? p[3] : p[1];
        lo = Math.min(lo, pMin, p[1]);
        hi = Math.max(hi, pMax, p[1]);
      }
    }
    if (!(x1 > x0)) x1 = x0 + MINUTE;
    let yTicks;
    if (fixedRange) {
      lo = Math.min(lo, fixedRange[0]);
      hi = Math.max(hi, fixedRange[1]);
      yTicks = niceTicks(lo, hi, 4).ticks;
    } else {
      const pad = (hi - lo) * 0.08 || Math.abs(hi) * 0.02 || 1;
      // Values that are never negative (voltages, runtimes, power) keep the axis at zero instead of padding below.
      yTicks = niceTicks(lo >= 0 ? Math.max(0, lo - pad) : lo - pad, hi + pad, 4).ticks;
    }
    lo = yTicks[0];
    hi = yTicks.at(-1);
    const innerW = W - MARGIN.l - MARGIN.r;
    const innerH = H - MARGIN.t - MARGIN.b;
    const sx = (ms) => MARGIN.l + ((ms - x0) / (x1 - x0)) * innerW;
    const sy = (v) => MARGIN.t + (1 - (v - lo) / (hi - lo)) * innerH;

    const grid = s('g', { class: 'chart-grid' });
    const axis = s('g', { class: 'chart-axis' });
    for (const v of yTicks) {
      const y = sy(v).toFixed(1);
      grid.appendChild(s('line', { x1: MARGIN.l, x2: W - MARGIN.r, y1: y, y2: y }));
      axis.appendChild(s('text', { x: MARGIN.l - 6, y, 'text-anchor': 'end', 'dominant-baseline': 'middle' }, fmtAxis(v)));
    }
    const { ticks: xTicks, step: xStep } = timeTicks(x0, x1, Math.max(2, Math.floor(innerW / 90)));
    for (const ms of xTicks) {
      const x = sx(ms).toFixed(1);
      grid.appendChild(s('line', { class: 'chart-grid-v', x1: x, x2: x, y1: MARGIN.t, y2: H - MARGIN.b }));
      const d = new Date(ms);
      const midnight = d.getHours() === 0 && d.getMinutes() === 0;
      const label = xStep >= DAY || (midnight && x1 - x0 > DAY) ? dayMonth(d) : timeOfDay(d);
      axis.appendChild(s('text', { x, y: H - 8, 'text-anchor': 'middle' }, label));
    }
    svg.append(grid, axis);

    const gapMs = Math.max((data.step || 30) * 1000 * 2.5, 3 * MINUTE);
    const lines = s('g', { class: 'chart-series' });
    all.forEach((pts, i) => {
      if (!pts.length) return;
      const segments = [];
      let current = [];
      for (let k = 0; k < pts.length; k += 1) {
        if (k && pts[k][0] - pts[k - 1][0] > gapMs) {
          segments.push(current);
          current = [];
        }
        current.push(pts[k]);
      }
      segments.push(current);
      let band = '';
      let line = '';
      for (const seg of segments) {
        if (seg.length === 1) {
          line += `M${(sx(seg[0][0]) - 1.5).toFixed(1)} ${sy(seg[0][1]).toFixed(1)}h3`;
          continue;
        }
        seg.forEach((p, k) => {
          line += `${k ? 'L' : 'M'}${sx(p[0]).toFixed(1)} ${sy(p[1]).toFixed(1)}`;
        });
        const hasBand = seg.some((p) => Number.isFinite(p[2]) && Number.isFinite(p[3]) && p[3] - p[2] > 1e-9);
        if (hasBand) {
          seg.forEach((p, k) => {
            band += `${k ? 'L' : 'M'}${sx(p[0]).toFixed(1)} ${sy(Number.isFinite(p[3]) ? p[3] : p[1]).toFixed(1)}`;
          });
          for (let k = seg.length - 1; k >= 0; k -= 1) {
            const p = seg[k];
            band += `L${sx(p[0]).toFixed(1)} ${sy(Number.isFinite(p[2]) ? p[2] : p[1]).toFixed(1)}`;
          }
          band += 'Z';
        }
      }
      if (band) lines.appendChild(s('path', { class: `chart-band series-${i}`, d: band }));
      lines.appendChild(s('path', { class: `chart-line series-${i}`, d: line }));
    });
    svg.appendChild(lines);

    const cursorLine = s('line', { class: 'chart-cursor', y1: MARGIN.t, y2: H - MARGIN.b, visibility: 'hidden' });
    const dots = series.map((_, i) => s('circle', { class: `chart-dot series-${i}`, r: 3.5, visibility: 'hidden' }));
    svg.append(cursorLine, ...dots);

    const reference = all.reduce((best, pts) => (pts.length > best.length ? pts : best), []);
    geometry = { sx, sy, all, reference, cursorLine, dots, x0, x1, innerW };

    const latest = series.map((sr, i) => {
      const last = all[i].at(-1);
      return last ? `${sr.label} ${fmt(last[1])}` : null;
    }).filter(Boolean).join(', ');
    plot.setAttribute('aria-label', `${title}. ${t('chart.latest')}: ${latest}. ${t('chart.keyboardHint')}`);
    if (cursorIndex >= 0) showCursor(Math.min(cursorIndex, reference.length - 1));
  }

  function fmtAxis(v) {
    return fmt(v);
  }

  function showCursor(index) {
    if (!geometry || index < 0 || !geometry.reference.length) return;
    cursorIndex = index;
    const ms = geometry.reference[index][0];
    const x = geometry.sx(ms);
    geometry.cursorLine.setAttribute('x1', x.toFixed(1));
    geometry.cursorLine.setAttribute('x2', x.toFixed(1));
    geometry.cursorLine.setAttribute('visibility', 'visible');
    const rows = [];
    let topY = Infinity;
    geometry.all.forEach((pts, i) => {
      const dot = geometry.dots[i];
      const k = nearest(pts, ms);
      const tolerance = Math.max((data.step || 30) * 1500, MINUTE);
      if (k < 0 || Math.abs(pts[k][0] - ms) > tolerance) {
        dot.setAttribute('visibility', 'hidden');
        return;
      }
      const p = pts[k];
      const y = geometry.sy(p[1]);
      topY = Math.min(topY, y);
      dot.setAttribute('cx', geometry.sx(p[0]).toFixed(1));
      dot.setAttribute('cy', y.toFixed(1));
      dot.setAttribute('visibility', 'visible');
      const range = Number.isFinite(p[2]) && Number.isFinite(p[3]) && p[3] - p[2] > 1e-9
        ? h('span', { class: 'tt-range', text: `${fmt(p[2])} – ${fmt(p[3])}` }) : null;
      rows.push(h('div', { class: 'tt-row' }, h('span', { class: `legend-swatch series-${i}` }),
        h('span', { class: 'tt-label', text: series[i].label }), h('strong', { text: fmt(p[1]) }), range));
    });
    tooltip.replaceChildren(h('div', { class: 'tt-time', text: dateTime(ms) }), ...rows);
    tooltip.hidden = false;
    const tipWidth = tooltip.offsetWidth;
    const left = Math.max(0, Math.min(width - tipWidth, x + 12 > width - tipWidth ? x - tipWidth - 12 : x + 12));
    const top = Math.max(0, Math.min(height - tooltip.offsetHeight, (Number.isFinite(topY) ? topY : MARGIN.t) - 20));
    tooltip.style.transform = `translate(${left.toFixed(0)}px, ${top.toFixed(0)}px)`;
    live.textContent = tooltip.textContent;
  }

  function hideCursor() {
    cursorIndex = -1;
    tooltip.hidden = true;
    if (!geometry) return;
    geometry.cursorLine.setAttribute('visibility', 'hidden');
    for (const d of geometry.dots) d.setAttribute('visibility', 'hidden');
  }

  plot.addEventListener('pointermove', (event) => {
    if (!geometry || !geometry.reference.length) return;
    const rect = plot.getBoundingClientRect();
    const x = event.clientX - rect.left;
    if (x < MARGIN.l - 4 || x > width - MARGIN.r + 4) {
      hideCursor();
      return;
    }
    const ms = geometry.x0 + ((x - MARGIN.l) / geometry.innerW) * (geometry.x1 - geometry.x0);
    showCursor(nearest(geometry.reference, ms));
  });
  plot.addEventListener('pointerleave', hideCursor);
  plot.addEventListener('blur', hideCursor);
  plot.addEventListener('keydown', (event) => {
    if (!geometry || !geometry.reference.length) return;
    const last = geometry.reference.length - 1;
    const bigStep = Math.max(1, Math.round(last / 20));
    let next = null;
    if (event.key === 'ArrowRight') next = cursorIndex < 0 ? last : Math.min(last, cursorIndex + 1);
    else if (event.key === 'ArrowLeft') next = cursorIndex < 0 ? last : Math.max(0, cursorIndex - 1);
    else if (event.key === 'PageUp') next = Math.max(0, (cursorIndex < 0 ? last : cursorIndex) - bigStep);
    else if (event.key === 'PageDown') next = Math.min(last, (cursorIndex < 0 ? last : cursorIndex) + bigStep);
    else if (event.key === 'Home') next = 0;
    else if (event.key === 'End') next = last;
    else if (event.key === 'Escape') {
      hideCursor();
      return;
    }
    if (next !== null) {
      event.preventDefault();
      showCursor(next);
    }
  });

  return {
    el,
    setData(seriesMap, { from, to, stepSeconds } = {}) {
      data = { series: seriesMap ?? {}, from: from ?? null, to: to ?? null, step: stepSeconds ?? 30 };
      draw();
    },
    setLoading(on) {
      el.classList.toggle('is-loading', !!on);
    },
    destroy() {
      observer.disconnect();
    },
  };
}
