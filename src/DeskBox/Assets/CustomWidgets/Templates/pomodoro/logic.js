function t(ctx, key, fallback) {
  if (ctx && typeof ctx.t === 'function') {
    const res = ctx.t(key, fallback || '');
    if (res) return String(res);
  }
  return fallback || '';
}

function clampInt(val, fallback, minVal, maxVal) {
  const n = parseInt(String(val == null ? '' : val), 10);
  if (isNaN(n)) return fallback;
  return Math.max(minVal, Math.min(maxVal, n));
}

function pad2(n) {
  return n < 10 ? '0' + n : String(n);
}

function readFlag(state, key) {
  if (!state) return false;
  const raw = state[key];
  if (raw === true || raw === 1) return true;
  const v = String(raw || '').trim().toLowerCase();
  return v === 'true' || v === '1';
}

function isTodoCompleted(item) {
  if (!item) return false;
  return item.done === true ||
    item.done === 'true' ||
    item.completed === true ||
    item.completed === 'true' ||
    item.isCompleted === true ||
    item.isCompleted === 'true';
}

function isGenericPlaceholderTask(title) {
  const s = String(title || '').trim();
  return !s || s === '开始管理第一项任务' || s === 'Start managing your first task';
}

function getTodosCollection(ctx) {
  if (ctx && ctx.collections && typeof ctx.collections.get === 'function') {
    const list = ctx.collections.get('todos');
    if (Array.isArray(list)) return list;
  }
  return [];
}

function getCleanPendingTodos(ctx) {
  const raw = getTodosCollection(ctx);
  const out = [];
  for (let i = 0; i < raw.length; i++) {
    const item = raw[i];
    if (!item || isTodoCompleted(item)) continue;
    const title = String(item.title || '').trim();
    if (isGenericPlaceholderTask(title)) continue;
    out.push({
      id: String(item.id || '').trim(),
      title,
      dueText: String(item.dueText || '').trim()
    });
  }
  return out;
}

function project(ctx) {
  if (!ctx || !ctx.computed) return;
  const state = ctx.state || {};
  const settings = ctx.settings || {};
  const env = ctx.env || {};
  const isPreview = Boolean(env.isPreview);

  let phase = String(state['pomo.phase'] || 'focus').trim();
  if (phase !== 'shortBreak' && phase !== 'longBreak') {
    phase = 'focus';
  }

  const pomoRunning = readFlag(state, 'pomo.running');
  const rawCompleted = clampInt(state['pomo.todayCompleted'], 0, 0, 100);
  const rawMinutes = clampInt(state['pomo.todayMinutes'], 0, 0, 1440);
  const rawCycle = clampInt(state['pomo.cycle'], 1, 1, 4);

  const todayCompleted = (isPreview && rawCompleted === 0) ? 2 : rawCompleted;
  const todayMinutes = (isPreview && rawMinutes === 0) ? 50 : rawMinutes;
  const cycle = (isPreview && rawCompleted === 0 && rawCycle === 1) ? 3 : rawCycle;
  const dailyGoal = clampInt(settings.dailyGoal, 6, 1, 20);

  const breakParts = String(settings.breakPreset || '5,15').split(',');
  const shortBreakMin = clampInt(breakParts[0], 5, 1, 60);
  const longBreakMin = clampInt(breakParts[1], 15, 1, 90);
  const sedIntervalMin = clampInt(settings.sedentaryIntervalMin, 50, 20, 180);
  const idleResetMin = clampInt(settings.idleAutoResetMin, 5, 0, 60);

  if (phase === 'shortBreak') {
    ctx.computed.phaseTagText = t(ctx, 'phaseShortBreak', 'Short break');
    ctx.computed.phaseTone = 'emerald';
    ctx.computed.roundHintText = t(ctx, 'breakRelaxHint', 'Rest your eyes and stretch');
    ctx.computed.timerSubtitle = t(ctx, 'shortBreakSubFmt', '{0} min break before next round')
      .replace('{0}', String(shortBreakMin));
  } else if (phase === 'longBreak') {
    ctx.computed.phaseTagText = t(ctx, 'phaseLongBreak', 'Long break');
    ctx.computed.phaseTone = 'emerald';
    ctx.computed.roundHintText = t(ctx, 'longBreakRelaxHint', 'Set complete, take a longer rest');
    ctx.computed.timerSubtitle = t(ctx, 'shortBreakSubFmt', '{0} min break before next round')
      .replace('{0}', String(longBreakMin));
  } else {
    ctx.computed.phaseTagText = pomoRunning
      ? t(ctx, 'phaseFocusing', 'Focusing')
      : t(ctx, 'phaseFocusReady', 'Ready to focus');
    ctx.computed.phaseTone = 'amber';
    ctx.computed.roundHintText = t(ctx, 'roundBadgeFmt', 'Round {0}/4')
      .replace('{0}', String(cycle));
    ctx.computed.timerSubtitle = t(ctx, 'focusSubFmt', '{0} min break after focus')
      .replace('{0}', String(shortBreakMin));
  }

  const scape = String(settings.focusSoundscape || 'off').trim().toLowerCase();
  ctx.computed.soundChipLabel = scape === 'rain'
    ? t(ctx, 'soundShortRain', 'Rain')
    : (scape === 'pink'
      ? t(ctx, 'soundShortPink', 'Pink')
      : (scape === 'brown'
        ? t(ctx, 'soundShortBrown', 'Brown')
        : t(ctx, 'soundShortOff', 'Muted')));

  if (idleResetMin > 0) {
    ctx.computed.sedRuleText = t(ctx, 'sedRuleFmt', 'Remind every {0}m · auto-reset after {1}m away')
      .replace('{0}', String(sedIntervalMin))
      .replace('{1}', String(idleResetMin));
  } else {
    ctx.computed.sedRuleText = t(ctx, 'sedRuleNoIdleFmt', 'Remind every {0}m')
      .replace('{0}', String(sedIntervalMin));
  }

  const pickableTodos = getCleanPendingTodos(ctx);
  ctx.computed.pickableTodos = pickableTodos;

  const customTask = String(state.currentTask || '').trim();
  const linkedId = String(state.linkedTodoId || '').trim();
  let matchedTodo = linkedId
    ? pickableTodos.find(item => item.id === linkedId)
    : null;
  if (!matchedTodo && !customTask && pickableTodos.length > 0) {
    matchedTodo = pickableTodos[0];
  }

  const envNextTitle = (!matchedTodo && env.todo && env.todo.nextTaskTitle && !isGenericPlaceholderTask(env.todo.nextTaskTitle))
    ? String(env.todo.nextTaskTitle).trim()
    : '';
  const resolvedTodoTitle = matchedTodo ? matchedTodo.title : envNextTitle;
  const resolvedTodoId = matchedTodo ? matchedTodo.id : '';

  const previewFallbackTask = isPreview ? t(ctx, 'demoTaskTitle', 'Review weekly project notes') : '';
  const effectiveLinkedTitle = customTask || resolvedTodoTitle;
  if (String(state['pomo.linkedTaskTitle'] || '') !== effectiveLinkedTitle) {
    state['pomo.linkedTaskTitle'] = effectiveLinkedTitle;
  }

  const displayTask = effectiveLinkedTitle || previewFallbackTask || t(ctx, 'emptyTaskPrompt', 'Select or enter a focus task');
  ctx.computed.hasCustomTask = customTask ? 'true' : 'false';
  ctx.computed.hasLinkedTodo = (!customTask && resolvedTodoId) ? 'true' : 'false';
  ctx.computed.hasActiveTask = (effectiveLinkedTitle || previewFallbackTask) ? 'true' : 'false';
  ctx.computed.effectiveTodoId = resolvedTodoId;
  ctx.computed.activeTaskLabel = displayTask;

  const goalPct = Math.min(100, Math.round((todayCompleted * 100) / Math.max(1, dailyGoal)));
  ctx.computed.dailyGoalPct = goalPct;
  ctx.computed.dailyGoalSummary = t(ctx, 'dailyGoalSummaryFmt', '{0} min today · {1}/{2} rounds')
    .replace('{0}', String(todayMinutes))
    .replace('{1}', String(todayCompleted))
    .replace('{2}', String(dailyGoal));
  ctx.computed.eyeCareHint = t(ctx, 'eyeCareHintFmt', '{0}m eye-care alert')
    .replace('{0}', String(sedIntervalMin));

  const focusMin = clampInt(settings.focusMinutes, 25, 5, 180);
  const remSec = clampInt(state['pomo.remainingSec'], focusMin * 60, 0, 86400);
  const mmss = pad2(Math.floor(remSec / 60)) + ':' + pad2(remSec % 60);
  ctx.computed.capsuleTitle = mmss + ' · ' + t(ctx, 'widgetName', 'Focus Session');
  ctx.computed.capsuleSummary = displayTask;
  ctx.computed.capsuleBadge = todayCompleted + '/' + dailyGoal + t(ctx, 'roundsSuffix', ' rounds');
}

export function onInit(ctx) {
  project(ctx);
}

export function onSourceData(ctx) {
  project(ctx);
}

export function onSettingsChange(ctx) {
  project(ctx);
}

export function refreshView(ctx) {
  project(ctx);
}

export function stepFocusDuration(ctx) {
  if (!ctx || !ctx.settings || typeof ctx.dispatch !== 'function') return;
  const opts = [15, 25, 35, 45, 60];
  const cur = clampInt(ctx.settings.focusMinutes, 25, 15, 60);
  const dir = clampInt(ctx.args && ctx.args.dir, 1, -1, 1);
  let idx = opts.indexOf(cur);
  if (idx < 0) idx = 1;
  const nextIdx = Math.max(0, Math.min(opts.length - 1, idx + (dir >= 0 ? 1 : -1)));
  ctx.dispatch([
    { type: 'settings.set', key: 'focusMinutes', value: String(opts[nextIdx]) },
    { type: 'script.run', fn: 'refreshView' }
  ]);
}

export function commitTask(ctx) {
  if (!ctx || !ctx.state) return;
  ctx.state.currentTask = String(ctx.state.currentTask || '').trim();
  if (ctx.state.currentTask) {
    ctx.state.linkedTodoId = '';
  }
  project(ctx);
}

export function clearCustomTask(ctx) {
  if (!ctx || !ctx.state) return;
  ctx.state.currentTask = '';
  ctx.state.linkedTodoId = '';
  ctx.state['pomo.linkedTaskTitle'] = '';
  project(ctx);
}

export function pickTodoTask(ctx) {
  if (!ctx || !ctx.state) return;
  const id = String((ctx.args && ctx.args.id) || '').trim();
  const title = String((ctx.args && ctx.args.title) || '').trim();
  ctx.state.linkedTodoId = id;
  ctx.state.currentTask = '';
  ctx.state['pomo.linkedTaskTitle'] = title;
  project(ctx);
}

export function completeActiveTask(ctx) {
  if (!ctx || !ctx.state) return;
  const customTask = String(ctx.state.currentTask || '').trim();
  if (customTask) {
    ctx.state.currentTask = '';
    ctx.state['pomo.linkedTaskTitle'] = '';
    ctx.ui.toast(t(ctx, 'taskCompletedToast', 'Completed focus task'), 'success');
    project(ctx);
    return;
  }

  const pickableTodos = getCleanPendingTodos(ctx);
  const linkedId = String(ctx.state.linkedTodoId || '').trim();
  let matched = linkedId
    ? pickableTodos.find(item => item.id === linkedId)
    : null;
  if (!matched && pickableTodos.length > 0) {
    matched = pickableTodos[0];
  }
  const targetId = matched ? matched.id : '';
  ctx.state.linkedTodoId = '';
  if (targetId && typeof ctx.dispatch === 'function') {
    ctx.dispatch([
      { type: 'todo.toggle', targetId },
      { type: 'script.run', fn: 'refreshView' }
    ]);
    ctx.ui.toast(t(ctx, 'taskCompletedToast', 'Completed focus task'), 'success');
  }
  project(ctx);
}
