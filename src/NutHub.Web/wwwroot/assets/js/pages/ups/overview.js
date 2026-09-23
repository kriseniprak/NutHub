// Overview tab of a UPS, laid out like NutDesk: a tall card with the big battery charge gauge, the remaining time and
// four status lights, then one gauge card for each value the UPS reports (load, voltages, frequency) with a range
// around its nominal value, and a device card. Built once; update() follows the live summary and the polled detail.
import { h, setText } from '../../dom.js';
import { t } from '../../i18n.js';
import { arcGauge, batteryLevel } from '../../components/gauge.js';
import { duration, unit, isNum, relTime, setRelTime } from '../../format.js';
import { upsFlags, isOffline } from '../../labels.js';

const BATTERY_NOMINALS = [6, 12, 24, 36, 48, 72, 96, 120, 144, 192, 240, 384, 480];

function variable(detail, name) {
  return (detail?.variables ?? []).find((v) => v.name === name)?.value ?? null;
}

function numVariable(detail, name) {
  const raw = variable(detail, name);
  const n = raw === null || raw === '' ? NaN : Number(raw);
  return Number.isFinite(n) ? n : null;
}

/** Mains range: the transfer thresholds when known, otherwise ±15 % around the nominal (guessed from the value). */
function mainsRange(value, nominal, low, high) {
  if (isNum(low) && isNum(high) && high > low) return [low, high];
  let nom = nominal;
  if (!isNum(nom) && isNum(value)) nom = value > 180 ? 230 : value > 90 ? 120 : null;
  if (!isNum(nom)) return [0, isNum(value) ? Math.ceil(value * 1.25) : 100];
  return [Math.floor(nom * 0.85), Math.ceil(nom * 1.15)];
}

/** Battery range: the device thresholds when known, otherwise -20 % / +20 % around the (guessed) nominal. */
function batteryRange(value, nominal, low, high) {
  if (isNum(low) && isNum(high) && high > low) return [low, high];
  let nom = nominal;
  if (!isNum(nom) && isNum(value)) {
    nom = BATTERY_NOMINALS.reduce((best, n) => (Math.abs(value / 1.12 - n) < Math.abs(value / 1.12 - best) ? n : best));
  }
  if (!isNum(nom)) return [0, 100];
  return [Math.floor(nom * 0.8), Math.ceil(nom * 1.2)];
}

function frequencyRange(value, nominal) {
  const nom = isNum(nominal) ? nominal : isNum(value) && value > 55 ? 60 : 50;
  return [nom - 5, nom + 5];
}

function gaugeCard(title, gauge) {
  return h('section', { class: 'card gauge-card', 'aria-label': title }, h('h2', { class: 'card-label', text: title }), gauge.el);
}

/** Builds the overview; returns { el, update(summary, detail) }. */
export function overviewTab() {
  // ---- Battery card ----
  const charge = arcGauge({ label: t('ups.batteryCharge'), unit: '%', className: 'gauge-lg' });
  const runtime = h('strong', { class: 'overview-runtime-value' });
  const runtimeLow = h('span', { class: 'overview-runtime-low' });
  const lights = [
    { flag: 'OL', key: 'ups.light.online', sev: 'ok' },
    { flag: 'OB', key: 'ups.light.onBattery', sev: 'warning' },
    { flag: 'OVER', key: 'ups.light.overload', sev: 'critical' },
    { flag: 'LB', key: 'ups.light.lowBattery', sev: 'critical' },
  ].map((light) => {
    const state = h('span', { class: 'sr-only' });
    return { ...light, state, el: h('li', { class: ['status-light', `sev-${light.sev}`] }, h('span', { class: 'status-light-dot', 'aria-hidden': 'true' }), h('span', { text: t(light.key) }), state) };
  });
  const main = h('section', { class: 'card overview-main', 'aria-label': t('ups.batteryCharge') },
    h('h2', { class: 'card-label', text: t('ups.batteryCharge') }),
    charge.el,
    h('div', { class: 'overview-runtime' },
      h('span', { class: 'card-label', text: t('ups.remaining') }), runtime, runtimeLow),
    h('ul', { class: 'status-lights' }, lights.map((l) => l.el)));

  // ---- Gauge cards (shown only for the values the UPS reports) ----
  const load = arcGauge({ label: t('metric.load'), unit: '%' });
  const input = arcGauge({ label: t('metric.inputVoltage'), unit: 'V', digits: 1 });
  const output = arcGauge({ label: t('metric.outputVoltage'), unit: 'V', digits: 1 });
  const battery = arcGauge({ label: t('metric.batteryVoltage'), unit: 'V', digits: 1 });
  const frequency = arcGauge({ label: t('metric.frequency'), unit: 'Hz', digits: 1 });
  const cards = {
    load: gaugeCard(t('metric.load'), load),
    input: gaugeCard(t('metric.inputVoltage'), input),
    output: gaugeCard(t('metric.outputVoltage'), output),
    battery: gaugeCard(t('metric.batteryVoltage'), battery),
    frequency: gaugeCard(t('metric.frequency'), frequency),
  };

  // ---- Device card ----
  const deviceList = h('dl', { class: 'device-list' });
  const updated = relTime(null);
  const device = h('section', { class: 'card device-card', 'aria-label': t('ups.device') },
    h('h2', { class: 'card-label', text: t('ups.device') }), deviceList,
    h('p', { class: 'device-updated' }, t('dashboard.updated'), ' ', updated));
  let deviceSignature = null;

  const el = h('div', { class: 'overview' }, main, Object.values(cards), device);

  function update(u, detail) {
    const off = u.enabled === false || u.driverState === 'disabled' || isOffline(u);
    el.classList.toggle('is-offline', off);
    const b = u.battery ?? {};
    charge.set(off ? null : b.charge, { level: batteryLevel(u) });
    setText(runtime, isNum(b.runtime) && !off ? duration(b.runtime) : '–');
    setText(runtimeLow, isNum(b.runtimeLow) ? t('ups.lowAt', { value: duration(b.runtimeLow) }) : '');
    const flags = off ? [] : upsFlags(u);
    for (const light of lights) {
      const on = flags.includes(light.flag);
      light.el.classList.toggle('is-on', on);
      setText(light.state, on ? t('ups.light.active') : t('ups.light.inactive'));
    }

    const watts = isNum(u.realPower) ? unit(u.realPower, 'W', 0)
      : isNum(u.power) ? unit(u.power, 'VA', 0)
        : isNum(u.load) && isNum(u.realPowerNominal) ? `≈ ${unit((u.load * u.realPowerNominal) / 100, 'W', 0)}` : '';
    cards.load.hidden = !isNum(u.load);
    load.set(u.load, { secondary: watts, level: flags.includes('OVER') || u.load >= 100 ? 'critical' : 'accent' });

    const [inLo, inHi] = mainsRange(u.inputVoltage, numVariable(detail, 'input.voltage.nominal'),
      numVariable(detail, 'input.transfer.low'), numVariable(detail, 'input.transfer.high'));
    cards.input.hidden = !isNum(u.inputVoltage);
    input.set(u.inputVoltage, { min: inLo, max: inHi });

    const [outLo, outHi] = mainsRange(u.outputVoltage, numVariable(detail, 'output.voltage.nominal')
      ?? numVariable(detail, 'input.voltage.nominal'));
    cards.output.hidden = !isNum(u.outputVoltage);
    output.set(u.outputVoltage, { min: outLo, max: outHi });

    const [batLo, batHi] = batteryRange(b.voltage, numVariable(detail, 'battery.voltage.nominal'),
      numVariable(detail, 'battery.voltage.low'), numVariable(detail, 'battery.voltage.high'));
    cards.battery.hidden = !isNum(b.voltage);
    battery.set(b.voltage, { min: batLo, max: batHi });

    const [fLo, fHi] = frequencyRange(u.inputFrequency, numVariable(detail, 'input.frequency.nominal'));
    cards.frequency.hidden = !isNum(u.inputFrequency);
    frequency.set(u.inputFrequency, { min: fLo, max: fHi });

    // The device facts change rarely: rebuild the list only when one of them does.
    const facts = [
      [t('ups.manufacturer'), u.mfr ?? variable(detail, 'ups.mfr') ?? variable(detail, 'device.mfr')],
      [t('ups.model'), u.model ?? variable(detail, 'ups.model') ?? variable(detail, 'device.model')],
      [t('ups.serialLabel'), u.serial ?? variable(detail, 'ups.serial') ?? variable(detail, 'device.serial')],
      [t('ups.firmware'), variable(detail, 'ups.firmware')],
      [t('ups.driver'), u.driverName ?? u.driver],
      [t('metric.temperature'), isNum(u.temperature) ? unit(u.temperature, '°C', 1) : null],
      [t('ups.batteryDate'), variable(detail, 'battery.date') ?? variable(detail, 'battery.mfr.date')],
    ].filter(([, value]) => value !== null && value !== undefined && value !== '');
    const signature = JSON.stringify(facts);
    if (signature !== deviceSignature) {
      deviceSignature = signature;
      deviceList.replaceChildren(...facts.map(([label, value]) => h('div', { class: 'device-row' }, h('dt', { text: label }), h('dd', { text: String(value) }))));
    }
    setRelTime(updated, u.lastUpdate);
  }

  return { el, update };
}
