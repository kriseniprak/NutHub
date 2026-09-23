// The one gauge of the panel, modelled on NutDesk: a thick 270° arc with round caps, the value and its unit in the
// middle, an optional secondary line and the min/max under the two ends. It is SVG, so it scales with its box and
// keeps the same proportions in a dashboard card and on the detail page. The arc animates through a CSS transition
// on stroke-dashoffset, set via the CSSOM (allowed by the Content-Security-Policy, unlike style attributes).
import { s, setText } from '../dom.js';
import { num, isNum } from '../format.js';
import { t } from '../i18n.js';
import { upsFlags } from '../labels.js';

const CX = 50;
const CY = 50;
const R = 40;
const LENGTH = (2 * Math.PI * R * 270) / 360;

function point(degrees) {
  const rad = (degrees * Math.PI) / 180;
  return [CX + R * Math.cos(rad), CY + R * Math.sin(rad)];
}

// Angles run clockwise from three o'clock (the SVG y axis points down): from bottom left (135°) over the top to
// bottom right (45°), which is the large, clockwise arc.
const START = point(135);
const END = point(45);
const ARC = `M ${START[0].toFixed(3)} ${START[1].toFixed(3)} A ${R} ${R} 0 1 1 ${END[0].toFixed(3)} ${END[1].toFixed(3)}`;

/** Colour level of a battery charge gauge: red when low or critical, amber on battery, the accent otherwise. */
export function batteryLevel(u) {
  const charge = u?.battery?.charge;
  if (!isNum(charge) || u?.availability !== 'available') return 'offline';
  const flags = upsFlags(u);
  const low = isNum(u.battery?.chargeLow) ? u.battery.chargeLow : 20;
  if (flags.includes('LB') || u.severity === 'critical' || charge <= low) return 'critical';
  if (flags.includes('OB')) return 'warning';
  return 'accent';
}

/** Font size (viewBox units) that keeps the value inside the ring whatever its length. */
function valueSize(text) {
  // About 80 % of the inner diameter: "100 %" and "229,5 V" stay clear of the arc.
  return Math.min(18, 62 / Math.max(1, text.length * 0.56)).toFixed(1);
}

/**
 * Creates a gauge; returns { el, set(value, { level, secondary, min, max }) }. `digits` is the number of decimals
 * shown, `range` shows min/max under the ends, `format(value)` replaces the default "value unit" text.
 */
export function arcGauge({ label, unit = '', min = 0, max = 100, digits = 0, range = true, className, format } = {}) {
  const track = s('path', { class: 'gauge-track', d: ARC });
  const arc = s('path', { class: 'gauge-arc', d: ARC, 'stroke-dasharray': `${LENGTH.toFixed(2)} ${LENGTH.toFixed(2)}` });
  arc.style.strokeDashoffset = LENGTH.toFixed(2);
  const value = s('text', { class: 'gauge-value', x: CX, y: CY, 'text-anchor': 'middle', 'dominant-baseline': 'central' });
  const secondary = s('text', { class: 'gauge-secondary', x: CX, y: 65, 'text-anchor': 'middle', 'dominant-baseline': 'central' });
  const minText = s('text', { class: 'gauge-range', x: START[0].toFixed(2), y: 93, 'text-anchor': 'middle' });
  const maxText = s('text', { class: 'gauge-range', x: END[0].toFixed(2), y: 93, 'text-anchor': 'middle' });
  const el = s('svg', {
    class: ['gauge', 'lvl-offline', 'is-empty', className],
    viewBox: range ? '0 0 100 96' : '0 0 100 86',
    role: 'meter',
    'aria-label': label,
    focusable: 'false',
  }, track, arc, value, secondary, range ? minText : null, range ? maxText : null);

  let lo = min;
  let hi = max;
  let level = 'offline';

  function text(v) {
    if (!isNum(v)) return '–';
    if (format) return format(v);
    return unit ? `${num(v, digits, digits)} ${unit}` : num(v, digits, digits);
  }

  function set(v, options = {}) {
    if (isNum(options.min)) lo = options.min;
    if (isNum(options.max)) hi = options.max;
    const known = isNum(v);
    const span = hi - lo || 1;
    const fraction = known ? Math.max(0, Math.min(1, (v - lo) / span)) : 0;
    arc.style.strokeDashoffset = (LENGTH * (1 - fraction)).toFixed(2);
    // A zero-length arc would still draw its round caps as a dot: hide it instead.
    el.classList.toggle('is-empty', !known || fraction <= 0);
    const label = text(v);
    setText(value, label);
    value.setAttribute('font-size', valueSize(label));
    const sub = options.secondary ?? '';
    setText(secondary, sub);
    value.setAttribute('y', sub ? '45' : String(CY));
    setText(minText, num(lo, digits > 0 && !Number.isInteger(lo) ? 1 : 0));
    setText(maxText, num(hi, digits > 0 && !Number.isInteger(hi) ? 1 : 0));
    el.setAttribute('aria-valuemin', String(lo));
    el.setAttribute('aria-valuemax', String(hi));
    if (known) {
      el.setAttribute('aria-valuenow', String(v));
      el.setAttribute('aria-valuetext', sub ? `${label}, ${sub}` : label);
    } else {
      el.removeAttribute('aria-valuenow');
      el.setAttribute('aria-valuetext', t('common.unknown'));
    }
    const next = known ? options.level || 'accent' : 'offline';
    if (next !== level) {
      el.classList.replace(`lvl-${level}`, `lvl-${next}`);
      level = next;
    }
  }

  set(null);
  return { el, set };
}
