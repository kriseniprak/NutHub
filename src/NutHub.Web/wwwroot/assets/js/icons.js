// Stroke icons drawn on a 24x24 grid. Each icon is a list of [element, attributes]; they inherit the text colour
// (currentColor) so they follow the theme and the status colours.
import { s } from './dom.js';

const C = (cx, cy, r) => ['circle', { cx, cy, r }];
const P = (d) => ['path', { d }];
const DOT = (cx, cy) => ['circle', { cx, cy, r: 1.3, class: 'icon-fill' }];

/** Outline of a gear with eight teeth (the Settings tab), computed once instead of hand-written. */
function gear() {
  const steps = 32;
  let d = '';
  for (let i = 0; i < steps; i += 1) {
    const radius = i % 4 < 2 ? 9.5 : 7.2;
    const angle = ((i - 0.5) / steps) * 2 * Math.PI;
    d += `${i ? 'L' : 'M'}${(12 + radius * Math.cos(angle)).toFixed(2)} ${(12 + radius * Math.sin(angle)).toFixed(2)}`;
  }
  return `${d}Z`;
}

const ICONS = {
  close: [P('M6 6l12 12M18 6L6 18')],
  battery: [P('M3 8a2 2 0 0 1 2-2h11a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2zM21 10v4')],
  sliders: [P('M4 6h9M17 6h3M4 12h3M11 12h9M4 18h11M19 18h1'), C(15, 6, 2), C(9, 12, 2), C(17, 18, 2)],
  user: [C(12, 8, 4), P('M4 21a8 8 0 0 1 16 0')],
  key: [C(8, 15, 4), P('M11 12l9-9M17 6l3 3')],
  shield: [P('M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z')],
  info: [C(12, 12, 9), P('M12 11v6'), DOT(12, 7.5)],
  warning: [P('M12 3.5L2.5 20h19zM12 9.5v5'), DOT(12, 17)],
  critical: [P('M8 3h8l5 5v8l-5 5H8l-5-5V8zM12 7.5v6'), DOT(12, 16.5)],
  check: [P('M5 12.5l4.5 4.5L19 7')],
  checkCircle: [C(12, 12, 9), P('M8 12.5l3 3 5-6')],
  chevronDown: [P('M6 9l6 6 6-6')],
  chevronRight: [P('M9 6l6 6-6 6')],
  chevronLeft: [P('M15 6l-6 6 6 6')],
  logout: [P('M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4M16 17l5-5-5-5M21 12H9')],
  login: [P('M15 3h4a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2h-4M10 17l5-5-5-5M15 12H3')],
  plus: [P('M12 5v14M5 12h14')],
  edit: [P('M4 20h4L19 9l-4-4L4 16zM13.5 6.5l4 4')],
  trash: [P('M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3')],
  refresh: [P('M20 11a8 8 0 0 0-14.8-3M4 4v4h4M4 13a8 8 0 0 0 14.8 3M20 20v-4h-4')],
  arrowUp: [P('M12 19V5M6 11l6-6 6 6')],
  arrowDown: [P('M12 5v14M6 13l6 6 6-6')],
  arrowLeft: [P('M19 12H5M11 6l-6 6 6 6')],
  grip: [DOT(9, 6), DOT(15, 6), DOT(9, 12), DOT(15, 12), DOT(9, 18), DOT(15, 18)],
  search: [C(11, 11, 7), P('M20 20l-4-4')],
  download: [P('M12 4v12M6 10l6 6 6-6M4 20h16')],
  upload: [P('M12 20V8M6 14l6-6 6 6M4 4h16')],
  copy: [P('M9 9h11v11H9zM5 15H4V4h11v1')],
  more: [DOT(12, 5), DOT(12, 12), DOT(12, 19)],
  play: [P('M7 4l13 8-13 8z')],
  filter: [P('M3 5h18l-7 8v6l-4 2v-8z')],
  xCircle: [C(12, 12, 9), P('M9 9l6 6M15 9l-6 6')],
  send: [P('M21 3L10 14M21 3l-7 18-4-7-7-4z')],
  settings: [C(12, 12, 3), P(gear())],
};

/**
 * Returns an inline SVG icon. Decorative by default (aria-hidden); pass `label` when the icon carries meaning
 * on its own.
 */
export function icon(name, { label, className } = {}) {
  const def = ICONS[name] ?? ICONS.info;
  const svg = s('svg', {
    class: ['icon', className],
    viewBox: '0 0 24 24',
    fill: 'none',
    stroke: 'currentColor',
    'stroke-width': 2,
    'stroke-linecap': 'round',
    'stroke-linejoin': 'round',
    focusable: 'false',
    'aria-hidden': label ? null : 'true',
    role: label ? 'img' : null,
    'aria-label': label ?? null,
  });
  for (const [tag, attrs] of def) svg.appendChild(s(tag, attrs));
  return svg;
}
