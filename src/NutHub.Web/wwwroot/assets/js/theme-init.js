// Runs before the first paint (classic script, not a module) so a stored theme or language never flashes the
// default one. Everything else lives in the ES modules loaded afterwards.
(function () {
  'use strict';
  var root = document.documentElement;
  try {
    var theme = window.localStorage.getItem('nuthub.theme');
    if (theme === 'light' || theme === 'dark') {
      root.setAttribute('data-theme', theme);
    }
    var lang = window.localStorage.getItem('nuthub.lang');
    if (lang === 'en' || lang === 'it') {
      root.setAttribute('lang', lang);
    }
  } catch (e) {
    // Storage can be disabled (private mode, policies): the defaults apply.
  }
})();
