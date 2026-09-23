// Light / dark theme: follows the system by default (prefers-color-scheme); a manual choice is stored per
// browser. theme-init.js applies the stored choice before the first paint.
const STORAGE_KEY = 'nuthub.theme';
const listeners = new Set();
const media = window.matchMedia('(prefers-color-scheme: dark)');

/** The chosen mode: "system", "light" or "dark". */
export function themeMode() {
  const attr = document.documentElement.getAttribute('data-theme');
  return attr === 'light' || attr === 'dark' ? attr : 'system';
}

/** The theme actually shown ("light" or "dark"). */
export function effectiveTheme() {
  const mode = themeMode();
  if (mode !== 'system') return mode;
  return media.matches ? 'dark' : 'light';
}

function updateMeta() {
  const meta = document.querySelector('meta[name="theme-color"]');
  if (meta) meta.setAttribute('content', effectiveTheme() === 'dark' ? '#202020' : '#f3f3f3');
}

/** Sets the mode and remembers it. */
export function setThemeMode(mode) {
  const root = document.documentElement;
  if (mode === 'light' || mode === 'dark') root.setAttribute('data-theme', mode);
  else root.removeAttribute('data-theme');
  try {
    if (mode === 'light' || mode === 'dark') window.localStorage.setItem(STORAGE_KEY, mode);
    else window.localStorage.removeItem(STORAGE_KEY);
  } catch {
    // Not persisted; applies to this tab only.
  }
  updateMeta();
  for (const fn of listeners) fn(mode);
}

/** Registers a listener for theme changes (manual or system). */
export function onThemeChange(fn) {
  listeners.add(fn);
  return () => listeners.delete(fn);
}

media.addEventListener('change', () => {
  updateMeta();
  if (themeMode() === 'system') for (const fn of listeners) fn('system');
});
updateMeta();
