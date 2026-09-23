// Settings home: every settings page in its group with a one-line description. On phones it is the settings
// navigation itself; on wide screens it is an overview (the pages then show the same list on their left).
import { h } from '../dom.js';
import { icon } from '../icons.js';
import { t } from '../i18n.js';
import { pageHeader } from '../page.js';
import { SETTINGS_GROUPS } from '../settings-nav.js';

export default {
  title: () => t('nav.settings'),
  render(el) {
    el.append(
      pageHeader({ title: t('nav.settings'), subtitle: t('settings.subtitle') }),
      h('div', { class: 'settings-home' }, SETTINGS_GROUPS.map((group) => h('section', { class: 'settings-home-group', 'aria-label': t(group.key) },
        h('h2', { class: 'settings-home-title', text: t(group.key) }),
        h('ul', { class: 'card settings-home-list' }, group.items.map((item) => h('li', {},
          h('a', { class: 'settings-home-link', href: `#${item.path}` },
            h('span', { class: 'settings-home-text' },
              h('span', { class: 'settings-home-name', text: t(item.key) }),
              h('span', { class: 'settings-home-desc', text: t(item.descKey) })),
            icon('chevronRight', { className: 'icon-sm' })))))))),
    );
  },
};
