// Dashboard cards of one UPS. Both are built once and updated in place on every live update, so values change
// without layout shifts. The plain card answers "is my power OK?" (status, charge gauge, runtime and load); the
// detailed card of the "Detailed" view adds voltages, temperature, a sparkline and the update time.
import { h, setText, flash, toggleClass } from '../dom.js';
import { t } from '../i18n.js';
import { link } from '../router.js';
import { duration, pct, unit, relTime, setRelTime, isNum } from '../format.js';
import { statusBadge, updateStatusBadge } from './badge.js';
import { arcGauge, batteryLevel } from './gauge.js';
import { sparkline } from './sparkline.js';
import { isOnBattery, isOffline } from '../labels.js';

const SEVERITIES = ['ok', 'info', 'warning', 'critical', 'offline'];

function setValue(el, text) {
  if (el.textContent !== text) {
    const changed = el.textContent !== '';
    el.textContent = text;
    if (changed) flash(el);
  }
}

function figure(label, valueEl) {
  return h('div', { class: 'figure' }, h('dt', { text: label }), h('dd', {}, valueEl));
}

/** Whether the card should be greyed out: disabled, no driver connection or stale data. */
function unavailable(u) {
  return u.enabled === false || u.driverState === 'disabled' || isOffline(u);
}

/** The one-line reason shown on a greyed card, as nodes. */
function reason(u) {
  if (u.enabled === false || u.driverState === 'disabled') return [t('ups.overlay.disabled')];
  if (u.availability === 'stale') return [t('ups.card.lastData'), ' ', relTime(u.lastUpdate)];
  return [u.driverMessage || t('ups.overlay.noConnection')];
}

function head(summary, nameLink, desc, badge) {
  return h('header', { class: 'ups-card-head' },
    h('div', { class: 'ups-card-title' }, h('h3', {}, nameLink), desc),
    badge);
}

/** The plain card of the Cards view; returns { el, update(summary) }. */
export function upsCard(summary) {
  const nameLink = h('a', { class: 'stretched-link', href: link('/ups', summary.name) });
  const desc = h('p', { class: 'ups-card-desc' });
  const badge = statusBadge(summary, { short: true });
  const gauge = arcGauge({ label: t('metric.charge'), unit: '%', className: 'ups-card-gauge' });
  const runtime = h('span', { class: 'figure-value' });
  const load = h('span', { class: 'figure-value' });
  const figures = h('dl', { class: 'figures' }, figure(t('metric.runtime'), runtime), figure(t('metric.load'), load));
  const note = h('p', { class: 'ups-card-note', hidden: true });
  const el = h('article', { class: 'card ups-card', dataset: { name: summary.name } },
    head(summary, nameLink, desc, badge), gauge.el, figures, note);

  function update(u) {
    setText(nameLink, u.name);
    setText(desc, u.description || [u.mfr, u.model].filter(Boolean).join(' ') || u.driverName || '');
    desc.title = desc.textContent;
    updateStatusBadge(badge, u);
    for (const sev of SEVERITIES) toggleClass(el, `sev-${sev}`, u.severity === sev);
    const off = unavailable(u);
    toggleClass(el, 'is-offline', off);
    gauge.set(off ? null : u.battery?.charge, { level: batteryLevel(u) });
    setValue(runtime, !off && isNum(u.battery?.runtime) ? duration(u.battery.runtime) : '–');
    setValue(load, off ? '–' : pct(u.load));
    // A greyed card says why instead of showing empty figures.
    figures.hidden = off;
    note.hidden = !off;
    if (off) note.replaceChildren(...reason(u));
  }

  update(summary);
  return { el, update };
}

function metric(label, valueEl) {
  return h('div', { class: 'metric' }, h('dt', { text: label }), h('dd', {}, valueEl));
}

/** The richer card of the Detailed view; returns { el, update(summary), setHistory(seriesMap) }. */
export function upsDetailCard(summary) {
  const nameLink = h('a', { class: 'stretched-link', href: link('/ups', summary.name) });
  const desc = h('p', { class: 'ups-card-desc' });
  const badge = statusBadge(summary);
  const gauge = arcGauge({ label: t('metric.charge'), unit: '%', range: false, className: 'ups-detail-gauge' });
  const runtime = h('span', { class: 'metric-value' });
  const load = h('span', { class: 'metric-value' });
  const voltage = h('span', { class: 'metric-value' });
  const temperature = h('span', { class: 'metric-value' });
  const tempMetric = metric(t('metric.temperature'), temperature);
  const model = h('span', { class: 'metric-value' });
  const modelMetric = metric(t('ups.model'), model);
  const spark = sparkline({ label: t('dashboard.sparkLoad') });
  const sparkLabel = h('span', { class: 'spark-label', text: t('dashboard.sparkLoad') });
  const updated = relTime(summary.lastUpdate);
  const note = h('p', { class: 'ups-card-note', hidden: true });

  const el = h('article', { class: 'card ups-card ups-card--detailed', dataset: { name: summary.name } },
    head(summary, nameLink, desc, badge),
    h('div', { class: 'ups-card-body' },
      gauge.el,
      h('dl', { class: 'metrics' },
        metric(t('metric.runtime'), runtime),
        metric(t('metric.load'), load),
        metric(t('metric.inOut'), voltage),
        tempMetric,
        modelMetric)),
    note,
    h('footer', { class: 'ups-card-foot' },
      h('div', { class: 'ups-card-spark' }, spark.el, sparkLabel),
      h('span', { class: 'ups-card-updated' }, t('dashboard.updated'), ' ', updated)));

  let last = null;
  let history = null;

  function update(u) {
    last = u;
    setText(nameLink, u.name);
    setText(desc, u.description || u.driverName || '');
    updateStatusBadge(badge, u);
    for (const sev of SEVERITIES) toggleClass(el, `sev-${sev}`, u.severity === sev);
    const off = unavailable(u);
    toggleClass(el, 'is-offline', off);
    gauge.set(off ? null : u.battery?.charge, { level: batteryLevel(u) });
    setValue(runtime, isNum(u.battery?.runtime) ? duration(u.battery.runtime) : '–');
    setValue(load, pct(u.load));
    const vin = isNum(u.inputVoltage) ? unit(u.inputVoltage, 'V', 0) : '–';
    const vout = isNum(u.outputVoltage) ? unit(u.outputVoltage, 'V', 0) : '–';
    setValue(voltage, `${vin} / ${vout}`);
    tempMetric.hidden = !isNum(u.temperature);
    setValue(temperature, unit(u.temperature, '°C', 1));
    const modelText = [u.mfr, u.model].filter(Boolean).join(' ');
    modelMetric.hidden = !modelText;
    setText(model, modelText);
    setRelTime(updated, u.lastUpdate);
    note.hidden = !off;
    if (off) note.replaceChildren(...reason(u));
    renderSpark();
  }

  function renderSpark() {
    if (!history || !last) return;
    const charge = history['battery.charge'] ?? [];
    const loadPts = history['ups.load'] ?? [];
    const values = charge.map((p) => p[1]).filter(Number.isFinite);
    const chargeMoves = values.length > 1 && Math.max(...values) - Math.min(...values) >= 1;
    const useCharge = charge.length > 1 && (isOnBattery(last) || chargeMoves || loadPts.length < 2);
    const label = useCharge ? t('dashboard.sparkCharge') : t('dashboard.sparkLoad');
    setText(sparkLabel, label);
    spark.setLabel(label);
    spark.set(useCharge ? charge : loadPts, useCharge ? { min: 0, max: 100 } : { min: 0 });
  }

  update(summary);
  return {
    el,
    update,
    setHistory(seriesMap) {
      history = seriesMap ?? {};
      renderSpark();
    },
  };
}
