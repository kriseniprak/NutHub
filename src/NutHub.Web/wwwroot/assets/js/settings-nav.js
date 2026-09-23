// The settings area: its pages in groups, shared by the secondary navigation of the shell (a list on the left on
// wide screens) and by the settings home page (the list itself on phones).

export const SETTINGS_GROUPS = [
  {
    key: 'settings.group.devices',
    items: [
      { path: '/settings/ups', key: 'nav.upsDevices', descKey: 'settings.desc.ups' },
      { path: '/settings/import', key: 'nav.import', descKey: 'settings.desc.import' },
    ],
  },
  {
    key: 'settings.group.access',
    items: [
      { path: '/settings/web-users', key: 'nav.webUsers', descKey: 'settings.desc.webUsers' },
      { path: '/settings/nut-users', key: 'nav.nutUsers', descKey: 'settings.desc.nutUsers' },
    ],
  },
  {
    key: 'settings.group.server',
    items: [
      { path: '/settings/nut', key: 'nav.nutServer', descKey: 'settings.desc.nut' },
      { path: '/settings/web', key: 'nav.webPanel', descKey: 'settings.desc.web' },
      { path: '/settings/identity', key: 'nav.serverIdentity', descKey: 'settings.desc.identity' },
    ],
  },
  {
    key: 'settings.group.alerts',
    items: [
      { path: '/settings/notifications', key: 'nav.notifications', descKey: 'settings.desc.notifications' },
      { path: '/settings/host-protection', key: 'nav.hostProtection', descKey: 'settings.desc.hostProtection' },
    ],
  },
  {
    key: 'settings.group.data',
    items: [
      { path: '/settings/history', key: 'nav.history', descKey: 'settings.desc.history' },
    ],
  },
  {
    key: 'settings.group.system',
    items: [
      { path: '/settings/system', key: 'nav.system', descKey: 'settings.desc.system' },
      { path: '/settings/logs', key: 'nav.logs', descKey: 'settings.desc.logs' },
    ],
  },
];

/** The settings item a path belongs to ("/settings/ups/rack1/edit" -> UPS devices), or null. */
export function settingsItem(path) {
  let best = null;
  for (const group of SETTINGS_GROUPS) {
    for (const item of group.items) {
      if ((path === item.path || path.startsWith(`${item.path}/`)) && (!best || item.path.length > best.path.length)) best = item;
    }
  }
  return best;
}
