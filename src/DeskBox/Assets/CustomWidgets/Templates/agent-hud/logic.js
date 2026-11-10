// DeskBox AI Progress HUD (deskbox.agent-hud v3.0)
// Interactive multi-stage push simulator, quick task completion, and clean standby reset.

function shortNowTime(ctx) {
  const raw = String((ctx.env && ctx.env.time) || '');
  return raw.length >= 5 ? raw.slice(0, 5) : raw;
}

export function onInit(_ctx) {
  // Pure declarative cold start; external CLI/MCP pushes update state & collections directly.
}

export function simulateStep(ctx) {
  const prevStep = Number(ctx.state.demoStage || '0');
  const stage = (Number.isFinite(prevStep) ? prevStep : 0) % 4 + 1;
  const nowHm = shortNowTime(ctx);
  const agent = ctx.state.agentName && ctx.state.agentName !== 'Agent Bridge'
    ? ctx.state.agentName
    : (ctx.settings.defaultAgentName && ctx.settings.defaultAgentName !== 'Agent Bridge'
      ? ctx.settings.defaultAgentName
      : 'Claude Code');

  ctx.state.demoStage = String(stage);
  ctx.state.agentName = agent;
  ctx.state.updatedAt = nowHm;

  if (stage === 1) {
    ctx.state.level = 'info';
    ctx.state.isBusy = 'true';
    ctx.state.progress = '25';
    ctx.state.statusText = ctx.t('simStatusRun', 'Running');
    ctx.state.stepLabel = 'Step 1 / 4';
    ctx.state.taskTitle = ctx.t('simTask1', 'Scanning workspace dependency graph & AST…');
    ctx.collections.prepend('events', {
      tag: 'PLAN',
      tone: 'cyan',
      text: ctx.t('simEvt1', 'Indexed 18 modules and resolved build targets'),
      timeText: nowHm
    });
  } else if (stage === 2) {
    ctx.state.level = 'info';
    ctx.state.isBusy = 'true';
    ctx.state.progress = '60';
    ctx.state.statusText = ctx.t('simStatusBuild', 'Building');
    ctx.state.stepLabel = 'Step 2 / 4';
    ctx.state.taskTitle = ctx.t('simTask2', 'Applying patch set & compiling x64 binaries…');
    ctx.collections.prepend('events', {
      tag: 'BUILD',
      tone: 'accent',
      text: ctx.t('simEvt2', 'Incremental compile succeeded with 0 warnings'),
      timeText: nowHm
    });
  } else if (stage === 3) {
    ctx.state.level = 'info';
    ctx.state.isBusy = 'true';
    ctx.state.progress = '88';
    ctx.state.statusText = ctx.t('simStatusTest', 'Testing');
    ctx.state.stepLabel = 'Step 3 / 4';
    ctx.state.taskTitle = ctx.t('simTask3', 'Running x64 regression test suite…');
    ctx.collections.prepend('events', {
      tag: 'TEST',
      tone: 'violet',
      text: ctx.t('simEvt3', 'Passed 124 / 124 assertions (x64)'),
      timeText: nowHm
    });
  } else {
    ctx.state.level = 'ok';
    ctx.state.isBusy = 'false';
    ctx.state.progress = '100';
    ctx.state.statusText = ctx.t('simStatusDone', 'Completed');
    ctx.state.stepLabel = 'Step 4 / 4';
    ctx.state.taskTitle = ctx.t('simTask4', 'All checks green · workspace ready for review');
    ctx.collections.prepend('events', {
      tag: 'DONE',
      tone: 'success',
      text: ctx.t('simEvt4', 'Task pipeline finished cleanly'),
      timeText: nowHm
    });
  }
}

export function markComplete(ctx) {
  const nowHm = shortNowTime(ctx);
  ctx.state.isBusy = 'false';
  ctx.state.level = 'ok';
  ctx.state.progress = '100';
  ctx.state.statusText = ctx.t('simStatusDone', 'Completed');
  ctx.state.updatedAt = nowHm;
  ctx.collections.prepend('events', {
    tag: 'DONE',
    tone: 'success',
    text: ctx.state.taskTitle || ctx.t('simEvt4', 'Task marked completed'),
    timeText: nowHm
  });
}

export function resetHud(ctx) {
  ctx.state.taskTitle = '';
  ctx.state.statusText = '';
  ctx.state.stepLabel = '';
  ctx.state.level = '';
  ctx.state.progress = '0';
  ctx.state.isBusy = 'false';
  ctx.state.updatedAt = '';
  ctx.state.demoStage = '0';
  ctx.collections.clear('events');
}
