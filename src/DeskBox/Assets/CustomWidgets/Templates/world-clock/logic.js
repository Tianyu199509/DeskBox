const SLOT_KEYS = ['city1', 'city2', 'city3', 'city4'];

function pad2(n) {
  const v = Math.floor(Math.abs(Number(n) || 0)) % 100;
  return v < 10 ? '0' + v : String(v);
}

function parseWorkWindow(raw) {
  const clean = String(raw || '09-18').trim();
  const dash = clean.indexOf('-');
  if (dash <= 0 || dash >= clean.length - 1) return [9, 18];
  const s = parseInt(clean.slice(0, dash), 10);
  const e = parseInt(clean.slice(dash + 1), 10);
  if (!Number.isFinite(s) || !Number.isFinite(e) || s < 0 || s > 23 || e <= s || e > 24) {
    return [9, 18];
  }
  return [s, e];
}

function polarPoint(cx, cy, r, deg) {
  const rad = ((deg - 90) * Math.PI) / 180;
  return {
    x: Number((cx + r * Math.cos(rad)).toFixed(2)),
    y: Number((cy + r * Math.sin(rad)).toFixed(2)),
  };
}

function buildModernDialMarks() {
  const marks = [];
  for (let i = 0; i < 12; i++) {
    const deg = i * 30;
    if (i % 3 === 0) {
      const p1 = polarPoint(50, 50, i === 0 ? 35.5 : 37, deg);
      const p2 = polarPoint(50, 50, 42.5, deg);
      marks.push({
        type: 'line',
        x1: p1.x,
        y1: p1.y,
        x2: p2.x,
        y2: p2.y,
        stroke: i === 0 ? 'accent' : 'secondary',
        strokeWidth: i === 0 ? 2.4 : 1.7,
        strokeCap: 'round',
        opacity: i === 0 ? 0.95 : 0.72,
      });
    } else {
      const p = polarPoint(50, 50, 39.5, deg);
      marks.push({
        type: 'circle',
        cx: p.x,
        cy: p.y,
        r: 1.25,
        fill: 'secondary',
        opacity: 0.45,
      });
    }
  }
  return marks;
}

function buildClassicDialMarks() {
  const marks = [];
  for (let i = 0; i < 60; i++) {
    const deg = i * 6;
    const isMajor = i % 15 === 0;
    const isHour = i % 5 === 0;
    const innerR = isMajor ? 36.2 : isHour ? 38.2 : 41.0;
    const p1 = polarPoint(50, 50, innerR, deg);
    const p2 = polarPoint(50, 50, 43.2, deg);
    marks.push({
      type: 'line',
      x1: p1.x,
      y1: p1.y,
      x2: p2.x,
      y2: p2.y,
      stroke: i === 0 ? 'accent' : isHour ? 'default' : 'secondary',
      strokeWidth: isMajor ? 2.0 : isHour ? 1.35 : 0.75,
      strokeCap: 'round',
      opacity: isMajor ? 0.92 : isHour ? 0.72 : 0.34,
    });
  }
  const numerals = [
    { text: '12', x: 50, y: 20.5 },
    { text: '3', x: 79.5, y: 50 },
    { text: '6', x: 50, y: 79.5 },
    { text: '9', x: 20.5, y: 50 },
  ];
  for (let i = 0; i < numerals.length; i++) {
    const item = numerals[i];
    marks.push({
      type: 'text',
      x: item.x,
      y: item.y,
      text: item.text,
      align: 'center',
      fontSize: 7.8,
      fontWeight: 'semiBold',
      fill: 'secondary',
      opacity: 0.8,
    });
  }
  return marks;
}

function buildHorizonTicks() {
  const ticks = [];
  const angles = [-22.62, -11.31, 0, 11.31, 22.62];
  for (let i = 0; i < angles.length; i++) {
    const deg = angles[i];
    const p1 = polarPoint(142, 321, 309.2, deg);
    const p2 = polarPoint(142, 321, 314.8, deg);
    ticks.push({
      type: 'line',
      x1: p1.x,
      y1: p1.y,
      x2: p2.x,
      y2: p2.y,
      stroke: 'secondary',
      strokeWidth: i === 2 ? 1.4 : 1.0,
      strokeCap: 'round',
      opacity: i === 2 ? 0.6 : 0.4,
    });
  }
  return ticks;
}

function parseIsoDayNumber(isoDate) {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(String(isoDate || '').trim());
  if (!m) return null;
  return Math.floor(Date.UTC(Number(m[1]), Number(m[2]) - 1, Number(m[3])) / 86400000);
}

function formatMonthDayFromDayNum(dayNum) {
  const d = new Date(dayNum * 86400000);
  return pad2(d.getUTCMonth() + 1) + '-' + pad2(d.getUTCDate());
}

function formatClockTime(totalMinutes, hour12) {
  const norm = ((Math.round(totalMinutes) % 1440) + 1440) % 1440;
  const h24 = Math.floor(norm / 60);
  const m = norm % 60;
  if (!hour12) {
    return pad2(h24) + ':' + pad2(m);
  }
  const suffix = h24 >= 12 ? 'PM' : 'AM';
  const h12 = h24 % 12 === 0 ? 12 : h24 % 12;
  return h12 + ':' + pad2(m) + ' ' + suffix;
}

function formatOffsetHuman(ctx, localMeta, zMeta, scrubHours) {
  const localOff = localMeta && Number.isFinite(localMeta.offsetMinutes) ? localMeta.offsetMinutes : 480;
  const zOff = zMeta && Number.isFinite(zMeta.offsetMinutes) ? zMeta.offsetMinutes : localOff;
  const diffMins = zOff - localOff;
  const diffHours = diffMins / 60;
  const absH = Math.abs(diffHours);
  const hourStr = Number.isInteger(absH) ? String(absH) : absH.toFixed(1).replace(/\.0$/, '');
  const suffix = ctx.t('diffHourSuffix');
  let signDiff = ctx.t('diffSame');
  if (diffHours > 0) signDiff = '+' + hourStr + suffix;
  else if (diffHours < 0) signDiff = '-' + hourStr + suffix;

  const scrubMins = Math.round((Number(scrubHours) || 0) * 60);
  const localHourFloat = localMeta && Number.isFinite(localMeta.hourFloat) ? localMeta.hourFloat : 8;
  const zHourFloat = zMeta && Number.isFinite(zMeta.hourFloat)
    ? zMeta.hourFloat
    : ((localHourFloat + diffHours) % 24 + 24) % 24;

  const localBaseDay = parseIsoDayNumber(localMeta && localMeta.isoDate);
  const zBaseDay = parseIsoDayNumber(zMeta && zMeta.isoDate);

  let dayWord = ctx.t('daySame');
  if (localBaseDay !== null && zBaseDay !== null) {
    const lDay = localBaseDay + Math.floor((Math.floor(localHourFloat * 60 + 1e-6) + scrubMins) / 1440);
    const zDay = zBaseDay + Math.floor((Math.floor(zHourFloat * 60 + 1e-6) + scrubMins) / 1440);
    if (zDay < lDay) dayWord = ctx.t('dayPrev');
    else if (zDay > lDay) dayWord = ctx.t('dayNext');
  } else {
    const lTotal = Math.floor(localHourFloat * 60) + scrubMins;
    const zTotal = lTotal + diffMins;
    const lDayRel = Math.floor(lTotal / 1440);
    const zDayRel = Math.floor(zTotal / 1440);
    if (zDayRel < lDayRel) dayWord = ctx.t('dayPrev');
    else if (zDayRel > lDayRel) dayWord = ctx.t('dayNext');
  }

  return dayWord + ' · ' + signDiff;
}

function buildActiveCities(ctx) {
  const settings = ctx.settings || {};
  const env = ctx.env || {};
  const clockZones = env.clockZones || {};
  const localMeta = clockZones.local || {};
  const scrub = parseFloat(String((ctx.state && ctx.state.scrubHours) || '0')) || 0;
  const cities = [];
  for (let i = 0; i < SLOT_KEYS.length; i++) {
    const slot = SLOT_KEYS[i];
    const zone = String(settings[slot] || '').trim();
    if (zone && zone.toLowerCase() !== 'none') {
      const zMeta = clockZones[zone] || {};
      cities.push({
        slot,
        zone,
        offsetHuman: formatOffsetHuman(ctx, localMeta, zMeta, scrub),
      });
    }
  }
  return cities;
}

function buildDevTimestamps(env) {
  const nowMs = Number(env.nowMs || Date.now());
  const d = new Date(nowMs);
  const utcTime = pad2(d.getUTCHours()) + ':' + pad2(d.getUTCMinutes()) + ':' + pad2(d.getUTCSeconds());
  const unixSec = String(Math.floor(nowMs / 1000));
  const unixMs = String(Math.floor(nowMs));
  const iso = d.toISOString().slice(0, 19) + 'Z';
  return [
    { kind: 'utcSeconds', label: 'UTC', value: utcTime },
    { kind: 'unixSeconds', label: 'Unix s', value: unixSec },
    { kind: 'unixMs', label: 'Unix ms', value: unixMs },
    { kind: 'iso', label: 'ISO 8601', value: iso },
  ];
}

function resolveZoneId(ctx, slot) {
  const settings = ctx.settings || {};
  if (slot === 'city1' || slot === 'city2' || slot === 'city3' || slot === 'city4') {
    const raw = String(settings[slot] || '').trim();
    if (raw && raw.toLowerCase() !== 'none') {
      return raw;
    }
  }
  return 'local';
}

function syncFocusState(ctx) {
  const rawSlot = String((ctx.state && ctx.state.focusSlot) || 'local').trim();
  const settings = ctx.settings || {};
  let validSlot = 'local';
  if (SLOT_KEYS.includes(rawSlot)) {
    const val = String(settings[rawSlot] || '').trim();
    if (val && val.toLowerCase() !== 'none') {
      validSlot = rawSlot;
    }
  }
  ctx.state.focusSlot = validSlot;
  ctx.state.focusZone = resolveZoneId(ctx, validSlot);
}

function recomputeOverlap(ctx) {
  const settings = ctx.settings || {};
  const env = ctx.env || {};
  const clockZones = env.clockZones || {};
  const [workStart, workEnd] = parseWorkWindow(settings.workWindow);

  const localOffset = clockZones.local && Number.isFinite(clockZones.local.offsetMinutes)
    ? clockZones.local.offsetMinutes
    : 0;
  const localTzLower = String(env.timeZone || '').trim().toLowerCase();

  const cityZones = [];
  for (let i = 0; i < SLOT_KEYS.length; i++) {
    const z = String(settings[SLOT_KEYS[i]] || '').trim();
    if (!z || z.toLowerCase() === 'none' || z.toLowerCase() === 'local') continue;
    if (localTzLower && z.toLowerCase() === localTzLower) continue;
    if (!cityZones.includes(z)) {
      cityZones.push(z);
    }
  }
  const hasLocalEquivalent = cityZones.some((z) => {
    const meta = clockZones[z];
    return meta && Number.isFinite(meta.offsetMinutes) && meta.offsetMinutes === localOffset;
  });
  const activeZones = hasLocalEquivalent ? cityZones : ['local', ...cityZones];

  const total = Math.max(1, activeZones.length);
  const hourStats = [];
  for (let lh = 0; lh < 24; lh++) {
    let score = 0;
    let workCount = 0;
    let awakeCount = 0;
    let sleepCount = 0;

    for (let zIdx = 0; zIdx < activeZones.length; zIdx++) {
      const zId = activeZones[zIdx];
      const zMeta = clockZones[zId];
      const zOff = zMeta && Number.isFinite(zMeta.offsetMinutes) ? zMeta.offsetMinutes : localOffset;
      const totalMins = (((lh * 60 + (zOff - localOffset)) % 1440) + 1440) % 1440;
      const zHour = totalMins / 60;

      if (zHour >= workStart && zHour < workEnd) {
        score += 1.0;
        workCount++;
        awakeCount++;
      } else if ((zHour >= 7 && zHour < workStart) || (zHour >= workEnd && zHour < 22)) {
        score += 0.4;
        awakeCount++;
      } else {
        sleepCount++;
      }
    }

    hourStats.push({ lh, score, workCount, awakeCount, sleepCount });
  }

  let bestStart = 9;
  let bestSpan = 2;
  let bestMetric = -1;
  let bestWorkCount = 0;
  let bestAwakeCount = 0;

  for (let lh = 0; lh < 24; lh++) {
    const s = hourStats[lh];
    const next = hourStats[(lh + 1) % 24];
    const pairScore = (s.score + next.score) + (s.workCount + next.workCount) * 0.25 + (s.sleepCount === 0 ? 0.5 : 0);
    if (pairScore > bestMetric) {
      bestMetric = pairScore;
      bestStart = lh;
      bestWorkCount = s.workCount;
      bestAwakeCount = s.awakeCount;
    }
  }

  const startStat = hourStats[bestStart];
  if (hourStats[(bestStart + 2) % 24].score >= startStat.score - 0.15) {
    bestSpan = 3;
  }

  const bars = [];
  for (let lh = 0; lh < 24; lh++) {
    const st = hourStats[lh];
    const ratio = Math.min(1, st.score / total);
    const barH = Math.max(4, Math.round(4 + ratio * 18));
    let fill = '#64748B';
    let opacity = 0.45;
    if (st.sleepCount === 0 && st.workCount >= Math.ceil(total * 0.5)) {
      fill = '#10B981';
      opacity = 0.92;
    } else if (st.workCount >= Math.ceil(total * 0.5) || st.score >= total * 0.55) {
      fill = '#0EA5E9';
      opacity = 0.88;
    } else if (st.awakeCount >= Math.ceil(total * 0.6)) {
      fill = '#F59E0B';
      opacity = 0.78;
    }

    bars.push({
      type: 'rect',
      x: Number((5 + lh * 11.25).toFixed(2)),
      y: Number((26 - barH).toFixed(2)),
      width: 8.4,
      height: barH,
      rx: 2,
      fill,
      opacity,
    });
  }

  const endHour = (bestStart + bestSpan) % 24;
  ctx.computed.overlapBars = bars;
  ctx.computed.bestOverlapStartHour = bestStart;
  ctx.computed.bestOverlapRange = pad2(bestStart) + ':00–' + pad2(endHour) + ':00';
  ctx.computed.bestOverlapSummary =
    Math.max(bestWorkCount, bestAwakeCount) + '/' + total + ' ' + ctx.t('overlapUnit');

  const focusZone = String((ctx.state && ctx.state.focusZone) || 'local');
  const focusMeta = clockZones[focusZone] || clockZones.local || {};
  const focusOffMins = Number.isFinite(focusMeta.offsetMinutes) ? focusMeta.offsetMinutes : localOffset;
  const focusOffH = focusOffMins / 60;
  const focusOffStr = Number.isInteger(focusOffH) ? String(Math.abs(focusOffH)) : Math.abs(focusOffH).toFixed(1).replace(/\.0$/, '');
  ctx.computed.shortUtcBadge = 'UTC' + (focusOffH >= 0 ? '+' : '-') + focusOffStr;
}

function refreshAll(ctx) {
  syncFocusState(ctx);
  const settings = ctx.settings || {};
  ctx.computed.modernDialMarks = buildModernDialMarks();
  ctx.computed.classicDialMarks = buildClassicDialMarks();
  ctx.computed.dialMarks = String(settings.dialStyle || '').toLowerCase() === 'classic'
    ? ctx.computed.classicDialMarks
    : ctx.computed.modernDialMarks;
  ctx.computed.horizonTicks = buildHorizonTicks();
  const activeCities = buildActiveCities(ctx);
  ctx.computed.cities = activeCities;
  ctx.computed.meetingRows = activeCities;
  ctx.computed.devTimestamps = buildDevTimestamps(ctx.env || {});
  recomputeOverlap(ctx);
}

export function onInit(ctx) {
  refreshAll(ctx);
}

export function refreshScrub(ctx) {
  refreshAll(ctx);
}

export function toggleFocus(ctx) {
  const slot = String((ctx.args && ctx.args.slot) || '').trim();
  if (!SLOT_KEYS.includes(slot)) {
    return;
  }
  const current = String((ctx.state && ctx.state.focusSlot) || 'local').trim();
  if (current === slot) {
    ctx.state.focusSlot = 'local';
    ctx.state.focusZone = 'local';
  } else {
    const resolved = resolveZoneId(ctx, slot);
    ctx.state.focusSlot = resolved === 'local' ? 'local' : slot;
    ctx.state.focusZone = resolved;
  }
  refreshAll(ctx);
}

export function focusLocal(ctx) {
  ctx.state.focusSlot = 'local';
  ctx.state.focusZone = 'local';
  refreshAll(ctx);
}

export function jumpBestOverlap(ctx) {
  recomputeOverlap(ctx);
  const env = ctx.env || {};
  const bestStart = Number(ctx.computed.bestOverlapStartHour || 0);
  let curHourFloat = 0;
  if (env.clockZones && env.clockZones.local && Number.isFinite(env.clockZones.local.hourFloat)) {
    curHourFloat = env.clockZones.local.hourFloat;
  } else if (env.time) {
    const parts = String(env.time).split(':');
    const hh = parseInt(parts[0], 10) || 0;
    const mm = parseInt(parts[1], 10) || 0;
    curHourFloat = hh + mm / 60;
  }
  const diffHours = ((bestStart - curHourFloat) % 24 + 24) % 24;
  const delta = (Math.round(diffHours * 2) / 2) % 24;
  ctx.state.scrubHours = String(delta);
  refreshAll(ctx);
}

export function copyMeetingTimes(ctx) {
  const args = ctx.args || {};
  const settings = ctx.settings || {};
  const env = ctx.env || {};
  const clockZones = env.clockZones || {};
  const rawScrub = parseFloat(String((ctx.state && ctx.state.scrubHours) || '0'));
  const scrub = Number.isFinite(rawScrub)
    ? Math.max(0, Math.min(23.5, Math.round(rawScrub * 2) / 2))
    : 0;
  const scrubText = Number.isInteger(scrub) ? String(scrub) : scrub.toFixed(1).replace(/\.0$/, '');
  const offsetTag = scrub > 0 ? ' (+' + scrubText + 'h)' : '';

  const parts = [];
  const hour12 = String(settings.hourFormat || '').toLowerCase() === '12h';
  const scrubMins = Math.round(scrub * 60);
  const hasNowMs = Number.isFinite(env.nowMs) && env.nowMs > 0;
  const scrubUtcMs = hasNowMs ? Number(env.nowMs) + scrub * 3600000 : null;

  const localMeta = clockZones.local || {};
  const localHourFloat = Number.isFinite(localMeta.hourFloat) ? localMeta.hourFloat : 0;
  const localDstShift = (Number.isFinite(localMeta.scrubbedOffsetMinutes) && Number.isFinite(localMeta.offsetMinutes))
    ? (localMeta.scrubbedOffsetMinutes - localMeta.offsetMinutes)
    : 0;
  const localTotalMins = Math.floor(localHourFloat * 60 + 1e-6) + scrubMins + localDstShift;
  const localBaseDay = parseIsoDayNumber(localMeta.isoDate || env.isoDate);
  let localScrubDay = null;
  if (scrubUtcMs !== null && Number.isFinite(localMeta.scrubbedOffsetMinutes)) {
    localScrubDay = Math.floor((scrubUtcMs + localMeta.scrubbedOffsetMinutes * 60000) / 86400000);
  } else if (localBaseDay !== null) {
    localScrubDay = localBaseDay + Math.floor(localTotalMins / 1440);
  }
  const localDatePrefix = localScrubDay !== null ? formatMonthDayFromDayNum(localScrubDay) + ' ' : '';
  const localFormatted = hour12
    ? (localMeta.scrubbedFormatted12h || formatClockTime(localTotalMins, true))
    : (localMeta.scrubbedFormatted24h || formatClockTime(localTotalMins, false));
  const localLabel = args.localLabel || ctx.t('localNow');
  parts.push(localLabel + ' ' + localDatePrefix + localFormatted);

  const labelKeys = ['c1Label', 'c2Label', 'c3Label', 'c4Label'];
  for (let i = 0; i < SLOT_KEYS.length; i++) {
    const slot = SLOT_KEYS[i];
    const zone = String(settings[slot] || '').trim();
    if (!zone || zone.toLowerCase() === 'none') continue;
    const zMeta = clockZones[zone];
    if (!zMeta || (!Number.isFinite(zMeta.hourFloat) && !zMeta.scrubbedFormatted24h && !zMeta.scrubbedFormatted12h)) continue;
    const zHourFloat = Number.isFinite(zMeta.hourFloat) ? zMeta.hourFloat : 0;
    const zDstShift = (Number.isFinite(zMeta.scrubbedOffsetMinutes) && Number.isFinite(zMeta.offsetMinutes))
      ? (zMeta.scrubbedOffsetMinutes - zMeta.offsetMinutes)
      : 0;
    const zTotalMins = Math.floor(zHourFloat * 60 + 1e-6) + scrubMins + zDstShift;
    const zBaseDay = parseIsoDayNumber(zMeta.isoDate);
    let zScrubDay = null;
    if (scrubUtcMs !== null && Number.isFinite(zMeta.scrubbedOffsetMinutes)) {
      zScrubDay = Math.floor((scrubUtcMs + zMeta.scrubbedOffsetMinutes * 60000) / 86400000);
    } else if (zBaseDay !== null) {
      zScrubDay = zBaseDay + Math.floor(zTotalMins / 1440);
    }
    let dayTag = '';
    if (zScrubDay !== null && localScrubDay !== null) {
      const diffDays = zScrubDay - localScrubDay;
      if (diffDays > 0) dayTag = ' (+' + diffDays + 'd)';
      else if (diffDays < 0) dayTag = ' (' + diffDays + 'd)';
    }
    const zFormatted = hour12
      ? (zMeta.scrubbedFormatted12h || formatClockTime(zTotalMins, true))
      : (zMeta.scrubbedFormatted24h || formatClockTime(zTotalMins, false));
    const cityLabel = args[labelKeys[i]] || zone.split('/').pop().replace(/_/g, ' ');
    parts.push(cityLabel + ' ' + zFormatted + dayTag);
  }

  const summary = ctx.t('meetingAt') + offsetTag + ': ' + parts.join(' · ');
  ctx.ui.copy(summary);
  ctx.ui.toast(ctx.t('copiedToast'), 'success');
}
