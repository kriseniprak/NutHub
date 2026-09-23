// Running an instant command of a UPS (from the Actions menu of the detail page): a confirmation for dangerous
// commands and a parameter for the commands that take one.
import { h, seg } from '../../dom.js';
import { t } from '../../i18n.js';
import { api, commandResultText, errorText } from '../../api.js';
import { openDialog } from '../../components/dialog.js';
import { field, numberInput, textInput, numberValue, setFieldError } from '../../components/fields.js';
import { toast } from '../../components/toast.js';
import { callout } from '../../page.js';

/** Commands that take a value in NUT (a delay in seconds). */
export function commandParameter(name) {
  if (/(^|\.)delay$/.test(name)) return { kind: 'seconds' };
  return null;
}

/** Runs a command after the needed confirmation / parameter input; resolves when done or cancelled. */
export async function runCommand(upsName, command) {
  const param = commandParameter(command.name);
  const execute = async (parameter) => {
    const result = await api.post(`api/ups/${seg(upsName)}/commands`, { command: command.name, parameter: parameter ?? null });
    toast(result?.ok ? t('cmd.done', { command: command.name }) : `${t('cmd.failed', { command: command.name })}: ${commandResultText(result)}`,
      { kind: result?.ok ? 'success' : 'error' });
    return result;
  };

  if (!command.dangerous && !param) {
    try {
      await execute(null);
    } catch (err) {
      toast(errorText(err), { kind: 'error' });
    }
    return;
  }

  const content = [
    h('p', {}, h('code', { text: command.name }), command.description ? ` — ${command.description}` : ''),
  ];
  if (command.dangerous) {
    content.push(callout('critical', t('cmd.dangerTitle'), h('p', { text: t('cmd.dangerText', { ups: upsName }) })));
  }
  let input = null;
  let wrapper = null;
  if (param) {
    input = param.kind === 'seconds' ? numberInput({ min: 0, step: 1, placeholder: '30', inputmode: 'numeric' }) : textInput({});
    wrapper = field({ label: t('cmd.parameterSeconds'), control: input, help: t('cmd.parameterHelp'), required: true });
    content.push(wrapper);
  }
  const ctl = openDialog({
    title: t('cmd.runTitle', { ups: upsName }),
    content,
    className: command.dangerous ? 'modal-danger' : null,
    actions: [
      { label: t('common.cancel') },
      {
        label: t('cmd.run'),
        kind: command.dangerous ? 'danger' : 'primary',
        icon: 'play',
        onClick: async (dialog) => {
          let parameter = null;
          if (input) {
            const n = numberValue(input);
            if (n === null || Number.isNaN(n) || n < 0 || !Number.isInteger(n)) {
              setFieldError(wrapper, t('validate.nonNegativeInteger'));
              input.focus();
              return false;
            }
            setFieldError(wrapper, null);
            parameter = String(n);
          }
          const result = await execute(parameter);
          if (!result?.ok) {
            dialog.showError(commandResultText(result));
            return false;
          }
          return true;
        },
      },
    ],
  });
  if (input) input.focus();
  await ctl.result;
}
