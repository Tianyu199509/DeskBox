const SCENES = {
  off: { hex: '#0EA5E9', cardTone: 'subtle', emoji: '🎧' },
  rain: { hex: '#0EA5E9', cardTone: 'day', emoji: '🌧️' },
  pink: { hex: '#10B981', cardTone: 'emerald', emoji: '🌿' },
  brown: { hex: '#0284C7', cardTone: 'day', emoji: '🌊' },
  night: { hex: '#8B5CF6', cardTone: 'night', emoji: '🌙' }
};

const SNAP_TARGETS = [15, 25, 30, 45, 60, 75, 90];
const SCALE_MIN = 0;
const MIN_MINS = 5;
const MAX_MINS = 90;
const START_ANGLE = 135;
const SWEEP_SPAN = 270;
const CX = 104;
const CY = 72;
const RING_R = 55;

function clampInt(val, fallback, minVal, maxVal) {
  const n = Number(val);
  if (!Number.isFinite(n)) return fallback;
  return Math.max(minVal, Math.min(maxVal, Math.round(n)));
}

function readBool(val, fallback) {
  if (typeof val === 'boolean') return val;
  if (val === 1 || val === '1') return true;
  if (val === 0 || val === '0') return false;
  const s = String(val == null ? '' : val).trim().toLowerCase();
  if (s === 'true') return true;
  if (s === 'false') return false;
  return Boolean(fallback);
}

function pad2(n) {
  const v = Math.max(0, Math.floor(Number(n) || 0));
  return v < 10 ? '0' + v : String(v);
}

function formatMMSS(totalSec) {
  const s = Math.max(0, Math.round(Number(totalSec) || 0));
  return pad2(Math.floor(s / 60)) + ':' + pad2(s % 60);
}

function polarPt(cx, cy, r, deg) {
  const rad = (deg * Math.PI) / 180.0;
  return {
    x: Number((cx + r * Math.cos(rad)).toFixed(2)),
    y: Number((cy + r * Math.sin(rad)).toFixed(2))
  };
}

function normalizeSoundKey(raw) {
  const s = String(raw || 'off').trim().toLowerCase();
  return Object.prototype.hasOwnProperty.call(SCENES, s) ? s : 'off';
}

function resolveSceneLabel(ctx, sceneKey) {
  if (sceneKey === 'rain') return ctx.t('sceneRain', 'Rain');
  if (sceneKey === 'pink') return ctx.t('sceneForest', 'Breeze');
  if (sceneKey === 'brown') return ctx.t('sceneOcean', 'Waves');
  if (sceneKey === 'night') return ctx.t('sceneNight', 'Night Rain');
  return ctx.t('noiseMutedShort', 'Muted');
}

function todayKey(nowMs) {
  const d = new Date(nowMs);
  return d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate());
}

function formatFinishHHMM(nowMs, addSec) {
  const d = new Date(nowMs + Math.max(0, addSec) * 1000);
  return pad2(d.getHours()) + ':' + pad2(d.getMinutes());
}

function syncTodayBucket(s, nowMs) {
  const key = todayKey(nowMs);
  if (s.todayDateKey !== key) {
    s.todayDateKey = key;
    s.todayMinutes = 0;
    s.todaySessions = 0;
  } else {
    s.todayMinutes = clampInt(s.todayMinutes, 0, 0, 1440);
    s.todaySessions = clampInt(s.todaySessions, 0, 0, 200);
  }
}

function buildDialShapes(s, hexColor) {
  const durMin = clampInt(s.durationMin, 30, MIN_MINS, MAX_MINS);
  const totalSec = durMin * 60;
  const remSec = clampInt(s.remainingSec, totalSec, 0, MAX_MINS * 60);
  const isRunning = readBool(s.isRunning, false);
  const hasStarted = readBool(s.hasStarted, false);
  const wavePhase = Number.isFinite(Number(s.wavePhase)) ? Number(s.wavePhase) : 0;

  const targetRatio = (isRunning || hasStarted)
    ? ((remSec / 60.0) - SCALE_MIN) / (MAX_MINS - SCALE_MIN)
    : (durMin - SCALE_MIN) / (MAX_MINS - SCALE_MIN);
  const clampedRatio = Math.max(0.03, Math.min(1.0, targetRatio));
  const activeSweep = Number((SWEEP_SPAN * clampedRatio).toFixed(1));
  const activeAngle = START_ANGLE + activeSweep;
  const thumb = polarPt(CX, CY, RING_R, activeAngle);
  const haloOpacity = isRunning
    ? Number((0.20 + Math.sin(wavePhase * 0.6) * 0.06).toFixed(2))
    : 0.10;

  return [
    {
      type: 'circle',
      cx: CX,
      cy: CY,
      r: RING_R + 7,
      fill: 'transparent',
      stroke: hexColor,
      strokeWidth: 1,
      opacity: haloOpacity
    },
    {
      type: 'arc',
      cx: CX,
      cy: CY,
      r: RING_R,
      startAngle: START_ANGLE,
      sweepAngle: SWEEP_SPAN,
      stroke: '#8A94A6',
      strokeWidth: 4.5,
      strokeCap: 'round',
      opacity: 0.18
    },
    {
      type: 'arc',
      cx: CX,
      cy: CY,
      r: RING_R,
      startAngle: START_ANGLE,
      sweepAngle: activeSweep,
      stroke: hexColor,
      strokeWidth: 4.5,
      strokeCap: 'round',
      opacity: 0.94
    },
    {
      type: 'circle',
      cx: thumb.x,
      cy: thumb.y,
      r: 4.8,
      fill: hexColor,
      opacity: 1.0
    }
  ];
}

function project(ctx, overrideNowMs) {
  const s = ctx.state || (ctx.state = {});
  const settings = ctx.settings || {};
  const env = ctx.env || {};
  const isPreview = Boolean(env.isPreview);
  const nowMs = Number.isFinite(overrideNowMs) ? overrideNowMs : ((env && Number(env.nowMs)) || Date.now());

  const durMin = clampInt(s.durationMin, 30, MIN_MINS, MAX_MINS);
  s.durationMin = durMin;
  const vol = clampInt(s.level, 70, 0, 100);
  s.level = vol;
  const mode = normalizeSoundKey(s.noiseMode);
  s.noiseMode = mode;
  let lastScape = normalizeSoundKey(s.lastSoundscape);
  if (lastScape === 'off') lastScape = 'rain';
  s.lastSoundscape = lastScape;

  const isRunning = readBool(s.isRunning, false);
  const hasStarted = readBool(s.hasStarted, false);
  const remSec = clampInt(s.remainingSec, durMin * 60, 0, MAX_MINS * 60);
  s.remainingSec = remSec;
  syncTodayBucket(s, nowMs);

  const usePreviewDemo = isPreview && !readBool(s.previewTouch, false) && mode === 'off';
  const activeScene = usePreviewDemo ? 'rain' : mode;
  const visualSceneKey = activeScene !== 'off' ? activeScene : lastScape;
  const visualMeta = SCENES[visualSceneKey] || SCENES.rain;

  const rawTodayMin = clampInt(s.todayMinutes, 0, 0, 1440);
  const rawTodayRuns = clampInt(s.todaySessions, 0, 0, 200);
  const displayTodayMin = (usePreviewDemo && rawTodayMin === 0) ? 45 : rawTodayMin;
  const displayTodayRuns = (usePreviewDemo && rawTodayRuns === 0) ? 2 : rawTodayRuns;

  ctx.computed.activeScene = activeScene;
  ctx.computed.heroCardTone = activeScene !== 'off' ? visualMeta.cardTone : 'subtle';

  const activeLabel = resolveSceneLabel(ctx, activeScene);
  const lastLabel = resolveSceneLabel(ctx, lastScape);
  ctx.computed.centerSceneLabel = activeScene !== 'off'
    ? (visualMeta.emoji + ' ' + activeLabel)
    : ctx.t('noiseMuted', 'Silent Focus');

  if (displayTodayMin > 0) {
    ctx.computed.headerStatusText = ctx.t('todaySummaryFmt', '{0}m focused today', displayTodayMin);
  } else if (activeScene !== 'off') {
    ctx.computed.headerStatusText = activeLabel + ' · ' + vol + '%';
  } else {
    ctx.computed.headerStatusText = ctx.t('headerReadyHint', 'Pick an ambient sound to focus');
  }

  const displayStyle = String(settings.displayStyle || 'mmss').trim().toLowerCase();
  if (displayStyle === 'calm') {
    const minsLeft = Math.max(1, Math.ceil((isRunning || hasStarted ? remSec : durMin * 60) / 60));
    ctx.computed.timeDisplay = minsLeft + ' ' + ctx.t('unitMinWord', 'min');
  } else {
    ctx.computed.timeDisplay = formatMMSS(isRunning || hasStarted ? remSec : durMin * 60);
  }

  if (isRunning) {
    ctx.computed.subStatusText = ctx.t('finishAtFmt', 'Ends at {0}', formatFinishHHMM(nowMs, remSec));
    ctx.computed.primaryActionIcon = 'pause';
    ctx.computed.primaryActionLabel = ctx.t('btnPause', 'Pause');
  } else if (hasStarted) {
    ctx.computed.subStatusText = ctx.t('subPausedHint', 'Paused');
    ctx.computed.primaryActionIcon = 'play';
    ctx.computed.primaryActionLabel = ctx.t('btnResume', 'Resume');
  } else {
    ctx.computed.subStatusText = ctx.t('finishAtFmt', 'Ends at {0}', formatFinishHHMM(nowMs, durMin * 60));
    ctx.computed.primaryActionIcon = 'play';
    ctx.computed.primaryActionLabel = ctx.t('btnStartFmt', 'Start Focus · {0}m', durMin);
  }

  if (isRunning || hasStarted) {
    ctx.computed.secondaryActionIcon = 'refresh';
    ctx.computed.secondaryActionLabel = ctx.t('btnReset', 'Reset');
  } else {
    ctx.computed.secondaryActionIcon = activeScene === 'off' ? 'mute' : 'volume';
    ctx.computed.secondaryActionLabel = vol + '%';
  }

  ctx.computed.todaySummaryText = ctx.t('mixerStatsFmt', '{0}m today · {1} sessions', displayTodayMin, displayTodayRuns);
  ctx.computed.muteTooltip = mode === 'off'
    ? ctx.t('tooltipUnmute', 'Play ambient sound')
    : ctx.t('tooltipMute', 'Mute ambient sound');

  const keepAwakeOn = Boolean(env.device && readBool(env.device.keepAwake, false));
  ctx.computed.keepAwakeTooltip = keepAwakeOn
    ? ctx.t('tooltipAwakeOn', 'Keep screen awake: On')
    : ctx.t('tooltipAwakeOff', 'Keep screen awake: Off');

  const progressPct = (isRunning || hasStarted)
    ? Math.round((1 - remSec / Math.max(1, durMin * 60)) * 100)
    : 0;
  ctx.computed.capsuleTitle = isRunning
    ? (formatMMSS(remSec) + ' · ' + (activeScene !== 'off' ? activeLabel : lastLabel))
    : (formatMMSS(durMin * 60) + ' · ' + ctx.t('widgetName', 'Ambient Focus'));
  ctx.computed.capsuleSummary = displayTodayMin > 0
    ? ctx.t('todaySummaryFmt', '{0}m focused today', displayTodayMin)
    : ctx.t('headerReadyHint', 'Pick an ambient sound to focus');
  ctx.computed.capsuleBadge = activeScene !== 'off'
    ? (activeLabel + ' ' + vol + '%')
    : ctx.t('noiseMutedShort', 'Muted');
  ctx.computed.capsuleProgress = Math.max(0, Math.min(100, progressPct));
  ctx.computed.capsulePrimaryIcon = isRunning ? 'pause' : 'play';

  ctx.computed.dialShapes = buildDialShapes(s, visualMeta.hex);
}

function dispatchSoundStart(ctx, mode, volPct) {
  const m = normalizeSoundKey(mode);
  const vol = clampInt(volPct, 70, 0, 100);
  if (m === 'off' || vol <= 0) {
    ctx.dispatch({ type: 'audio.stopAll' });
    return;
  }
  if (m === 'night') {
    const subVol = Math.max(1, Math.round(vol * 0.48));
    ctx.dispatch([
      { type: 'audio.play', trackId: 'ambient', src: 'noise:rain', loop: true, volume: String(vol) },
      { type: 'audio.play', trackId: 'sub', src: 'noise:brown', loop: true, volume: String(subVol) }
    ]);
  } else {
    ctx.dispatch([
      { type: 'audio.stop', trackId: 'sub' },
      { type: 'audio.play', trackId: 'ambient', src: 'noise:' + m, loop: true, volume: String(vol) }
    ]);
  }
}

function dispatchSoundVolume(ctx, mode, volPct) {
  const m = normalizeSoundKey(mode);
  const vol = clampInt(volPct, 70, 0, 100);
  if (m === 'off') return;
  if (vol <= 0) {
    ctx.dispatch({ type: 'audio.stopAll' });
    return;
  }
  if (m === 'night') {
    const subVol = Math.max(1, Math.round(vol * 0.48));
    ctx.dispatch([
      { type: 'audio.setVolume', trackId: 'ambient', value: String(vol) },
      { type: 'audio.setVolume', trackId: 'sub', value: String(subVol) }
    ]);
  } else {
    ctx.dispatch({ type: 'audio.setVolume', trackId: 'ambient', value: String(vol) });
  }
}

function applyMinsFromPointer(ctx, x, y, playDetent) {
  const s = ctx.state;
  const dx = x - CX;
  const dy = y - CY;
  let deg = (Math.atan2(dy, dx) * 180.0) / Math.PI;
  if (deg < 90) deg += 360;
  const clamped = Math.max(START_ANGLE, Math.min(START_ANGLE + SWEEP_SPAN, deg));
  const norm = (clamped - START_ANGLE) / SWEEP_SPAN;
  let mins = Math.round(SCALE_MIN + norm * (MAX_MINS - SCALE_MIN));
  for (let i = 0; i < SNAP_TARGETS.length; i++) {
    if (Math.abs(mins - SNAP_TARGETS[i]) <= 1) {
      mins = SNAP_TARGETS[i];
      break;
    }
  }
  mins = clampInt(mins, 30, MIN_MINS, MAX_MINS);
  const detent = Math.floor(mins / 5);
  const prevDetent = clampInt(s.lastTickDetent, -1, -1, 99);
  if (playDetent && detent !== prevDetent) {
    const hapticOn = !ctx.settings || readBool(ctx.settings.hapticTicks, true);
    if (hapticOn) {
      ctx.dispatch({ type: 'audio.cue', cue: 'tick', volume: '35' });
    }
  }
  s.lastTickDetent = detent;
  s.durationMin = mins;
  s.remainingSec = mins * 60;
  if (readBool(s.isRunning, false)) {
    s.endUnixSec = Math.floor(Date.now() / 1000) + s.remainingSec;
  }
}

export function onInit(ctx) {
  const s = ctx.state || (ctx.state = {});
  const env = (ctx && ctx.env) || {};
  const nowSec = Math.floor(Date.now() / 1000);
  const durMin = clampInt(s.durationMin, 30, MIN_MINS, MAX_MINS);
  s.durationMin = durMin;
  s.level = clampInt(s.level, 70, 0, 100);
  if (!env.isPreview && !env.ambientPlaying) {
    s.noiseMode = 'off';
    s.fadeStep = 0;
  }
  if (readBool(s.isRunning, false)) {
    const endSec = Number(s.endUnixSec || 0);
    if (endSec > nowSec) {
      s.remainingSec = Math.min(MAX_MINS * 60, Math.max(1, Math.round(endSec - nowSec)));
      s.hasStarted = true;
    } else {
      s.isRunning = false;
      s.hasStarted = false;
      s.endUnixSec = 0;
      s.remainingSec = durMin * 60;
    }
  }
  project(ctx, nowSec * 1000);
}

export function refreshView(ctx) {
  project(ctx);
}

export function onSettingsChange(ctx) {
  const s = ctx.state || (ctx.state = {});
  if (ctx && ctx.settings && !readBool(ctx.settings.showNoiseBar, true) && normalizeSoundKey(s.noiseMode) !== 'off') {
    s.noiseMode = 'off';
    s.fadeStep = 0;
    ctx.dispatch({ type: 'audio.stopAll' });
  }
  project(ctx);
}

export function onAmbientPreempted(ctx) {
  const s = ctx.state || (ctx.state = {});
  s.noiseMode = 'off';
  s.fadeStep = 0;
  project(ctx);
}

export function onTick(ctx, nowUnixMs) {
  const s = ctx.state || (ctx.state = {});
  const nowMs = Number.isFinite(nowUnixMs) ? nowUnixMs : Date.now();
  const nowSec = Math.floor(nowMs / 1000);
  const mode = normalizeSoundKey(s.noiseMode);
  const isRunning = readBool(s.isRunning, false);

  if (isRunning || mode !== 'off') {
    s.wavePhase = ((Number(s.wavePhase) || 0) + 1) % 3600;
  }

  const fadeStep = clampInt(s.fadeStep, 0, 0, 3);
  if (fadeStep > 0 && mode !== 'off') {
    const targetVol = clampInt(s.level, 70, 0, 100);
    const ratios = [0.35, 0.70, 1.0];
    const stepVol = Math.round(targetVol * ratios[3 - fadeStep]);
    dispatchSoundVolume(ctx, mode, stepVol);
    s.fadeStep = fadeStep - 1;
  }

  if (isRunning) {
    const endSec = Number(s.endUnixSec || 0);
    const rem = endSec > 0 ? Math.max(0, Math.round(endSec - nowSec)) : Math.max(0, clampInt(s.remainingSec, 0, 0, MAX_MINS * 60) - 1);
    s.remainingSec = rem;

    if (rem > 0 && rem <= 4 && mode !== 'off') {
      const targetVol = clampInt(s.level, 70, 0, 100);
      dispatchSoundVolume(ctx, mode, Math.max(5, Math.round(targetVol * (rem / 5))));
    }

    if (rem <= 0) {
      const completedMin = clampInt(s.durationMin, 30, MIN_MINS, MAX_MINS);
      syncTodayBucket(s, nowMs);
      s.todayMinutes = clampInt(s.todayMinutes, 0, 0, 1440) + completedMin;
      s.todaySessions = clampInt(s.todaySessions, 0, 0, 200) + 1;
      s.isRunning = false;
      s.hasStarted = false;
      s.endUnixSec = 0;
      s.remainingSec = completedMin * 60;
      s.fadeStep = 0;

      const actions = [];
      if (mode !== 'off') {
        s.noiseMode = 'off';
        actions.push({ type: 'audio.stopAll' });
      }
      actions.push({ type: 'audio.cue', cue: 'bell', volume: '65' });
      actions.push({
        type: 'toast.show',
        title: ctx.t('completeToastTitle', 'Focus Complete'),
        message: ctx.t('completeToastBodyFmt', 'Completed {0} minutes of calm focus', completedMin)
      });
      ctx.dispatch(actions);
    }
  }

  project(ctx, nowMs);
}

export function onDialPointerDown(ctx) {
  const ev = ctx && ctx.event ? ctx.event : null;
  if (!ev || !Number.isFinite(ev.x) || !Number.isFinite(ev.y)) {
    project(ctx);
    return;
  }
  const dist = Math.hypot(ev.x - CX, ev.y - CY);
  if (dist <= 34) {
    ctx.state.pointerZone = 'center';
    project(ctx);
    return;
  }
  ctx.state.pointerZone = 'ring';
  applyMinsFromPointer(ctx, ev.x, ev.y, true);
  project(ctx);
}

export function onDialPointerMove(ctx) {
  const ev = ctx && ctx.event ? ctx.event : null;
  if (!ev || !ev.isPressed || ctx.state.pointerZone !== 'ring') {
    return;
  }
  if (!Number.isFinite(ev.x) || !Number.isFinite(ev.y)) {
    return;
  }
  applyMinsFromPointer(ctx, ev.x, ev.y, true);
  project(ctx);
}

export function onDialPointerUp(ctx) {
  const zone = String((ctx.state && ctx.state.pointerZone) || '');
  ctx.state.pointerZone = '';
  if (zone === 'center') {
    toggleSession(ctx);
    return;
  }
  project(ctx);
}

export function toggleSession(ctx) {
  const s = ctx.state || (ctx.state = {});
  s.previewTouch = true;
  const nowMs = Date.now();
  const nowSec = Math.floor(nowMs / 1000);
  const durMin = clampInt(s.durationMin, 30, MIN_MINS, MAX_MINS);
  const isRunning = readBool(s.isRunning, false);

  if (isRunning) {
    const endSec = Number(s.endUnixSec || 0);
    s.remainingSec = endSec > nowSec ? Math.max(1, Math.round(endSec - nowSec)) : clampInt(s.remainingSec, durMin * 60, 1, MAX_MINS * 60);
    s.isRunning = false;
    s.hasStarted = true;
    s.endUnixSec = 0;
    s.fadeStep = 0;
    ctx.dispatch({ type: 'audio.cue', cue: 'soft', volume: '45' });
  } else {
    const startSec = readBool(s.hasStarted, false)
      ? clampInt(s.remainingSec, durMin * 60, 1, MAX_MINS * 60)
      : durMin * 60;
    s.remainingSec = startSec;
    s.endUnixSec = nowSec + startSec;
    s.isRunning = true;
    s.hasStarted = true;

    const autoSound = !ctx.settings || readBool(ctx.settings.autoSoundOnStart, true);
    const mode = normalizeSoundKey(s.noiseMode);
    if (mode === 'off' && autoSound) {
      let scape = normalizeSoundKey(s.lastSoundscape);
      if (scape === 'off') scape = 'rain';
      s.noiseMode = scape;
      s.lastSoundscape = scape;
      s.fadeStep = 3;
      const vol = clampInt(s.level, 70, 0, 100);
      dispatchSoundStart(ctx, scape, Math.max(10, Math.round(vol * 0.25)));
    } else {
      ctx.dispatch({ type: 'audio.cue', cue: 'soft', volume: '50' });
    }
  }
  project(ctx, nowMs);
}

export function handleSecondaryAction(ctx) {
  const s = ctx.state || (ctx.state = {});
  if (readBool(s.isRunning, false) || readBool(s.hasStarted, false)) {
    const durMin = clampInt(s.durationMin, 30, MIN_MINS, MAX_MINS);
    s.isRunning = false;
    s.hasStarted = false;
    s.endUnixSec = 0;
    s.remainingSec = durMin * 60;
    s.fadeStep = 0;
    ctx.dispatch({ type: 'audio.cue', cue: 'pop', volume: '45' });
    project(ctx);
    return;
  }
  ctx.dispatch({ type: 'popover.toggle', popoverId: 'mixer' });
  project(ctx);
}

function extractArg(ctx, secondParam) {
  if (typeof secondParam === 'string' || typeof secondParam === 'number') {
    return secondParam;
  }
  if (secondParam && typeof secondParam === 'object') {
    if (secondParam.arg != null && secondParam.arg !== '') return secondParam.arg;
    if (secondParam.value != null && secondParam.value !== '') return secondParam.value;
    if (secondParam.scene != null && secondParam.scene !== '') return secondParam.scene;
    if (secondParam.mins != null && secondParam.mins !== '') return secondParam.mins;
  }
  if (ctx && ctx.args) {
    if (ctx.args.scene != null && ctx.args.scene !== '') return ctx.args.scene;
    if (ctx.args.mins != null && ctx.args.mins !== '') return ctx.args.mins;
  }
  if (ctx && ctx.event) {
    if (ctx.event.arg != null && ctx.event.arg !== '') return ctx.event.arg;
    if (ctx.event.value != null && ctx.event.value !== '') return ctx.event.value;
  }
  return undefined;
}

export function setDuration(ctx, arg) {
  const s = ctx.state || (ctx.state = {});
  const extracted = extractArg(ctx, arg);
  const mins = clampInt(extracted != null ? extracted : 30, 30, MIN_MINS, MAX_MINS);
  s.durationMin = mins;
  s.remainingSec = mins * 60;
  s.lastTickDetent = Math.floor(mins / 5);
  if (readBool(s.isRunning, false)) {
    s.endUnixSec = Math.floor(Date.now() / 1000) + mins * 60;
  } else {
    s.hasStarted = false;
    s.endUnixSec = 0;
  }
  ctx.dispatch({ type: 'audio.cue', cue: 'tick', volume: '40' });
  project(ctx);
}

export function selectSound(ctx, arg) {
  const s = ctx.state || (ctx.state = {});
  s.previewTouch = true;
  const candidate = extractArg(ctx, arg);
  const requested = normalizeSoundKey(candidate != null ? candidate : s.noiseMode);
  const fromCardClick = Boolean(ctx && ctx.args && ctx.args.scene);
  const current = normalizeSoundKey(s.noiseMode);
  const nextMode = (fromCardClick && requested !== 'off' && requested === current) ? 'off' : requested;

  s.noiseMode = nextMode;
  s.fadeStep = 0;
  if (nextMode !== 'off') {
    s.lastSoundscape = nextMode;
    dispatchSoundStart(ctx, nextMode, clampInt(s.level, 70, 0, 100));
  } else {
    ctx.dispatch({ type: 'audio.stopAll' });
  }
  project(ctx);
}

export function toggleMute(ctx) {
  const s = ctx.state || (ctx.state = {});
  s.previewTouch = true;
  const cur = normalizeSoundKey(s.noiseMode);
  if (cur !== 'off') {
    s.lastSoundscape = cur;
    s.noiseMode = 'off';
    s.fadeStep = 0;
    ctx.dispatch({ type: 'audio.stopAll' });
  } else {
    let scape = normalizeSoundKey(s.lastSoundscape);
    if (scape === 'off') scape = 'rain';
    s.noiseMode = scape;
    s.lastSoundscape = scape;
    dispatchSoundStart(ctx, scape, clampInt(s.level, 70, 0, 100));
  }
  project(ctx);
}

export function setVolume(ctx, arg) {
  const s = ctx.state || (ctx.state = {});
  const prevVol = clampInt(s.level, 70, 0, 100);
  const extracted = extractArg(ctx, arg);
  const raw = extracted != null ? extracted : prevVol;
  const nextVol = clampInt(raw, prevVol, 0, 100);
  s.level = nextVol;
  s.fadeStep = 0;
  const mode = normalizeSoundKey(s.noiseMode);
  if (mode !== 'off') {
    if (nextVol <= 0) {
      ctx.dispatch({ type: 'audio.stopAll' });
    } else if (prevVol <= 0 || mode === 'night') {
      dispatchSoundStart(ctx, mode, nextVol);
    } else {
      dispatchSoundVolume(ctx, mode, nextVol);
    }
  }
  project(ctx);
}

export function capsuleQuickAction(ctx) {
  toggleSession(ctx);
}
