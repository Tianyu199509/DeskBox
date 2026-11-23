// DeskBox AI Progress HUD (deskbox.agent-hud v1.0.0)
// Dynamic MCP/CLI bridge snippets, compute-time localization, and 4-stage pipeline simulator.

const STAGES = {
  1: { progress: '25', busy: 'true', level: 'info', tone: 'emerald', statusKey: 'simStatusRun', badgeKey: 'stepBadge1', stepKey: 'stepLabel1', taskKey: 'simTask1', evtKey: 'simEvt1' },
  2: { progress: '60', busy: 'true', level: 'info', tone: 'emerald', statusKey: 'simStatusBuild', badgeKey: 'stepBadge2', stepKey: 'stepLabel2', taskKey: 'simTask2', evtKey: 'simEvt2' },
  3: { progress: '88', busy: 'true', level: 'info', tone: 'emerald', statusKey: 'simStatusTest', badgeKey: 'stepBadge3', stepKey: 'stepLabel3', taskKey: 'simTask3', evtKey: 'simEvt3' },
  4: { progress: '100', busy: 'false', level: 'ok', tone: 'emerald', statusKey: 'simStatusDone', badgeKey: 'stepBadge4', stepKey: 'stepLabel4', taskKey: 'simTask4', evtKey: 'simEvt4' }
};

function shortNowTime(ctx) {
  const raw = String((ctx.env && ctx.env.time) || '');
  return raw.length >= 5 ? raw.slice(0, 5) : raw;
}

function resolveQuotedCliPath(ctx) {
  const raw = String((ctx && ctx.env && ctx.env.cliPath) || '').trim() || 'DeskBox.exe';
  const escaped = raw.replace(/"/g, '\\"');
  return `"${escaped}"`;
}

function resolveWidgetId(ctx) {
  return String((ctx && ctx.env && ctx.env.widgetId) || '').trim() || '<id>';
}

function getEventsList(ctx) {
  if (ctx && ctx.collections && typeof ctx.collections.get === 'function') {
    return ctx.collections.get('events') || [];
  }
  return [];
}

function normalizeEventRow(it, simMap) {
  const step = String((it && it.demoStep) || '').trim();
  const sim = simMap[step] || null;
  const rawTag = String((it && it.tag) || '').trim();
  const rawBadge = String((it && it.badge) || '').trim();
  const badgeText = sim
    ? sim.badge
    : (rawTag || rawBadge || 'INFO');

  const rawTone = String((it && it.tone) || '').trim();
  const badgeTone = rawTone
    || (rawBadge === 'OK' || rawBadge === 'DONE' ? 'emerald' : (rawBadge === 'ERR' ? 'danger' : 'subtle'));

  const rawBody = String((it && (it.text || it.title)) || '').trim();
  const bodyText = sim ? sim.evt : rawBody;
  const timeDisplay = String((it && (it.timeText || it.time)) || '').trim();

  const isKey = (
    badgeTone === 'emerald' ||
    badgeTone === 'violet' ||
    badgeTone === 'danger' ||
    badgeTone === 'warning' ||
    rawTag === 'DONE' ||
    rawTag === 'TEST' ||
    rawTag === 'ERR' ||
    rawBadge === 'OK' ||
    rawBadge === 'DONE' ||
    rawBadge === 'ERR'
  );

  return {
    id: String((it && it.id) || ''),
    badgeText,
    badgeTone,
    bodyText,
    timeDisplay,
    isKey: isKey ? 'true' : 'false'
  };
}

function recompute(ctx) {
  const state = ctx.state || {};
  const settings = ctx.settings || {};
  const cli = resolveQuotedCliPath(ctx);
  const id = resolveWidgetId(ctx);
  ctx.computed.widgetIdChip = `ID: ${id}`;
  ctx.computed.cliQuickCmd = ctx.t('cliQuickCmd', { cli, id });
  ctx.computed.copyPromptText = ctx.t('copyPromptText', { cli, id });

  const simBadge1 = ctx.t('stepBadge1', '01 PLAN');
  const simBadge2 = ctx.t('stepBadge2', '02 BUILD');
  const simBadge3 = ctx.t('stepBadge3', '03 TEST');
  const simBadge4 = ctx.t('stepBadge4', '04 DONE');

  const simStepLabel1 = ctx.t('stepLabel1', 'Step 1 / 4 · PLAN');
  const simStepLabel2 = ctx.t('stepLabel2', 'Step 2 / 4 · BUILD');
  const simStepLabel3 = ctx.t('stepLabel3', 'Step 3 / 4 · TEST');
  const simStepLabel4 = ctx.t('stepLabel4', 'Step 4 / 4 · DONE');

  const simStatus1 = ctx.t('simStatusRun', 'Running');
  const simStatus2 = ctx.t('simStatusBuild', 'Building');
  const simStatus3 = ctx.t('simStatusTest', 'Testing');
  const simStatus4 = ctx.t('simStatusDone', 'Completed');

  const simTask1 = ctx.t('simTask1', 'Scanning workspace dependency graph & AST…');
  const simTask2 = ctx.t('simTask2', 'Applying patch set & compiling x64 binaries…');
  const simTask3 = ctx.t('simTask3', 'Running x64 regression test suite…');
  const simTask4 = ctx.t('simTask4', 'All checks green · workspace ready for review');

  const simEvt1 = ctx.t('simEvt1', 'Indexed 18 modules and resolved build targets');
  const simEvt2 = ctx.t('simEvt2', 'Incremental compile succeeded with 0 warnings');
  const simEvt3 = ctx.t('simEvt3', 'Passed 124 / 124 assertions (x64)');
  const simEvt4 = ctx.t('simEvt4', 'Task pipeline finished cleanly');

  ctx.computed.simBadge1 = simBadge1;
  ctx.computed.simBadge2 = simBadge2;
  ctx.computed.simBadge3 = simBadge3;
  ctx.computed.simBadge4 = simBadge4;
  ctx.computed.simStepLabel1 = simStepLabel1;
  ctx.computed.simStepLabel2 = simStepLabel2;
  ctx.computed.simStepLabel3 = simStepLabel3;
  ctx.computed.simStepLabel4 = simStepLabel4;
  ctx.computed.simStatus1 = simStatus1;
  ctx.computed.simStatus2 = simStatus2;
  ctx.computed.simStatus3 = simStatus3;
  ctx.computed.simStatus4 = simStatus4;
  ctx.computed.simTask1 = simTask1;
  ctx.computed.simTask2 = simTask2;
  ctx.computed.simTask3 = simTask3;
  ctx.computed.simTask4 = simTask4;
  ctx.computed.simEvt1 = simEvt1;
  ctx.computed.simEvt2 = simEvt2;
  ctx.computed.simEvt3 = simEvt3;
  ctx.computed.simEvt4 = simEvt4;

  const simMap = {
    1: { status: simStatus1, task: simTask1, step: simStepLabel1, badge: simBadge1, evt: simEvt1 },
    2: { status: simStatus2, task: simTask2, step: simStepLabel2, badge: simBadge2, evt: simEvt2 },
    3: { status: simStatus3, task: simTask3, step: simStepLabel3, badge: simBadge3, evt: simEvt3 },
    4: { status: simStatus4, task: simTask4, step: simStepLabel4, badge: simBadge4, evt: simEvt4 }
  };

  const agentNameRaw = String(state.agentName || '').trim();
  const defaultAgent = String(settings.defaultAgentName || 'Agent Bridge').trim() || 'Agent Bridge';
  const agentTitle = (agentNameRaw && agentNameRaw !== 'Agent Bridge') ? agentNameRaw : defaultAgent;

  const demoStage = String(state.demoStage || '0').trim();
  const activeSim = simMap[demoStage] || null;

  const statusRaw = String(state.statusText || state.status || '').trim();
  const heroStatus = statusRaw || (activeSim ? activeSim.status : ctx.t('defaultStatus', 'Ready'));

  const taskRaw = String(state.taskTitle || state.currentTask || '').trim();
  const heroTask = taskRaw || (activeSim ? activeSim.task : ctx.t('defaultTask', 'Waiting for agent telemetry…'));
  const hasActiveTask = Boolean(taskRaw || demoStage !== '0');

  const stepRaw = String(state.stepLabel || '').trim();
  const stepLabel = stepRaw || (activeSim ? activeSim.step : ctx.t('bridgeReadyHint', 'Ready'));

  const level = String(state.level || '').trim().toLowerCase();
  const progress = String(state.progress || '0').trim() || '0';
  const isBusy = String(state.isBusy || 'false').trim().toLowerCase() === 'true';

  let heroTone = 'subtle';
  let statusIcon = '';
  if (level === 'error') {
    heroTone = 'danger';
    statusIcon = 'warning';
  } else if (level === 'warning') {
    heroTone = 'warning';
    statusIcon = 'warning';
  } else if (progress === '100' && !isBusy) {
    heroTone = 'emerald';
    statusIcon = 'check';
  } else if (isBusy) {
    heroTone = 'emerald';
    statusIcon = 'sparkles';
  }

  let barTone = 'emerald';
  if (level === 'error') barTone = 'danger';
  else if (level === 'warning') barTone = 'warning';

  const rawEvents = getEventsList(ctx);
  const filterMode = String(state.eventFilter || 'all').trim().toLowerCase();
  const projected = [];
  for (let i = 0; i < rawEvents.length; i++) {
    const row = normalizeEventRow(rawEvents[i], simMap);
    if (filterMode === 'key' && row.isKey !== 'true') continue;
    projected.push(row);
  }

  let capsuleBadge = '';
  if (progress !== '0') {
    capsuleBadge = `${progress}%`;
  } else if (isBusy) {
    capsuleBadge = '0%';
  } else if (rawEvents.length > 0) {
    capsuleBadge = String(rawEvents.length);
  }

  const canReset = !isBusy && (
    rawEvents.length > 0 ||
    progress !== '0' ||
    Boolean(taskRaw) ||
    demoStage !== '0'
  );

  ctx.computed.agentTitle = agentTitle;
  ctx.computed.heroStatus = heroStatus;
  ctx.computed.heroTask = heroTask;
  ctx.computed.heroTaskColor = hasActiveTask ? 'default' : 'secondary';
  ctx.computed.heroTone = heroTone;
  ctx.computed.statusIcon = statusIcon;
  ctx.computed.barTone = barTone;
  ctx.computed.stepLabel = stepLabel;
  ctx.computed.capsuleTitle = `${agentTitle} · ${heroStatus}`;
  ctx.computed.capsuleBadge = capsuleBadge;
  ctx.computed.canReset = canReset ? 'true' : 'false';
  ctx.computed.displayEvents = projected;
}

export function onInit(ctx) {
  recompute(ctx);
}

export function onSettingsChange(ctx) {
  recompute(ctx);
}

export function refreshView(ctx) {
  recompute(ctx);
}

export function removeEvent(ctx) {
  const id = String((ctx.args && ctx.args.id) || '').trim();
  if (!id) return;
  ctx.collections.remove('events', id);
  recompute(ctx);
}

export function simulateStep(ctx) {
  const prevStep = Number(ctx.state.demoStage || '0');
  const stage = (Number.isFinite(prevStep) ? prevStep : 0) % 4 + 1;
  const meta = STAGES[stage];
  const nowHm = shortNowTime(ctx);
  const agent = ctx.state.agentName && ctx.state.agentName !== 'Agent Bridge'
    ? ctx.state.agentName
    : (ctx.settings.defaultAgentName && ctx.settings.defaultAgentName !== 'Agent Bridge'
      ? ctx.settings.defaultAgentName
      : 'Claude Code');

  ctx.state.demoStage = String(stage);
  ctx.state.agentName = agent;
  ctx.state.updatedAt = nowHm;
  ctx.state.level = meta.level;
  ctx.state.isBusy = meta.busy;
  ctx.state.progress = meta.progress;
  ctx.state.statusText = '';
  ctx.state.status = '';
  ctx.state.stepLabel = '';
  ctx.state.taskTitle = '';
  ctx.state.currentTask = '';

  ctx.collections.prepend('events', {
    demoStep: String(stage),
    tone: meta.tone,
    timeText: nowHm
  });

  recompute(ctx);
}

export function markComplete(ctx) {
  const nowHm = shortNowTime(ctx);
  const customTitle = String(ctx.state.taskTitle || ctx.state.currentTask || '').trim();
  ctx.state.isBusy = 'false';
  ctx.state.level = 'ok';
  ctx.state.progress = '100';
  ctx.state.statusText = '';
  ctx.state.status = '';
  ctx.state.updatedAt = nowHm;

  if (customTitle) {
    ctx.collections.prepend('events', {
      tag: ctx.t('stepBadge4', '04 DONE'),
      tone: 'emerald',
      text: customTitle,
      timeText: nowHm
    });
  } else {
    ctx.state.demoStage = '4';
    ctx.state.stepLabel = '';
    ctx.collections.prepend('events', {
      demoStep: '4',
      tone: 'emerald',
      timeText: nowHm
    });
  }

  recompute(ctx);
}

export function resetHud(ctx) {
  ctx.state.taskTitle = '';
  ctx.state.currentTask = '';
  ctx.state.statusText = '';
  ctx.state.status = '';
  ctx.state.stepLabel = '';
  ctx.state.level = '';
  ctx.state.progress = '0';
  ctx.state.isBusy = 'false';
  ctx.state.updatedAt = '';
  ctx.state.demoStage = '0';
  ctx.collections.clear('events');
  recompute(ctx);
}
