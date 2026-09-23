// Hash router: "#/ups/rack1?tab=x". Page modules are loaded on demand and export
//   default { title(params) -> string, render(element, ctx) }
// where ctx = { params, query, signal, onCleanup(fn), setDirty(bool), setTitle(text) }. The signal aborts when
// the user leaves the page, so pending requests and timers of a page never outlive it.
import { t } from './i18n.js';

const routes = [];
let outlet = null;
let guard = null;
let onRoute = null;
let current = null;
let renderToken = 0;
let dirty = false;
let confirmLeave = null;
let initial = true;

/**
 * Declares a route. `pattern` like "/ups/:name"; `load` returns a promise of the page module. Options may carry
 * `redirect`: a path or a function(params) returning one, for addresses that moved (bookmarks keep working).
 */
export function addRoute(pattern, load, options = {}) {
  const keys = [];
  const source = pattern
    .split('/')
    .map((part) => {
      if (part.startsWith(':')) {
        keys.push(part.slice(1));
        return '([^/]+)';
      }
      return part.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    })
    .join('/');
  routes.push({ pattern, regex: new RegExp(`^${source}/?$`), keys, load, ...options });
}

/** Parses the current location hash. */
export function currentLocation() {
  let raw = window.location.hash.replace(/^#/, '');
  try {
    raw = decodeURI(raw);
  } catch {
    // A malformed escape in a hand-typed address: use it as it is (it will match no route).
  }
  raw = raw || '/';
  const [path, queryString = ''] = raw.split('?');
  return { path: path.startsWith('/') ? path : `/${path}`, query: new URLSearchParams(queryString), raw };
}

function match(path) {
  for (const route of routes) {
    const m = route.regex.exec(path);
    if (m) {
      const params = {};
      route.keys.forEach((key, i) => {
        try {
          params[key] = decodeURIComponent(m[i + 1]);
        } catch {
          params[key] = m[i + 1];
        }
      });
      return { route, params };
    }
  }
  return null;
}

/** Builds a hash link: link('/ups', name) -> "#/ups/<encoded name>". */
export function link(...parts) {
  const [first, ...rest] = parts;
  return `#${first}${rest.map((p) => `/${encodeURIComponent(p)}`).join('')}`;
}

/** Navigates to a path ("/events"). */
export function navigate(path, { replace = false } = {}) {
  const hash = path.startsWith('#') ? path : `#${path}`;
  if (replace) {
    window.history.replaceState(null, '', hash);
    render();
  } else if (window.location.hash === hash) {
    render();
  } else {
    window.location.hash = hash;
  }
}

/** Re-renders the current page (language change, sign-in). */
export function refresh() {
  dirty = false;
  render();
}

function cleanupCurrent() {
  if (!current) return;
  current.controller.abort();
  for (const fn of current.cleanups.splice(0)) {
    try {
      fn();
    } catch (err) {
      console.error(err);
    }
  }
  current = null;
}

async function render() {
  const token = ++renderToken;
  const location = currentLocation();
  const found = match(location.path);
  dirty = false;

  if (found?.route.redirect) {
    const target = typeof found.route.redirect === 'function' ? found.route.redirect(found.params) : found.route.redirect;
    const query = location.query.toString();
    navigate(query ? `${target}?${query}` : target, { replace: true });
    return;
  }

  if (found && guard) {
    const decision = guard(found.route, location);
    if (decision?.redirect) {
      navigate(decision.redirect, { replace: true });
      return;
    }
    if (decision?.forbidden) {
      cleanupCurrent();
      showMessagePage(t('page.forbidden.title'), t('page.forbidden.text'), location);
      return;
    }
  }

  if (!found) {
    cleanupCurrent();
    showMessagePage(t('page.notFound.title'), t('page.notFound.text'), location);
    return;
  }

  let module;
  try {
    module = (await found.route.load()).default;
  } catch (err) {
    console.error(err);
    if (token !== renderToken) return;
    cleanupCurrent();
    showMessagePage(t('page.loadFailed.title'), t('page.loadFailed.text'), location);
    return;
  }
  if (token !== renderToken) return;

  cleanupCurrent();
  const controller = new AbortController();
  const cleanups = [];
  current = { controller, cleanups, hash: window.location.hash || '#/', route: found.route };
  outlet.replaceChildren();
  outlet.className = ['content', found.route.bare ? 'content--bare' : '', found.route.wide ? 'content--wide' : '']
    .filter(Boolean).join(' ');

  const ctx = {
    params: found.params,
    query: location.query,
    signal: controller.signal,
    onCleanup: (fn) => cleanups.push(fn),
    setDirty: (value) => {
      if (current?.controller === controller) dirty = !!value;
    },
    setTitle: (text) => setDocumentTitle(text),
  };
  setDocumentTitle(typeof module.title === 'function' ? module.title(found.params) : '');
  onRoute?.(found.route, location);
  try {
    await module.render(outlet, ctx);
  } catch (err) {
    if (err?.name === 'AbortError') return;
    console.error(err);
    if (current?.controller === controller) {
      outlet.replaceChildren();
      showMessagePage(t('page.loadFailed.title'), t('page.loadFailed.text'), location, true);
    }
    return;
  }
  if (!initial && current?.controller === controller) {
    window.scrollTo(0, 0);
    const heading = outlet.querySelector('h1');
    if (heading) {
      heading.tabIndex = -1;
      heading.focus({ preventScroll: true });
    }
  }
  initial = false;
}

let titleSuffix = 'NutHub';

/** Sets the suffix of the document title (the server name). */
export function setTitleSuffix(text) {
  titleSuffix = text || 'NutHub';
}

function setDocumentTitle(text) {
  document.title = text ? `${text} · ${titleSuffix}` : titleSuffix;
}

function showMessagePage(title, text, location, keepOutlet = false) {
  if (!keepOutlet) outlet.replaceChildren();
  outlet.className = 'content';
  const box = document.createElement('div');
  box.className = 'empty-state';
  const h1 = document.createElement('h1');
  h1.textContent = title;
  const p = document.createElement('p');
  p.textContent = text;
  const a = document.createElement('a');
  a.href = '#/';
  a.className = 'btn btn-primary';
  a.textContent = t('page.backToDashboard');
  box.append(h1, p, a);
  outlet.appendChild(box);
  setDocumentTitle(title);
  onRoute?.(null, location);
}

/**
 * Starts routing. `guardFn(route, location)` may return { redirect: "/login" } or { forbidden: true };
 * `confirmLeaveFn()` resolves true when the user accepts to lose unsaved changes.
 */
export function startRouter({ element, guard: guardFn, onRoute: routeFn, confirmLeave: confirmFn }) {
  outlet = element;
  guard = guardFn;
  onRoute = routeFn;
  confirmLeave = confirmFn;

  window.addEventListener('hashchange', async () => {
    if (dirty && current && confirmLeave) {
      const target = window.location.hash;
      // Put the address back while asking (replaceState does not raise another hashchange).
      window.history.replaceState(null, '', current.hash);
      const ok = await confirmLeave();
      if (!ok) return;
      dirty = false;
      if (window.location.hash === target) render();
      else window.location.hash = target;
      return;
    }
    render();
  });
  window.addEventListener('beforeunload', (event) => {
    if (dirty) {
      event.preventDefault();
      event.returnValue = '';
    }
  });
  render();
}
