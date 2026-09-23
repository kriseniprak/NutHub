// Tiny DOM construction helpers. The panel runs under a strict Content-Security-Policy (no inline styles, no
// eval), so markup is always built as nodes: text is never parsed as HTML and dynamic styles go through the CSSOM.

const SVG_NS = 'http://www.w3.org/2000/svg';

/** Properties set as DOM properties rather than attributes (they reflect live state). */
const PROPS = new Set(['value', 'checked', 'disabled', 'selected', 'indeterminate', 'multiple', 'readOnly',
  'required', 'hidden']);

function applyProps(el, props, isSvg) {
  if (!props) return;
  for (const [key, value] of Object.entries(props)) {
    if (value === undefined || value === null || value === false) continue;
    if (key === 'class') {
      const cls = Array.isArray(value) ? value.filter(Boolean).join(' ') : String(value);
      if (cls) el.setAttribute('class', cls);
    } else if (key === 'text') {
      el.textContent = String(value);
    } else if (key === 'dataset') {
      for (const [k, v] of Object.entries(value)) if (v !== undefined && v !== null) el.dataset[k] = String(v);
    } else if (key === 'style') {
      for (const [p, v] of Object.entries(value)) if (v !== undefined && v !== null) el.style.setProperty(p, String(v));
    } else if (key.startsWith('on') && typeof value === 'function') {
      el.addEventListener(key.slice(2).toLowerCase(), value);
    } else if (key === 'ref' && typeof value === 'function') {
      value(el);
    } else if (!isSvg && PROPS.has(key)) {
      el[key] = value;
    } else {
      el.setAttribute(key, value === true ? '' : String(value));
    }
  }
}

function append(el, children) {
  for (const child of children) {
    if (child === undefined || child === null || child === false || child === true) continue;
    if (Array.isArray(child)) append(el, child);
    else if (child instanceof Node) el.appendChild(child);
    else el.appendChild(document.createTextNode(String(child)));
  }
}

/**
 * Creates an HTML element. `props` keys: class (string or array), text, dataset, style (CSS property map, custom
 * properties allowed), on<Event> handlers, ref callback, DOM properties (value, checked...) and plain attributes.
 */
export function h(tag, props, ...children) {
  const el = document.createElement(tag);
  applyProps(el, props, false);
  append(el, children);
  return el;
}

/** Creates an SVG element (same props as {@link h}). */
export function s(tag, props, ...children) {
  const el = document.createElementNS(SVG_NS, tag);
  applyProps(el, props, true);
  append(el, children);
  return el;
}

/** Removes every child of an element. */
export function clear(el) {
  while (el.firstChild) el.removeChild(el.firstChild);
  return el;
}

/** Replaces the children of an element. */
export function replace(el, ...children) {
  clear(el);
  append(el, children);
  return el;
}

/** Sets the text of an element only when it changed, so screen readers and transitions are not disturbed. */
export function setText(el, value) {
  const text = value === undefined || value === null ? '' : String(value);
  if (el.textContent !== text) el.textContent = text;
}

/** Toggles a class and returns whether it changed. */
export function toggleClass(el, cls, on) {
  const had = el.classList.contains(cls);
  if (had !== !!on) el.classList.toggle(cls, !!on);
  return had !== !!on;
}

let idCounter = 0;
/** Returns an id unique in this page, for label/aria wiring. */
export function uid(prefix = 'id') {
  idCounter += 1;
  return `${prefix}-${idCounter}`;
}

/** Briefly highlights an element whose value changed (respecting reduced motion through CSS). */
export function flash(el) {
  el.classList.remove('flash');
  // Reading offsetWidth restarts the animation when the class is re-added.
  void el.offsetWidth;
  el.classList.add('flash');
}

/** Debounces a function. */
export function debounce(fn, ms) {
  let timer = null;
  const wrapped = (...args) => {
    clearTimeout(timer);
    timer = setTimeout(() => fn(...args), ms);
  };
  wrapped.cancel = () => clearTimeout(timer);
  return wrapped;
}

/** Encodes a path segment for API URLs. */
export function seg(value) {
  return encodeURIComponent(String(value));
}
