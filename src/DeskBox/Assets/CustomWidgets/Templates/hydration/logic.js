const DRINK_EMOJIS = {
  water: '💧',
  tea: '🍵',
  coffee: '☕',
  milk: '🥛',
  custom: '💧'
};

function clamp(v, lo, hi) {
  return v < lo ? lo : (v > hi ? hi : v);
}

function parseIntSafe(raw, fallback) {
  const n = Number(raw);
  return Number.isFinite(n) ? Math.round(n) : fallback;
}

function readBool(val, fallback) {
  if (typeof val === 'boolean') return val;
  const s = String(val == null ? '' : val).trim().toLowerCase();
  if (s === 'true' || s === '1') return true;
  if (s === 'false' || s === '0') return false;
  return Boolean(fallback);
}

function pad2(n) {
  return n < 10 ? '0' + n : String(n);
}

function formatLocalIsoDate(d) {
  return d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate());
}

function resolveTodayIso(ctx) {
  if (ctx.env && typeof ctx.env.isoDate === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(ctx.env.isoDate)) {
    return ctx.env.isoDate;
  }
  const nowMs = (ctx.env && Number(ctx.env.nowMs)) || Date.now();
  return formatLocalIsoDate(new Date(nowMs));
}

function shiftIsoDate(isoDate, deltaDays) {
  const parts = String(isoDate).split('-');
  if (parts.length !== 3) return isoDate;
  const d = new Date(Number(parts[0]), Number(parts[1]) - 1, Number(parts[2]), 12, 0, 0, 0);
  d.setDate(d.getDate() + deltaDays);
  return formatLocalIsoDate(d);
}

function resolveRecordDay(rec, fallbackIso) {
  if (rec && typeof rec.day === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(rec.day)) return rec.day;
  return fallbackIso;
}

function resolveRecordKind(rec) {
  const raw = rec ? String(rec.kind || '').trim().toLowerCase() : '';
  return Object.prototype.hasOwnProperty.call(DRINK_EMOJIS, raw) ? raw : 'water';
}

function resolveDrinkLabel(ctx, kind) {
  if (kind === 'tea') return ctx.t('drinkTea', 'Tea');
  if (kind === 'coffee') return ctx.t('drinkCoffee', 'Coffee');
  if (kind === 'milk') return ctx.t('drinkMilk', 'Milk');
  return ctx.t('drinkWater', 'Water');
}

function ensurePreviewDemoSeed(ctx, todayIso) {
  if (!ctx.env || !ctx.env.isPreview) return;
  if (!ctx.collections || !ctx.state) return;
  if (readBool(ctx.state.seeded, false)) return;

  const existing = ctx.collections.get('logs') || [];
  if (existing.length > 0) {
    ctx.state.seeded = true;
    return;
  }

  const historyAmounts = [1850, 2100, 2000, 1750, 2150, 2050];
  for (let i = 0; i < historyAmounts.length; i++) {
    const dayIso = shiftIsoDate(todayIso, -(6 - i));
    ctx.collections.append('logs', {
      day: dayIso,
      loggedTime: '18:00',
      amountMl: String(historyAmounts[i]),
      kind: 'water',
      note: ''
    });
  }

  const todayDemos = [
    { time: '16:05', amount: '300', kind: 'tea', note: '' },
    { time: '14:20', amount: '400', kind: 'water', note: ctx.t('demoNoonNote', 'Afternoon water') },
    { time: '11:15', amount: '200', kind: 'coffee', note: '' },
    { time: '09:05', amount: '350', kind: 'water', note: ctx.t('demoMorningNote', 'Morning water') }
  ];
  for (let j = todayDemos.length - 1; j >= 0; j--) {
    const item = todayDemos[j];
    ctx.collections.prepend('logs', {
      day: todayIso,
      loggedTime: item.time,
      amountMl: item.amount,
      kind: item.kind,
      note: item.note
    });
  }
  ctx.state.seeded = true;
}

function recomputeAll(ctx, opts) {
  const options = opts || {};
  const todayIso = resolveTodayIso(ctx);
  const nowMs = (ctx.env && Number(ctx.env.nowMs)) || Date.now();
  const goalMl = clamp(parseIntSafe(ctx.settings && ctx.settings.dailyGoalMl, 2000), 500, 6000);

  ensurePreviewDemoSeed(ctx, todayIso);

  if (ctx.state.lastDate !== todayIso) {
    ctx.state.lastDate = todayIso;
    ctx.state.dayStartedAtMs = nowMs;
    ctx.state.lastReminderAt = '';
  }

  const logs = (ctx.collections && ctx.collections.get('logs')) || [];
  const dayTotals = Object.create(null);
  const todayLogs = [];
  let todayMl = 0;
  let latestTodayMs = 0;

  for (let i = 0; i < logs.length; i++) {
    const rec = logs[i];
    const amt = Math.max(0, parseIntSafe(rec.amountMl, 0));
    const recDay = resolveRecordDay(rec, todayIso);
    if (amt <= 0) continue;

    dayTotals[recDay] = (dayTotals[recDay] || 0) + amt;

    if (recDay === todayIso) {
      const kindKey = resolveRecordKind(rec);
      const emoji = DRINK_EMOJIS[kindKey] || '💧';
      todayMl += amt;

      const recMs = Number(rec.loggedAtMs || 0);
      if (recMs > latestTodayMs) latestTodayMs = recMs;

      const baseLabel = resolveDrinkLabel(ctx, kindKey);
      const noteText = typeof rec.note === 'string' && rec.note.trim().length > 0 ? rec.note.trim() : '';
      const displayLabel = noteText ? noteText : baseLabel;

      todayLogs.push({
        id: rec.id,
        emoji,
        amountMl: String(amt),
        noteOrLabel: displayLabel,
        drinkTime: rec.loggedTime || '--:--',
        day: recDay
      });
    }
  }

  ctx.state.todayMl = todayMl;

  const todayCups = todayLogs.length;
  const rawPct = goalMl > 0 ? (todayMl * 100) / goalMl : 0;
  const pctClamped = Math.round(clamp(rawPct, 0, 100));
  const pctRounded = Math.round(rawPct);
  const goalReached = todayMl >= goalMl;
  const remainingMl = Math.max(0, goalMl - todayMl);

  let streakDays = 0;
  const startOffset = goalReached ? 0 : 1;
  for (let d = 0; d < 30; d++) {
    const checkIso = shiftIsoDate(todayIso, -(startOffset + d));
    if ((dayTotals[checkIso] || 0) >= goalMl) {
      streakDays++;
    } else {
      break;
    }
  }

  let goalDays28 = 0;
  for (let d = 0; d < 28; d++) {
    if ((dayTotals[shiftIsoDate(todayIso, -d)] || 0) >= goalMl) goalDays28++;
  }

  let sum7 = 0;
  let goalDays7 = 0;
  const trend7Days = [];
  for (let d = 6; d >= 0; d--) {
    const iso = shiftIsoDate(todayIso, -d);
    const val = dayTotals[iso] || 0;
    sum7 += val;
    if (val >= goalMl) goalDays7++;
    const mNum = Number(iso.slice(5, 7));
    const dNum = Number(iso.slice(8, 10));
    trend7Days.push({
      label: mNum + '/' + dNum,
      value: val
    });
  }
  const avg7dMl = Math.round(sum7 / 7);

  const reminderMinutes = parseIntSafe(ctx.settings && ctx.settings.reminderMinutes, 60);
  const nowDate = new Date(nowMs);
  const localHour = nowDate.getHours();
  const anchorMs = latestTodayMs > 0 ? latestTodayMs : parseIntSafe(ctx.state.dayStartedAtMs, nowMs);
  let reminderActive = false;
  if (reminderMinutes > 0 && !goalReached && localHour >= 8 && localHour <= 22 && anchorMs > 0) {
    if ((nowMs - anchorMs) / 60000 >= reminderMinutes) {
      reminderActive = true;
      if (options.allowNotify) {
        const lastNotifiedMs = parseIntSafe(ctx.state.lastReminderAt, 0);
        if (nowMs - lastNotifiedMs >= reminderMinutes * 60000) {
          ctx.state.lastReminderAt = String(nowMs);
          ctx.ui.notify(
            ctx.t('remindNotifyTitle', 'Hydration Reminder'),
            ctx.t('remindNotifyBodyFmt', '{0} ml left to reach your daily goal.', remainingMl)
          );
        }
      }
    }
  }

  ctx.computed.todayMlDisplay = String(todayMl);
  ctx.computed.todayCups = todayCups;
  ctx.computed.pctClamped = pctClamped;
  ctx.computed.percentText = pctRounded + '%';
  ctx.computed.reminderActive = reminderActive ? 'true' : 'false';

  if (goalReached) {
    ctx.computed.statusLine = streakDays > 1
      ? ctx.t('statusGoalStreakFmt', 'Goal reached · {0}-day streak', streakDays)
      : ctx.t('statusGoalDoneFmt', 'Daily goal reached · {0} cups', todayCups);
    ctx.computed.statusColor = 'emerald';
  } else if (todayCups === 0) {
    ctx.computed.statusLine = ctx.t('statusEmptyHint', 'Tap a cup below to log your first drink');
    ctx.computed.statusColor = 'secondary';
  } else {
    ctx.computed.statusLine = ctx.t('statusProgressFmt', '{0} cups · {1} ml to go', todayCups, remainingMl);
    ctx.computed.statusColor = 'secondary';
  }

  ctx.computed.todayLogs = todayLogs.slice(0, 2);
  ctx.computed.trend7Days = trend7Days;
  ctx.computed.avg7Text = ctx.t('avg7SummaryFmt', 'Avg {0} ml/d · {1}/7 days met', avg7dMl, goalDays7);
  ctx.computed.heatSummaryText = ctx.t('heatSummaryFmt', '{0}/28 days met', goalDays28);

  ctx.computed.capsuleSummary = goalReached
    ? ctx.t('capDoneFmt', 'Goal reached · {0} cups', todayCups)
    : ctx.t('capSummaryFmt', '{0} cups · {1} ml left', todayCups, remainingMl);
}

function addDrinkRecord(ctx, kindInput, amountMl, rawNote) {
  const todayIso = resolveTodayIso(ctx);
  const goalMl = clamp(parseIntSafe(ctx.settings && ctx.settings.dailyGoalMl, 2000), 500, 6000);
  const prevMl = parseIntSafe(ctx.state.todayMl, 0);
  const cleanKind = Object.prototype.hasOwnProperty.call(DRINK_EMOJIS, kindInput) ? kindInput : 'water';
  const drinkName = resolveDrinkLabel(ctx, cleanKind);
  const cleanNote = typeof rawNote === 'string' ? rawNote.trim().slice(0, 32) : '';

  const nowMs = (ctx.env && Number(ctx.env.nowMs)) || Date.now();
  const nowDate = new Date(nowMs);
  const nowTime = pad2(nowDate.getHours()) + ':' + pad2(nowDate.getMinutes());

  ctx.collections.prepend('logs', {
    day: todayIso,
    loggedAtMs: String(nowMs),
    loggedTime: nowTime,
    amountMl: String(amountMl),
    kind: cleanKind,
    note: cleanNote
  });

  recomputeAll(ctx, { allowNotify: false });

  const nextMl = parseIntSafe(ctx.state.todayMl, 0);
  const hitGoalNow = prevMl < goalMl && nextMl >= goalMl;
  if (!ctx.settings || readBool(ctx.settings.soundCue, true)) {
    ctx.dispatch({ type: 'audio.cue', value: hitGoalNow ? 'complete' : 'pop' });
  }
  if (hitGoalNow) {
    ctx.ui.toast(ctx.t('toastGoalReached', 'Daily hydration goal reached!'), 'success');
  } else {
    ctx.ui.toast(ctx.t('toastLoggedFmt', 'Logged {0} +{1} ml', drinkName, amountMl), 'info');
  }
}

export function onInit(ctx) {
  recomputeAll(ctx, { allowNotify: false });
}

export function onTick(ctx) {
  recomputeAll(ctx, { allowNotify: true });
}

export function onSettingsChange(ctx) {
  recomputeAll(ctx, { allowNotify: false });
}

export function logPresetVessel(ctx) {
  const vessel = ctx.args && ctx.args.vessel ? String(ctx.args.vessel) : 'smallWater';
  if (vessel === 'largeBottle') {
    addDrinkRecord(ctx, 'water', clamp(parseIntSafe(ctx.settings && ctx.settings.cupLargeMl, 350), 100, 1500), ctx.t('vesselBottleTitle', 'Large cup'));
  } else if (vessel === 'teaCup') {
    addDrinkRecord(ctx, 'tea', 300, '');
  } else if (vessel === 'coffeeMug') {
    addDrinkRecord(ctx, 'coffee', 200, '');
  } else {
    addDrinkRecord(ctx, 'water', clamp(parseIntSafe(ctx.settings && ctx.settings.cupSmallMl, 250), 50, 1000), '');
  }
}

export function logCustomCup(ctx) {
  const amount = clamp(parseIntSafe(ctx.state.customMl, 300), 50, 2000);
  const note = ctx.state.customNote || '';
  ctx.state.customNote = '';
  addDrinkRecord(ctx, ctx.state.drinkKind || 'water', amount, note);
}

export function undoLastToday(ctx) {
  const todayIso = resolveTodayIso(ctx);
  const logs = (ctx.collections && ctx.collections.get('logs')) || [];
  for (let i = 0; i < logs.length; i++) {
    if (resolveRecordDay(logs[i], '') === todayIso) {
      ctx.collections.remove('logs', logs[i].id);
      recomputeAll(ctx, { allowNotify: false });
      ctx.ui.toast(ctx.t('toastUndone', 'Removed last entry'), 'info');
      return;
    }
  }
}

export function deleteLog(ctx) {
  const targetId = ctx.args && ctx.args.id ? String(ctx.args.id) : '';
  if (!targetId) return;
  ctx.collections.remove('logs', targetId);
  recomputeAll(ctx, { allowNotify: false });
}
