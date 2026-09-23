// Minimal sparkline for the dashboard cards: average line plus a soft area, no axes.
import { s, h } from '../dom.js';

const W = 120;
const H = 32;

/** Creates a sparkline; returns { el, set(points, { min, max }) } with points [[ms, avg, min, max], ...]. */
export function sparkline({ label = '' } = {}) {
  const area = s('path', { class: 'spark-area' });
  const line = s('path', { class: 'spark-line', 'vector-effect': 'non-scaling-stroke' });
  const svg = s('svg', { viewBox: `0 0 ${W} ${H}`, preserveAspectRatio: 'none', 'aria-hidden': 'true', focusable: 'false' },
    area, line);
  const el = h('div', { class: 'sparkline is-empty', role: 'img', 'aria-label': label }, svg);

  function set(points, { min, max } = {}) {
    const valid = (points ?? []).filter((p) => Array.isArray(p) && Number.isFinite(p[1]));
    if (valid.length < 2) {
      line.setAttribute('d', '');
      area.setAttribute('d', '');
      el.classList.add('is-empty');
      return;
    }
    el.classList.remove('is-empty');
    const t0 = valid[0][0];
    const t1 = valid[valid.length - 1][0];
    let lo = Number.isFinite(min) ? min : Math.min(...valid.map((p) => p[1]));
    let hi = Number.isFinite(max) ? max : Math.max(...valid.map((p) => p[1]));
    if (hi - lo < 1e-9) {
      hi += 1;
      lo -= 1;
    }
    const x = (ms) => ((ms - t0) / Math.max(1, t1 - t0)) * W;
    const y = (v) => H - 2 - ((v - lo) / (hi - lo)) * (H - 4);
    let d = '';
    valid.forEach((p, i) => {
      d += `${i ? 'L' : 'M'}${x(p[0]).toFixed(2)} ${y(p[1]).toFixed(2)}`;
    });
    line.setAttribute('d', d);
    area.setAttribute('d', `${d}L${W} ${H}L0 ${H}Z`);
  }
  return { el, set, setLabel: (text) => el.setAttribute('aria-label', text) };
}
