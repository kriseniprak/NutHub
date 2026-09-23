// System information and configuration export.
import { h, replace } from '../../dom.js';
import { icon } from '../../icons.js';
import { t } from '../../i18n.js';
import { api, isAbort } from '../../api.js';
import { navigate } from '../../router.js';
import { pageHeader, section, loading, errorState, definitionList } from '../../page.js';
import { pill } from '../../components/badge.js';
import { duration, bytes, dateTime } from '../../format.js';
import { loadDrivers } from './common.js';

export default {
  title: () => t('nav.system'),
  async render(el, ctx) {
    const refresh = h('button', { type: 'button', class: 'btn' }, icon('refresh'), h('span', { text: t('common.refresh') }));
    refresh.addEventListener('click', () => navigate('/settings/system', { replace: true }));
    el.append(pageHeader({ title: t('nav.system'), subtitle: t('system.subtitle'), actions: refresh }));
    const body = h('div', {}, loading());
    el.appendChild(body);
    let info;
    let drivers = [];
    try {
      [info, drivers] = await Promise.all([
        api.get('api/admin/system', { signal: ctx.signal }),
        loadDrivers(ctx.signal).catch(() => []),
      ]);
    } catch (err) {
      if (isAbort(err)) return;
      replace(body, errorState(err, () => navigate('/settings/system', { replace: true })));
      return;
    }
    const mono = (v) => (v ? h('span', { class: 'mono', text: v }) : '–');
    const name = (id) => drivers.find((d) => d.id === id)?.displayName ?? id;
    replace(body,
      section({ title: t('system.server') }, definitionList([
        [t('system.version'), info.version],
        [t('system.os'), info.os],
        [t('system.architecture'), info.architecture],
        [t('system.framework'), info.framework],
        [t('system.machine'), info.machineName],
        [t('system.pid'), String(info.processId ?? '–')],
        [t('system.service'), info.isService ? t('common.yes') : t('common.no')],
        [t('system.started'), dateTime(info.startedAt)],
        [t('system.uptime'), duration(info.uptimeSeconds)],
        [t('system.memory'), bytes(info.workingSetBytes)],
      ])),
      section({ title: t('system.paths') }, definitionList([
        [t('system.dataDir'), mono(info.dataDirectory)],
        [t('system.configFile'), mono(info.configFile)],
        [t('system.databaseFile'), mono(info.databaseFile)],
      ], 'deflist-wide')),
      section({ title: t('system.drivers') }, h('ul', { class: 'driver-support' },
        (info.drivers ?? []).map((d) => h('li', {}, h('span', { text: name(d.id) }), h('code', { text: d.id }),
          d.supported ? pill(t('system.supported'), 'ok') : pill(t('system.unsupported'), 'neutral'))))),
      section({ title: t('system.backup'), description: t('system.backupHint') },
        h('a', { class: 'btn btn-primary', href: 'api/admin/config/export', download: 'nuthub-config.json' }, icon('download'), h('span', { text: t('system.export') }))));
  },
};
