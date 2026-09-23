// Import from NUT: paste (or load) ups.conf and upsd.users, preview what NutHub would create, then apply.
import { h, replace } from '../../dom.js';
import { icon } from '../../icons.js';
import { t } from '../../i18n.js';
import { api, errorText } from '../../api.js';
import { pageHeader, section, callout } from '../../page.js';
import { field, textArea, formErrorBox } from '../../components/fields.js';
import { dataTable } from '../../components/table.js';
import { confirmDialog } from '../../components/dialog.js';
import { toast } from '../../components/toast.js';
import { monitorRoleText } from '../../labels.js';
import { trackDirty } from './common.js';

const MAX_FILE = 512 * 1024;

function fileLoader(target, label) {
  const input = h('input', { type: 'file', accept: '.conf,.users,.txt,text/plain', class: 'sr-only', tabindex: '-1' });
  const button = h('button', { type: 'button', class: 'btn btn-sm' }, icon('upload'), h('span', { text: label }));
  button.addEventListener('click', () => input.click());
  input.addEventListener('change', async () => {
    const file = input.files?.[0];
    if (!file) return;
    if (file.size > MAX_FILE) {
      toast(t('import.fileTooLarge'), { kind: 'error' });
      return;
    }
    target.value = await file.text();
    target.dispatchEvent(new Event('input', { bubbles: true }));
    input.value = '';
  });
  return h('span', {}, button, input);
}

export default {
  title: () => t('nav.import'),
  render(el, ctx) {
    const upsConf = textArea({ rows: 12, placeholder: '[rack1]\n  driver = usbhid-ups\n  port = auto\n  desc = "Rack A"' });
    const upsdUsers = textArea({ rows: 10, placeholder: '[upsmon]\n  password = secret\n  upsmon secondary' });
    const errors = formErrorBox();
    const preview = h('div');
    const previewButton = h('button', { type: 'submit', class: 'btn btn-primary' }, icon('search'), h('span', { text: t('import.preview') }));
    const form = h('form', { class: 'form', novalidate: true },
      errors.el,
      h('div', { class: 'form-grid import-grid' },
        h('div', { class: 'stack-sm' },
          field({ label: 'ups.conf', control: upsConf, name: 'upsConf', help: t('import.upsConfHelp') }),
          fileLoader(upsConf, t('import.loadFile'))),
        h('div', { class: 'stack-sm' },
          field({ label: 'upsd.users', control: upsdUsers, name: 'upsdUsers', help: t('import.usersHelp') }),
          fileLoader(upsdUsers, t('import.loadFile')))),
      h('div', { class: 'form-actions' }, previewButton));
    const dirty = trackDirty(form, ctx);

    el.append(
      pageHeader({ title: t('nav.import'), subtitle: t('import.subtitle') }),
      callout('info', t('import.howTitle'), h('p', { text: t('import.howText') })),
      section({ title: t('import.files') }, form),
      preview);

    async function run(apply) {
      const body = { upsConf: upsConf.value, upsdUsers: upsdUsers.value, apply };
      return api.post('api/admin/import/nut', body, { signal: ctx.signal });
    }

    function renderPreview(result, applied) {
      const upsTable = dataTable({
        caption: t('import.upsList'),
        rowKey: (u) => u.name,
        emptyText: t('import.noUps'),
        columns: [
          { key: 'name', label: t('common.name'), render: (u) => h('strong', { text: u.name }) },
          { key: 'driver', label: t('upsEdit.driver'), render: (u) => h('code', { text: u.driver }) },
          { key: 'description', label: t('common.description'), render: (u) => u.description || '–' },
          {
            key: 'options',
            label: t('import.options'),
            sortable: false,
            render: (u) => h('span', { class: 'cell-mono', text: Object.entries(u.options ?? {}).map(([k, v]) => `${k}=${v}`).join(', ') || '–' }),
          },
        ],
      });
      upsTable.setRows(result.ups ?? []);
      const usersTable = dataTable({
        caption: t('import.userList'),
        rowKey: (u) => u.name,
        emptyText: t('import.noUsers'),
        columns: [
          { key: 'name', label: t('common.name'), render: (u) => h('strong', { text: u.name }) },
          { key: 'monitor', label: t('nutUsers.monitor'), render: (u) => monitorRoleText(u.monitor) },
          { key: 'actions', label: t('nutUsers.actions'), render: (u) => (u.actions ?? []).join(', ') || '–' },
          { key: 'instantCommands', label: t('nutUsers.instantCommands'), render: (u) => (u.instantCommands ?? []).join(', ') || '–' },
        ],
      });
      usersTable.setRows(result.nutUsers ?? []);
      const warnings = result.warnings ?? [];
      const applyButton = h('button', { type: 'button', class: 'btn btn-primary' }, icon('check'), h('span', { text: t('import.apply') }));
      applyButton.disabled = applied || (!(result.ups ?? []).length && !(result.nutUsers ?? []).length);
      applyButton.addEventListener('click', async () => {
        const ok = await confirmDialog({
          title: t('import.applyTitle'),
          message: t('import.applyText', { ups: (result.ups ?? []).length, users: (result.nutUsers ?? []).length }),
          confirmLabel: t('import.apply'),
        });
        if (!ok) return;
        applyButton.disabled = true;
        applyButton.classList.add('is-busy');
        try {
          const done = await run(true);
          dirty.clean();
          toast(t('import.applied'), { kind: 'success' });
          renderPreview(done, true);
        } catch (err) {
          toast(errorText(err), { kind: 'error' });
          applyButton.disabled = false;
        } finally {
          applyButton.classList.remove('is-busy');
        }
      });
      replace(preview,
        section({ title: applied ? t('import.resultTitle') : t('import.previewTitle') },
          applied ? callout('ok', t('import.appliedTitle'), h('p', {}, t('import.appliedText'), ' ', h('a', { href: '#/settings/ups', text: t('nav.upsDevices') }))) : null,
          warnings.length ? callout('warning', t('import.warnings'), h('ul', {}, warnings.map((w) => h('li', { text: w })))) : null,
          h('h3', { class: 'subhead', text: t('import.upsList') }), upsTable.el,
          h('h3', { class: 'subhead', text: t('import.userList') }), usersTable.el,
          applied ? null : h('div', { class: 'form-actions' }, applyButton, h('span', { class: 'muted small', text: t('import.applyHint') }))));
      preview.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }

    form.addEventListener('submit', async (event) => {
      event.preventDefault();
      errors.show(null);
      if (!upsConf.value.trim() && !upsdUsers.value.trim()) {
        errors.show(t('import.nothing'));
        return;
      }
      previewButton.disabled = true;
      previewButton.classList.add('is-busy');
      try {
        renderPreview(await run(false), false);
      } catch (err) {
        if (err?.name !== 'AbortError') errors.show(errorText(err));
      } finally {
        previewButton.disabled = false;
        previewButton.classList.remove('is-busy');
      }
    });
  },
};
