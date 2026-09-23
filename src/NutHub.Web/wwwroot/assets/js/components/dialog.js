// Modal dialogs on the native <dialog> element: showModal() makes the rest of the page inert (focus stays in
// the dialog), Escape raises "cancel", and focus returns to the opener on close.
import { h, uid } from '../dom.js';
import { icon } from '../icons.js';
import { t } from '../i18n.js';
import { errorText } from '../api.js';

/**
 * Opens a dialog. `actions`: [{ label, kind: "primary"|"danger"|"secondary", value, onClick(ctl) }]. An onClick
 * returning false (or a promise of false) keeps the dialog open; thrown errors are shown inside the dialog.
 * Returns a controller { dialog, body, close(value), setBusy(bool), showError(err|text), result: Promise }.
 */
export function openDialog({ title, content, actions = [], size = 'md', dismissible = true, className, describedBy } = {}) {
  const opener = document.activeElement;
  const titleId = uid('dlg-title');
  const errorBox = h('div', { class: 'form-error', role: 'alert', hidden: true });
  const body = h('div', { class: 'modal-body' }, errorBox, content);
  const footer = h('footer', { class: 'modal-foot' });
  const closeButton = dismissible
    ? h('button', { type: 'button', class: 'btn btn-icon btn-ghost', 'aria-label': t('common.close'), onClick: () => close(null) },
      icon('close'))
    : null;
  const dialog = h('dialog', {
    class: ['modal', `modal-${size}`, className],
    'aria-labelledby': titleId,
    'aria-describedby': describedBy ?? null,
  },
  h('div', { class: 'modal-panel' },
    h('header', { class: 'modal-head' }, h('h2', { id: titleId, class: 'modal-title', text: title }), closeButton),
    body,
    footer));

  let resolveResult;
  const result = new Promise((resolve) => {
    resolveResult = resolve;
  });
  let closed = false;
  let busy = false;
  const buttons = [];

  function close(value) {
    if (closed) return;
    closed = true;
    if (dialog.open) dialog.close();
    dialog.remove();
    if (opener && typeof opener.focus === 'function' && document.contains(opener)) opener.focus();
    resolveResult(value);
  }

  function setBusy(value) {
    busy = !!value;
    dialog.classList.toggle('is-busy', busy);
    for (const b of buttons) b.disabled = busy || b.dataset.disabled === '1';
    if (closeButton) closeButton.disabled = busy;
  }

  function showError(err) {
    if (!err) {
      errorBox.hidden = true;
      errorBox.textContent = '';
      return;
    }
    errorBox.replaceChildren(icon('warning'), h('span', { text: typeof err === 'string' ? err : errorText(err) }));
    errorBox.hidden = false;
  }

  const ctl = { dialog, body, footer, close, setBusy, showError, result };

  for (const action of actions) {
    const kind = action.kind ?? 'secondary';
    const button = h('button', {
      type: action.submit ? 'submit' : 'button',
      class: ['btn', kind === 'primary' && 'btn-primary', kind === 'danger' && 'btn-danger'],
      disabled: action.disabled,
    }, action.icon ? icon(action.icon) : null, h('span', { text: action.label }));
    if (action.disabled) button.dataset.disabled = '1';
    button.addEventListener('click', async () => {
      if (busy) return;
      if (!action.onClick) {
        close(action.value ?? null);
        return;
      }
      showError(null);
      setBusy(true);
      try {
        const keep = await action.onClick(ctl);
        setBusy(false);
        if (keep !== false) close(action.value ?? true);
      } catch (err) {
        setBusy(false);
        if (err?.name !== 'AbortError') showError(err);
      }
    });
    buttons.push(button);
    footer.appendChild(button);
  }
  if (!actions.length) footer.hidden = true;

  dialog.addEventListener('cancel', (event) => {
    event.preventDefault();
    if (dismissible && !busy) close(null);
  });
  // Handle Escape ourselves too: browsers limit repeated "cancel" events without user activation.
  dialog.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && !event.defaultPrevented) {
      event.preventDefault();
      if (dismissible && !busy) close(null);
    }
  });
  dialog.addEventListener('click', (event) => {
    // A click on the dialog element itself is a click on the backdrop (the panel fills the dialog box).
    if (event.target === dialog && dismissible && !busy) close(null);
  });

  document.body.appendChild(dialog);
  dialog.showModal();
  const autofocus = dialog.querySelector('[autofocus]');
  if (autofocus) autofocus.focus();
  return ctl;
}

/**
 * Asks for confirmation. Resolves true when confirmed. `danger` styles the confirm button as destructive;
 * `confirmText` requires typing a word (for irreversible actions on critical objects).
 */
export function confirmDialog({ title, message, confirmLabel = t('common.confirm'), danger = false, details, confirmText } = {}) {
  const content = [];
  const messageId = uid('dlg-msg');
  content.push(h('p', { id: messageId, class: 'modal-message', text: message }));
  if (details) content.push(typeof details === 'string' ? h('p', { class: 'muted', text: details }) : details);
  let typed = null;
  if (confirmText) {
    const inputId = uid('confirm');
    typed = h('input', { id: inputId, class: 'input', autocomplete: 'off', spellcheck: 'false' });
    content.push(h('div', { class: 'field' },
      h('label', { for: inputId, text: t('dialog.typeToConfirm', { text: confirmText }) }), typed));
  }
  const ctl = openDialog({
    title,
    content,
    size: 'sm',
    describedBy: messageId,
    className: danger ? 'modal-danger' : null,
    actions: [
      { label: t('common.cancel'), value: false },
      {
        label: confirmLabel,
        kind: danger ? 'danger' : 'primary',
        value: true,
        onClick: () => {
          if (typed && typed.value.trim() !== confirmText) {
            throw new Error(t('dialog.typeMismatch'));
          }
          return true;
        },
      },
    ],
  });
  if (typed) typed.focus();
  else ctl.footer.querySelector('.btn-primary, .btn-danger')?.focus();
  return ctl.result.then((v) => v === true);
}
