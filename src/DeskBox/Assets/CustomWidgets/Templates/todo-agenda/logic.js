function trimText(value) {
  return String(value === undefined || value === null ? '' : value).trim();
}

function parseBool(value) {
  if (value === true || value === 1) return true;
  const s = trimText(value).toLowerCase();
  return s === 'true' || s === '1' || s === 'yes';
}

function pad2(n) {
  return String(n).padStart(2, '0');
}

function toIsoDate(d) {
  return `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())}`;
}

function parseIsoParts(iso) {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(trimText(iso));
  if (!m) return null;
  const y = Number(m[1]);
  const mon = Number(m[2]);
  const day = Number(m[3]);
  const d = new Date(y, mon - 1, day);
  if (d.getFullYear() !== y || d.getMonth() !== mon - 1 || d.getDate() !== day) {
    return null;
  }
  return { y, m: mon, d: day, date: d };
}

function extractIsoDate(rec) {
  const raw = trimText((rec && (rec.dueIso || rec.dueDate)) || '');
  if (!raw) return '';
  const m = /^(\d{4})-(\d{1,2})-(\d{1,2})/.exec(raw);
  if (!m) return '';
  const cand = `${m[1]}-${pad2(Number(m[2]))}-${pad2(Number(m[3]))}`;
  return parseIsoParts(cand) ? cand : '';
}

function addDaysIso(iso, deltaDays) {
  const parts = parseIsoParts(iso);
  const base = parts ? parts.date : new Date();
  const next = new Date(base.getFullYear(), base.getMonth(), base.getDate() + deltaDays);
  return toIsoDate(next);
}

function diffDaysIso(fromIso, toIso) {
  const a = parseIsoParts(fromIso);
  const b = parseIsoParts(toIso);
  if (!a || !b) return 0;
  const utcA = Date.UTC(a.y, a.m - 1, a.d);
  const utcB = Date.UTC(b.y, b.m - 1, b.d);
  return Math.round((utcB - utcA) / 86400000);
}

function getTodayIso(ctx) {
  const env = (ctx && ctx.env) || {};
  const candidate = trimText(env.isoDate || env.date || (env.time && env.time.dateIso) || '');
  if (parseIsoParts(candidate)) return candidate;
  return toIsoDate(new Date());
}

function isPreviewOfflineMode(ctx) {
  const env = (ctx && ctx.env) || {};
  return Boolean(env.isPreview) && !(env.todo && env.todo.hasLiveStore);
}

function splitTitleAndTag(rawTitle) {
  const raw = String(rawTitle === undefined || rawTitle === null ? '' : rawTitle);
  const m = /^\[([^\[\]\r\n]{1,16})\]\s+(.+)$/.exec(raw.trim());
  if (m) {
    return { tag: trimText(m[1]), cleanTitle: trimText(m[2]), rawTitle: raw.trim() };
  }
  return { tag: '', cleanTitle: trimText(raw), rawTitle: trimText(raw) };
}

function buildStoredTitle(cleanTitle, tag) {
  const clean = trimText(cleanTitle);
  const cleanTag = trimText(tag);
  if (!clean) return '';
  return cleanTag ? `[${cleanTag}] ${clean}` : clean;
}

function normalizePriority(rec) {
  const p = trimText(rec && rec.priority).toLowerCase();
  if (p === 'high' || p === 'p1') return 'high';
  if (p === 'medium' || p === 'p2') return 'medium';
  if (parseBool(rec && rec.important)) return 'high';
  return 'normal';
}

function formatShortMmDd(iso, todayIso, ctx) {
  if (!iso) return '';
  if (iso === todayIso) return ctx.t('todayPrefix');
  if (iso === addDaysIso(todayIso, 1)) return ctx.t('tomorrowLabel');
  const parts = parseIsoParts(iso);
  if (!parts) return iso;
  return `${pad2(parts.m)}-${pad2(parts.d)}`;
}

function upcomingWeekdayIso(todayIso, targetDow, forceNextWeek) {
  const parts = parseIsoParts(todayIso);
  const base = parts ? parts.date : new Date();
  const curDow = base.getDay();
  const delta = (targetDow - curDow + 7) % 7;
  if (forceNextWeek) {
    const curMonDelta = curDow === 0 ? 6 : curDow - 1;
    const monday = new Date(base.getFullYear(), base.getMonth(), base.getDate() - curMonDelta);
    const nextMonday = new Date(monday.getFullYear(), monday.getMonth(), monday.getDate() + 7);
    const targetOffset = targetDow === 0 ? 6 : targetDow - 1;
    return toIsoDate(new Date(nextMonday.getFullYear(), nextMonday.getMonth(), nextMonday.getDate() + targetOffset));
  }
  return addDaysIso(todayIso, delta);
}

function parseDraftNlp(rawDraft, todayIso, ctx) {
  const source = String(rawDraft === undefined || rawDraft === null ? '' : rawDraft);
  if (!source.trim()) {
    return {
      raw: source,
      cleanTitle: '',
      dueIso: '',
      dateLabel: '',
      dateSpan: null,
      priority: '',
      priorityLabel: '',
      prioritySpan: null,
      tag: '',
      tagSpan: null
    };
  }

  let priority = '';
  let prioritySpan = null;
  const prioRegex = /(?:^|\s)(!p1|!1|!!|#p1|!p2|!2|#p2|!p3|!3|#p3|紧急|urgent|重要)(?=\s|$)/i;
  const pm = prioRegex.exec(source);
  if (pm) {
    const tokenOffset = pm[0].indexOf(pm[1]);
    const start = pm.index + tokenOffset;
    const end = start + pm[1].length;
    const tok = pm[1].toLowerCase();
    if (tok === '!p1' || tok === '!1' || tok === '!!' || tok === '#p1' || tok === '紧急' || tok === 'urgent') {
      priority = 'high';
    } else if (tok === '!p2' || tok === '!2' || tok === '#p2' || tok === '重要') {
      priority = 'medium';
    } else {
      priority = 'normal';
    }
    prioritySpan = { start, end };
  }

  let tag = '';
  let tagSpan = null;
  const tagScanRegex = /(?:^|\s)(#[^\s#]{1,16})(?=\s|$)/g;
  let tm;
  while ((tm = tagScanRegex.exec(source)) !== null) {
    const token = tm[1];
    const low = token.toLowerCase();
    if (low === '#p1' || low === '#p2' || low === '#p3') continue;
    const tokenOffset = tm[0].indexOf(token);
    const start = tm.index + tokenOffset;
    const end = start + token.length;
    tag = token.slice(1).trim();
    tagSpan = { start, end };
    break;
  }

  let dueIso = '';
  let dateSpan = null;

  const tryMatchDate = (regex, resolver) => {
    if (dateSpan) return;
    const m = regex.exec(source);
    if (!m) return;
    const matched = m[1] || m[0];
    const tokenOffset = m[0].indexOf(matched);
    const resolved = resolver(m);
    if (!resolved) return;
    dueIso = resolved;
    dateSpan = { start: m.index + tokenOffset, end: m.index + tokenOffset + matched.length };
  };

  tryMatchDate(/(?:^|\s)(\d{4}-\d{1,2}-\d{1,2})(?=\s|$)/, (m) => {
    const p = m[1].split('-');
    const cand = `${p[0]}-${pad2(Number(p[1]))}-${pad2(Number(p[2]))}`;
    return parseIsoParts(cand) ? cand : '';
  });

  tryMatchDate(/(?:^|\s)(大后天|大後天|后天|後天|明天|明日|今天|今日)(?=\s|$|[，。,；;])/, (m) => {
    const k = m[1];
    if (k === '今天' || k === '今日') return todayIso;
    if (k === '明天' || k === '明日') return addDaysIso(todayIso, 1);
    if (k === '后天' || k === '後天') return addDaysIso(todayIso, 2);
    if (k === '大后天' || k === '大後天') return addDaysIso(todayIso, 3);
    return '';
  });

  tryMatchDate(/(?:^|\s)(today|tdy|tomorrow|tmr)(?=\s|$)/i, (m) => {
    const k = m[1].toLowerCase();
    return (k === 'today' || k === 'tdy') ? todayIso : addDaysIso(todayIso, 1);
  });

  tryMatchDate(/(?:^|\s)((?:下|本)?(?:周|星期|礼拜)[一二三四五六日天])(?=\s|$|[，。,；;])/, (m) => {
    const tok = m[1];
    const isNext = tok.startsWith('下');
    const lastCh = tok.charAt(tok.length - 1);
    const dowMap = { '日': 0, '天': 0, '一': 1, '二': 2, '三': 3, '四': 4, '五': 5, '六': 6 };
    const dow = dowMap[lastCh];
    return typeof dow === 'number' ? upcomingWeekdayIso(todayIso, dow, isNext) : '';
  });

  tryMatchDate(/(?:^|\s)((?:next\s+)?(?:monday|mon|tuesday|tue|wednesday|wed|thursday|thu|friday|fri|saturday|sat|sunday|sun))(?=\s|$)/i, (m) => {
    const tok = m[1].toLowerCase().trim();
    const isNext = tok.startsWith('next');
    const word = tok.replace(/^next\s+/, '');
    const map = { sun: 0, sunday: 0, mon: 1, monday: 1, tue: 2, tuesday: 2, wed: 3, wednesday: 3, thu: 4, thursday: 4, fri: 5, friday: 5, sat: 6, saturday: 6 };
    const dow = map[word];
    return typeof dow === 'number' ? upcomingWeekdayIso(todayIso, dow, isNext) : '';
  });

  tryMatchDate(/(?:^|\s)(\d{1,2}月\d{1,2}[日号]?)(?=\s|$|[，。,；;])/, (m) => {
    const nums = /(\d{1,2})月(\d{1,2})/.exec(m[1]);
    if (!nums) return '';
    const tp = parseIsoParts(todayIso);
    const year = tp ? tp.y : new Date().getFullYear();
    const mon = Number(nums[1]);
    const day = Number(nums[2]);
    let cand = `${year}-${pad2(mon)}-${pad2(day)}`;
    if (!parseIsoParts(cand)) return '';
    if (diffDaysIso(todayIso, cand) < -30) {
      const nextCand = `${year + 1}-${pad2(mon)}-${pad2(day)}`;
      if (parseIsoParts(nextCand)) cand = nextCand;
    }
    return cand;
  });

  tryMatchDate(/(?:^|\s)(\d{1,2}-\d{1,2})(?=\s|$)/, (m) => {
    const nums = m[1].split('-');
    const tp = parseIsoParts(todayIso);
    const year = tp ? tp.y : new Date().getFullYear();
    const mon = Number(nums[0]);
    const day = Number(nums[1]);
    let cand = `${year}-${pad2(mon)}-${pad2(day)}`;
    if (!parseIsoParts(cand)) return '';
    if (diffDaysIso(todayIso, cand) < -30) {
      const nextCand = `${year + 1}-${pad2(mon)}-${pad2(day)}`;
      if (parseIsoParts(nextCand)) cand = nextCand;
    }
    return cand;
  });

  const spans = [prioritySpan, tagSpan, dateSpan].filter(Boolean).sort((a, b) => a.start - b.start);
  let clean = '';
  let cursor = 0;
  for (let i = 0; i < spans.length; i++) {
    const sp = spans[i];
    if (sp.start > cursor) {
      clean += source.slice(cursor, sp.start);
    }
    cursor = Math.max(cursor, sp.end);
  }
  if (cursor < source.length) {
    clean += source.slice(cursor);
  }
  clean = clean.replace(/\s{2,}/g, ' ').trim();

  let priorityLabel = '';
  if (priority === 'high') priorityLabel = ctx.t('prioTagHigh');
  else if (priority === 'medium') priorityLabel = ctx.t('prioTagMed');
  else if (priority === 'normal') priorityLabel = ctx.t('prioTagLow');

  return {
    raw: source,
    cleanTitle: clean,
    dueIso,
    dateLabel: formatShortMmDd(dueIso, todayIso, ctx),
    dateSpan,
    priority,
    priorityLabel,
    prioritySpan,
    tag,
    tagSpan
  };
}

function removeSpanFromString(text, span) {
  if (!span) return trimText(text);
  const before = text.slice(0, span.start);
  const after = text.slice(span.end);
  return `${before} ${after}`.replace(/\s{2,}/g, ' ').trim();
}

function ensurePreviewSeeds(ctx, todayIso) {
  const env = (ctx && ctx.env) || {};
  if (!env.isPreview || !ctx.collections || !ctx.state) return;
  if (parseBool(ctx.state.seeded)) return;

  const existing = ctx.collections.get('tasks') || [];
  if (existing.length > 0) {
    ctx.state.seeded = true;
    return;
  }

  const samples = [
    {
      id: 'seed-1',
      title: ctx.t('seedTask1'),
      priority: 'high',
      important: 'true',
      dueIso: todayIso,
      dueText: ctx.t('todayPrefix'),
      isOverdue: 'false',
      done: 'false'
    },
    {
      id: 'seed-2',
      title: ctx.t('seedTask2'),
      priority: 'medium',
      important: 'false',
      dueIso: todayIso,
      dueText: ctx.t('todayPrefix'),
      isOverdue: 'false',
      done: 'false'
    },
    {
      id: 'seed-3',
      title: ctx.t('seedTask3'),
      priority: 'normal',
      important: 'false',
      dueIso: todayIso,
      dueText: ctx.t('todayPrefix'),
      isOverdue: 'false',
      done: 'false'
    }
  ];

  for (let i = 0; i < samples.length; i++) {
    ctx.collections.append('tasks', samples[i]);
  }
  ctx.state.seeded = true;
}

function syncDayRolloverState(ctx, todayIso) {
  if (!ctx || !ctx.state) return;
  const lastToday = trimText(ctx.state.lastTodayIso);
  const selected = trimText(ctx.state.selectedDate);
  if (!selected || (lastToday && lastToday !== todayIso && selected === lastToday)) {
    ctx.state.selectedDate = todayIso;
    ctx.state.calendarMonthOffset = 0;
  }
  if (!lastToday || lastToday !== todayIso) {
    ctx.state.lastTodayIso = todayIso;
  }
  if (!trimText(ctx.state.calMode)) {
    ctx.state.calMode = 'week';
  }
  const f = trimText(ctx.state.filter).toLowerCase();
  if (f !== 'day' && f !== 'all' && f !== 'p1' && f !== 'overdue') {
    ctx.state.filter = 'day';
  }
  const dp = trimText(ctx.state.draftPriority).toLowerCase();
  if (dp !== 'high' && dp !== 'medium' && dp !== 'normal') {
    ctx.state.draftPriority = 'normal';
  }
}

function applyComputed(ctx) {
  if (!ctx || !ctx.computed) return;
  const c = compute(ctx);
  const keys = Object.keys(c);
  for (let i = 0; i < keys.length; i++) {
    ctx.computed[keys[i]] = c[keys[i]];
  }
}

export function onInit(ctx) {
  const todayIso = getTodayIso(ctx);
  syncDayRolloverState(ctx, todayIso);
  ensurePreviewSeeds(ctx, todayIso);
  applyComputed(ctx);
}

export function onTick(ctx) {
  const todayIso = getTodayIso(ctx);
  syncDayRolloverState(ctx, todayIso);
  applyComputed(ctx);
}

export function onCalendarDatePicked(ctx) {
  if (!ctx || !ctx.state) return;
  ctx.state.filter = 'day';
  applyComputed(ctx);
}

export function setFilter(ctx) {
  if (!ctx || !ctx.state) return;
  const next = trimText(ctx.args && (ctx.args.filter || ctx.args.value)).toLowerCase();
  if (next === 'day' || next === 'all' || next === 'p1' || next === 'overdue') {
    ctx.state.filter = next;
  }
  applyComputed(ctx);
}

export function toggleCompletedDrawer(ctx) {
  if (!ctx || !ctx.state) return;
  const cur = parseBool(ctx.state.showCompletedDrawer);
  ctx.state.showCompletedDrawer = !cur;
  applyComputed(ctx);
}

export function clearParsedToken(ctx) {
  if (!ctx || !ctx.state) return;
  const kind = trimText(ctx.args && (ctx.args.kind || ctx.args.value)).toLowerCase();
  const todayIso = getTodayIso(ctx);
  const parsed = parseDraftNlp(ctx.state.draftTitle || '', todayIso, ctx);
  if (kind === 'date' && parsed.dateSpan) {
    ctx.state.draftTitle = removeSpanFromString(parsed.raw, parsed.dateSpan);
  } else if (kind === 'priority' && parsed.prioritySpan) {
    ctx.state.draftTitle = removeSpanFromString(parsed.raw, parsed.prioritySpan);
  } else if (kind === 'tag' && parsed.tagSpan) {
    ctx.state.draftTitle = removeSpanFromString(parsed.raw, parsed.tagSpan);
  }
  applyComputed(ctx);
}

function addSingleTask(ctx, storedTitle, dueIso, priority) {
  if (isPreviewOfflineMode(ctx) && ctx.collections) {
    const id = `preview-${Date.now()}-${Math.floor(Math.random() * 10000)}`;
    ctx.collections.prepend('tasks', {
      id,
      title: storedTitle,
      priority: priority || 'normal',
      important: priority === 'high' ? 'true' : 'false',
      dueIso: dueIso || '',
      dueText: dueIso || '',
      isOverdue: dueIso && dueIso < getTodayIso(ctx) ? 'true' : 'false',
      done: 'false'
    });
    return;
  }

  ctx.dispatch([
    {
      type: 'todo.add',
      title: storedTitle,
      dueIso: dueIso || '',
      priority: priority || 'normal'
    }
  ]);
}

export function submitDraft(ctx) {
  if (!ctx || !ctx.state) return;
  const raw = String(ctx.state.draftTitle || '');
  if (!raw.trim()) return;

  const todayIso = getTodayIso(ctx);
  const selectedIso = trimText(ctx.state.selectedDate) || todayIso;
  const activeFilter = trimText(ctx.state.filter).toLowerCase() || 'day';
  const parsed = parseDraftNlp(raw, todayIso, ctx);
  const cleanTitle = parsed.cleanTitle || trimText(raw);
  if (!cleanTitle) return;

  const finalTitle = buildStoredTitle(cleanTitle, parsed.tag);
  const finalDueIso = parsed.dueIso || (activeFilter === 'day' ? selectedIso : todayIso);
  const finalPriority = parsed.priority || trimText(ctx.state.draftPriority).toLowerCase() || 'normal';

  ctx.state.draftTitle = '';
  ctx.state.draftPriority = 'normal';
  addSingleTask(ctx, finalTitle, finalDueIso, finalPriority);
  applyComputed(ctx);
}

export function toggleTaskItem(ctx) {
  const id = trimText(ctx.args && ctx.args.id);
  if (!id) return;

  if (trimText(ctx.state && ctx.state.focusTaskId) === id && parseBool(ctx.state['pomo.running'])) {
    ctx.state.focusTaskId = '';
    ctx.state.focusTaskTitle = '';
    ctx.state.currentTask = '';
    ctx.dispatch([{ type: 'pomodoro.reset' }]);
  }

  if (isPreviewOfflineMode(ctx) && ctx.collections) {
    const items = ctx.collections.get('tasks') || [];
    for (let i = 0; i < items.length; i++) {
      if (items[i].id === id) {
        const nextDone = !parseBool(items[i].done);
        ctx.collections.update('tasks', id, { done: nextDone ? 'true' : 'false' });
        break;
      }
    }
    applyComputed(ctx);
    return;
  }

  ctx.dispatch([{ type: 'todo.toggle', id }]);
  applyComputed(ctx);
}

export function removeTaskItem(ctx) {
  const id = trimText(ctx.args && ctx.args.id);
  if (!id) return;

  if (trimText(ctx.state && ctx.state.focusTaskId) === id) {
    ctx.state.focusTaskId = '';
    ctx.state.focusTaskTitle = '';
    ctx.state.currentTask = '';
  }

  if (isPreviewOfflineMode(ctx) && ctx.collections) {
    ctx.collections.remove('tasks', id);
    applyComputed(ctx);
    return;
  }

  ctx.dispatch([{ type: 'todo.remove', id }]);
  applyComputed(ctx);
}

export function deferTaskTomorrow(ctx) {
  const id = trimText(ctx.args && ctx.args.id);
  if (!id) return;
  const tomorrowIso = addDaysIso(getTodayIso(ctx), 1);

  if (isPreviewOfflineMode(ctx) && ctx.collections) {
    ctx.collections.update('tasks', id, { dueIso: tomorrowIso, isOverdue: 'false' });
    ctx.ui.toast(ctx.t('deferTomorrowToast'), 'info');
    applyComputed(ctx);
    return;
  }

  ctx.dispatch([{ type: 'todo.update', id, dueIso: tomorrowIso }]);
  ctx.ui.toast(ctx.t('deferTomorrowToast'), 'info');
  applyComputed(ctx);
}

export function rolloverOverdueTasks(ctx) {
  const todayIso = getTodayIso(ctx);
  if (isPreviewOfflineMode(ctx) && ctx.collections) {
    const tasks = ctx.collections.get('tasks') || [];
    let count = 0;
    for (let i = 0; i < tasks.length; i++) {
      const t = tasks[i];
      const due = extractIsoDate(t);
      if (!parseBool(t.done) && due && due < todayIso) {
        ctx.collections.update('tasks', t.id, { dueIso: todayIso, isOverdue: 'false' });
        count++;
      }
    }
    if (count > 0) {
      ctx.ui.toast(ctx.t('rolloverToast', { count: String(count) }), 'success');
    }
    applyComputed(ctx);
    return;
  }

  const tasks = (ctx.collections && ctx.collections.get('tasks')) || [];
  let overdueCount = 0;
  for (let i = 0; i < tasks.length; i++) {
    const due = extractIsoDate(tasks[i]);
    if (!parseBool(tasks[i].done) && due && due < todayIso) {
      overdueCount++;
    }
  }
  ctx.dispatch([{ type: 'todo.rolloverOverdue', dueIso: 'today' }]);
  if (overdueCount > 0) {
    ctx.ui.toast(ctx.t('rolloverToast', { count: String(overdueCount) }), 'success');
  }
  applyComputed(ctx);
}

export function clearCompletedTasks(ctx) {
  const activeFilter = trimText(ctx.state && ctx.state.filter).toLowerCase() || 'day';
  const selectedIso = trimText(ctx.state && ctx.state.selectedDate) || getTodayIso(ctx);

  if (isPreviewOfflineMode(ctx) && ctx.collections) {
    ctx.collections.remove('tasks', (rec) => {
      if (!parseBool(rec.done)) return false;
      if (activeFilter === 'day' && selectedIso) {
        return extractIsoDate(rec) === selectedIso;
      }
      return true;
    });
    applyComputed(ctx);
    return;
  }

  ctx.dispatch([
    {
      type: 'todo.clearCompleted',
      dueIso: activeFilter === 'day' ? selectedIso : ''
    }
  ]);
  applyComputed(ctx);
}

export function startTaskFocus(ctx) {
  if (!ctx || !ctx.state) return;
  const id = trimText(ctx.args && ctx.args.id);
  let title = trimText(ctx.args && ctx.args.title);
  if (!title && id && ctx.collections) {
    const all = ctx.collections.get('tasks') || [];
    for (let i = 0; i < all.length; i++) {
      if (all[i].id === id) {
        title = splitTitleAndTag(all[i].title).cleanTitle;
        break;
      }
    }
  }

  const cleanTitle = splitTitleAndTag(title).cleanTitle || ctx.t('focusDefaultTitle');
  const prevId = trimText(ctx.state.focusTaskId);
  const isRunning = parseBool(ctx.state['pomo.running']);

  ctx.state.focusTaskId = id;
  ctx.state.focusTaskTitle = cleanTitle;
  ctx.state.currentTask = cleanTitle;
  ctx.state['pomo.linkedTaskTitle'] = cleanTitle;

  if (isRunning && prevId === id) {
    ctx.dispatch([{ type: 'pomodoro.toggle' }]);
    applyComputed(ctx);
    return;
  }

  ctx.dispatch([
    { type: 'pomodoro.setPhase', value: 'focus' },
    { type: 'pomodoro.toggle' }
  ]);
  applyComputed(ctx);
}

export function stopTaskFocus(ctx) {
  if (!ctx || !ctx.state) return;
  ctx.state.focusTaskId = '';
  ctx.state.focusTaskTitle = '';
  ctx.state.currentTask = '';
  ctx.state['pomo.linkedTaskTitle'] = '';
  if (parseBool(ctx.state['pomo.running'])) {
    ctx.dispatch([{ type: 'pomodoro.reset' }]);
  }
  applyComputed(ctx);
}

export function completeFocusTask(ctx) {
  if (!ctx || !ctx.state) return;
  const id = trimText(ctx.state.focusTaskId);
  ctx.state.focusTaskId = '';
  ctx.state.focusTaskTitle = '';
  ctx.state.currentTask = '';
  ctx.state['pomo.linkedTaskTitle'] = '';

  const actions = [];
  if (id) {
    if (isPreviewOfflineMode(ctx) && ctx.collections) {
      ctx.collections.update('tasks', id, { done: 'true' });
    } else {
      actions.push({ type: 'todo.toggle', id });
    }
  }
  if (parseBool(ctx.state['pomo.running'])) {
    actions.push({ type: 'pomodoro.reset' });
  }
  if (actions.length > 0) {
    ctx.dispatch(actions);
  }
  applyComputed(ctx);
}

export function onCapsulePrimaryAction(ctx) {
  if (!ctx || !ctx.state) return;
  const todayIso = getTodayIso(ctx);
  const focusId = trimText(ctx.state.focusTaskId);
  const isRunning = parseBool(ctx.state['pomo.running']);

  if (isRunning || focusId) {
    ctx.dispatch([{ type: 'pomodoro.toggle' }]);
    applyComputed(ctx);
    return;
  }

  const view = compute(ctx);
  const top = view.pendingTasks && view.pendingTasks.length > 0 ? view.pendingTasks[0] : null;
  if (top && top.id) {
    if (isPreviewOfflineMode(ctx) && ctx.collections) {
      ctx.collections.update('tasks', top.id, { done: 'true' });
    } else {
      ctx.dispatch([{ type: 'todo.toggle', id: top.id }]);
    }
    applyComputed(ctx);
    return;
  }

  ctx.state.selectedDate = todayIso;
  ctx.state.calendarMonthOffset = 0;
  ctx.state.filter = 'day';
  applyComputed(ctx);
}

export function compute(ctx) {
  const env = (ctx && ctx.env) || {};
  const todayIso = getTodayIso(ctx);
  const selectedIso = trimText(ctx.state && ctx.state.selectedDate) || todayIso;
  const activeFilter = trimText(ctx.state && ctx.state.filter).toLowerCase() || 'day';
  const rawTasks = (ctx.collections && ctx.collections.get('tasks')) || [];
  const showDrawer = parseBool(ctx.state && ctx.state.showCompletedDrawer);
  const hideCompleted = parseBool(ctx.settings && ctx.settings.hideCompleted);

  const normalized = [];
  for (let i = 0; i < rawTasks.length; i++) {
    const r = rawTasks[i];
    if (!r) continue;
    const done = parseBool(r.done) || parseBool(r.completed);
    const dueIso = extractIsoDate(r);
    const priority = normalizePriority(r);
    const overdueDays = (!done && dueIso && dueIso < todayIso) ? diffDaysIso(dueIso, todayIso) : 0;
    const isOverdue = overdueDays > 0 || (!done && parseBool(r.isOverdue));
    const effectiveOverdueDays = overdueDays > 0 ? overdueDays : (isOverdue ? 1 : 0);
    const split = splitTitleAndTag(r.title || '');
    if (!split.cleanTitle && !split.rawTitle) continue;

    let dueBadge = '';
    if (isOverdue) {
      dueBadge = ctx.t('overdueDaysTpl', { n: String(effectiveOverdueDays) });
    } else if (dueIso && !(activeFilter === 'day' && dueIso === selectedIso)) {
      dueBadge = formatShortMmDd(dueIso, todayIso, ctx);
    }

    const railColor = priority === 'high' ? '#F43F5E' : (priority === 'medium' ? '#F59E0B' : '#00000000');
    const priorityCode = priority === 'high' ? 'P1' : (priority === 'medium' ? 'P2' : 'P3');

    normalized.push({
      id: trimText(r.id),
      title: split.cleanTitle || split.rawTitle,
      rawTitle: split.rawTitle,
      tagText: split.tag,
      hasTag: split.tag ? 'true' : 'false',
      dueBadge,
      hasDueBadge: dueBadge ? 'true' : 'false',
      isOverdue: isOverdue ? 'true' : 'false',
      overdueRank: isOverdue ? 1 : 0,
      prioRank: priority === 'high' ? 0 : (priority === 'medium' ? 1 : 2),
      railColor,
      priority,
      priorityCode,
      dueIso,
      done,
      doneStr: done ? 'true' : 'false',
      orderIndex: i
    });
  }

  let dayPending = 0;
  let dayCompleted = 0;
  let allPending = 0;
  let allCompleted = 0;
  let p1Pending = 0;
  let overduePending = 0;

  const pendingTasks = [];
  const completedTasks = [];

  for (let i = 0; i < normalized.length; i++) {
    const it = normalized[i];
    const matchesDay = it.dueIso === selectedIso;
    if (it.done) {
      allCompleted++;
      if (matchesDay) dayCompleted++;
    } else {
      allPending++;
      if (matchesDay) dayPending++;
      if (it.priority === 'high') p1Pending++;
      if (it.isOverdue === 'true') overduePending++;
    }

    let inScope = true;
    if (activeFilter === 'day') {
      inScope = matchesDay;
    } else if (activeFilter === 'p1') {
      inScope = it.priority === 'high';
    } else if (activeFilter === 'overdue') {
      inScope = it.isOverdue === 'true';
    }

    if (!inScope) continue;
    if (it.done) {
      completedTasks.push({
        id: it.id,
        title: it.title,
        rawTitle: it.rawTitle,
        tagText: it.tagText,
        hasTag: it.hasTag,
        dueIso: it.dueIso,
        done: 'true'
      });
    } else {
      pendingTasks.push({
        id: it.id,
        title: it.title,
        rawTitle: it.rawTitle,
        tagText: it.tagText,
        hasTag: it.hasTag,
        dueBadge: it.dueBadge,
        hasDueBadge: it.hasDueBadge,
        isOverdue: it.isOverdue,
        overdueRank: it.overdueRank,
        prioRank: it.prioRank,
        orderIndex: it.orderIndex,
        railColor: it.railColor,
        priority: it.priority,
        priorityCode: it.priorityCode,
        dueIso: it.dueIso,
        done: 'false'
      });
    }
  }

  pendingTasks.sort((a, b) => {
    if (a.overdueRank !== b.overdueRank) return b.overdueRank - a.overdueRank;
    if (a.prioRank !== b.prioRank) return a.prioRank - b.prioRank;
    if (a.dueIso !== b.dueIso) {
      if (!a.dueIso) return 1;
      if (!b.dueIso) return -1;
      return a.dueIso < b.dueIso ? -1 : 1;
    }
    return a.orderIndex - b.orderIndex;
  });

  const scopeDone = activeFilter === 'day' ? dayCompleted : allCompleted;
  const scopeTotal = scopeDone + (activeFilter === 'day' ? dayPending : allPending);
  const progressPercent = scopeTotal > 0 ? Math.round((scopeDone / scopeTotal) * 100) : (allPending === 0 && allCompleted > 0 ? 100 : 0);
  const progressSummary = `${scopeDone}/${scopeTotal}`;
  const progressSummaryText = scopeTotal > 0
    ? `${ctx.t('doneUnit')} ${scopeDone}/${scopeTotal}`
    : progressSummary;

  const rawDraft = String((ctx.state && ctx.state.draftTitle) || '');
  const nlp = parseDraftNlp(rawDraft, todayIso, ctx);
  const basePrio = trimText(ctx.state && ctx.state.draftPriority).toLowerCase() || 'normal';
  const effectiveDraftPrio = nlp.priority || basePrio;
  const draftPriorityCode = effectiveDraftPrio === 'high' ? 'P1' : (effectiveDraftPrio === 'medium' ? 'P2' : 'P3');
  const draftPriorityAccent = effectiveDraftPrio === 'high' ? 'true' : 'false';

  const hasParsedDate = nlp.dueIso ? 'true' : 'false';
  const hasParsedPriority = nlp.priority ? 'true' : 'false';
  const hasParsedTag = nlp.tag ? 'true' : 'false';
  const hasParsedTokens = (nlp.dueIso || nlp.priority || nlp.tag) ? 'true' : 'false';

  const pomoRunning = parseBool(ctx.state && ctx.state['pomo.running']);
  const focusTaskId = trimText(ctx.state && ctx.state.focusTaskId);
  const focusActive = (pomoRunning || focusTaskId !== '') ? 'true' : 'false';
  const focusTitle = trimText((ctx.state && (ctx.state.focusTaskTitle || ctx.state.currentTask)) || '') || ctx.t('focusDefaultTitle');
  const focusPlayIcon = pomoRunning ? 'pause' : 'play';

  const dayTotal = dayPending + dayCompleted;
  const isAllDoneToday = activeFilter === 'day' && dayPending === 0 && dayTotal > 0;
  const emptyHintTitle = isAllDoneToday
    ? ctx.t('emptyAllDoneTitle')
    : (activeFilter === 'day' ? ctx.t('emptyTitle') : ctx.t('emptyFilterTitle'));
  const emptyHintSub = activeFilter === 'day'
    ? (allPending > 0 ? ctx.t('emptySwitchAllSub') : ctx.t('emptySub'))
    : ctx.t('emptyFilterSub');

  const dayPillText = dayPending > 0 ? `${ctx.t('filterDay')} ${dayPending}` : ctx.t('filterDay');
  const allPillText = allPending > 0 ? `${ctx.t('filterAll')} ${allPending}` : ctx.t('filterAll');
  const p1PillText = p1Pending > 0 ? `${ctx.t('filterP1')} ${p1Pending}` : ctx.t('filterP1');
  const overduePillText = overduePending > 0 ? `${ctx.t('filterOverdue')} ${overduePending}` : ctx.t('filterOverdue');

  const topTask = pendingTasks.length > 0 ? pendingTasks[0] : null;
  let capsuleTitle = `${formatShortMmDd(selectedIso, todayIso, ctx)} · ${dayPending} ${ctx.t('pendingUnit')}`;
  let capsuleSummary = topTask
    ? topTask.title
    : (overduePending > 0
      ? `${ctx.t('filterOverdue')} ${overduePending} · ${ctx.t('tipRolloverOverdue')}`
      : (dayTotal > 0 ? ctx.t('allClearCapsule') : ctx.t('emptyTitle')));
  let capsuleGlyph = '📅';
  let capsuleBadge = overduePending > 0 ? `${ctx.t('filterOverdue')} ${overduePending}` : progressSummary;
  let capsuleWarn = overduePending > 0 ? 'true' : 'false';
  let capsuleProgress = progressPercent;
  let capsuleActionLabel = topTask ? ctx.t('capsuleQuickDone') : ctx.t('jumpTodayBtn');
  let capsuleActionIcon = topTask ? 'check' : 'quick-note';

  if (focusActive === 'true') {
    capsuleTitle = focusTitle;
    capsuleSummary = pomoRunning ? ctx.t('capsuleFocusingSub') : ctx.t('capsulePausedSub');
    capsuleGlyph = '🍅';
    capsuleBadge = ctx.t('focusBannerPrefix');
    capsuleWarn = 'false';
    capsuleActionLabel = pomoRunning ? ctx.t('capsulePauseFocus') : ctx.t('capsuleResumeFocus');
    capsuleActionIcon = pomoRunning ? 'pause' : 'play';
  }

  const chevron = showDrawer ? '\u25BE' : '\u25B8';
  const completedDrawerLabel = `${chevron} ${ctx.t('doneUnit')} (${completedTasks.length})`;
  const dayType = trimText(env.dayType).toLowerCase();
  const holidayPillText = dayType === 'holiday'
    ? ctx.t('badgeHoliday')
    : (dayType === 'workday_swap' ? ctx.t('badgeSwapWork') : '');

  return {
    todayIso,
    holidayPillText,
    dayPillText,
    allPillText,
    p1PillText,
    overduePillText,
    overdueCount: String(overduePending),
    hasOverdue: overduePending > 0 ? 'true' : 'false',
    showOverduePill: (overduePending > 0 || activeFilter === 'overdue') ? 'true' : 'false',
    progressPercent,
    progressSummary,
    progressSummaryText,
    draftPriorityCode,
    draftPriorityAccent,
    hasParsedTokens,
    hasParsedDate,
    parsedDateChip: nlp.dateLabel ? `📅 ${nlp.dateLabel}` : '',
    hasParsedPriority,
    parsedPriorityChip: nlp.priorityLabel ? `⚑ ${nlp.priorityLabel}` : '',
    hasParsedTag,
    parsedTagChip: nlp.tag ? `#${nlp.tag}` : '',
    focusActive,
    focusTitle,
    focusPlayIcon,
    pendingTasks,
    completedTasks,
    hasCompleted: (!hideCompleted && completedTasks.length > 0) ? 'true' : 'false',
    completedDrawerLabel,
    emptyHintTitle,
    emptyHintSub,
    capsuleTitle,
    capsuleSummary,
    capsuleGlyph,
    capsuleBadge,
    capsuleWarn,
    capsuleProgress,
    capsuleActionLabel,
    capsuleActionIcon
  };
}
