function t(ctx, key, fallback) {
  if (ctx && typeof ctx.t === 'function') {
    const val = ctx.t(key, fallback || '');
    if (val) return String(val);
  }
  return fallback || '';
}

function toDisplayDeg(celsius, useF) {
  const num = Number(celsius);
  if (!Number.isFinite(num)) return '--';
  const v = useF ? Math.round((num * 9) / 5 + 32) : Math.round(num);
  return String(v) + '°';
}

function resolveSpanColorHex(lowC, highC) {
  const avg = (Number(lowC) + Number(highC)) / 2;
  if (!Number.isFinite(avg)) return '#3B82F6';
  if (highC >= 27) return '#F59E0B';
  if (highC >= 21) return '#10B981';
  if (avg >= 12) return '#0EA5E9';
  return '#3B82F6';
}

function resolveSampleCondition(ctx, idx) {
  if (idx === 1) return t(ctx, 'condSampleSunnyCloudy', 'Mostly Sunny');
  if (idx === 2) return t(ctx, 'condSampleRain', 'Light Showers');
  if (idx === 3) return t(ctx, 'condSampleOvercast', 'Cloudy');
  if (idx === 4) return t(ctx, 'condSampleClear', 'Sunny');
  return t(ctx, 'condSampleCloudy', 'Partly Cloudy');
}

function buildFallbackDays(ctx) {
  const baseMs = (ctx && ctx.env && Number(ctx.env.nowMs)) ? Number(ctx.env.nowMs) : 1763424000000;
  const specs = [
    { hi: 21, lo: 13, icon: '⛅', precip: 10 },
    { hi: 23, lo: 14, icon: '🌤️', precip: 5 },
    { hi: 18, lo: 11, icon: '🌦️', precip: 60 },
    { hi: 17, lo: 10, icon: '☁️', precip: 20 },
    { hi: 20, lo: 12, icon: '☀️', precip: 0 }
  ];
  const out = [];
  for (let i = 0; i < specs.length; i++) {
    const dt = new Date(baseMs + i * 86400000);
    const yyyy = dt.getFullYear();
    const mm = String(dt.getMonth() + 1).padStart(2, '0');
    const dd = String(dt.getDate()).padStart(2, '0');
    out.push({
      id: 'fb-' + i,
      date: yyyy + '-' + mm + '-' + dd,
      shortDate: mm + '-' + dd,
      weekday: '',
      high: String(specs[i].hi),
      low: String(specs[i].lo),
      condition: resolveSampleCondition(ctx, i),
      icon: specs[i].icon,
      precip: specs[i].precip + '%'
    });
  }
  return out;
}

function normalizeForecastDay(ctx, rawItem, idx) {
  const d = rawItem || {};
  const rawId = String(d.id || '');
  const isSampleId = /^fb-[0-4]$/.test(rawId);
  return {
    id: rawId || ('day-' + idx),
    date: String(d.date || ''),
    shortDate: String(d.shortDate || ''),
    weekday: isSampleId ? '' : String(d.weekday || ''),
    high: d.high,
    low: d.low,
    condition: isSampleId ? resolveSampleCondition(ctx, idx) : String(d.condition || ''),
    icon: String(d.icon || '⛅'),
    precip: String(d.precip || '0%')
  };
}

function resolveWeekdayLabel(ctx, index, rawWeekday, rawDate) {
  if (index === 0) return t(ctx, 'dayToday', 'Today');
  if (index === 1) return t(ctx, 'dayTomorrow', 'Tomorrow');
  const trimmed = String(rawWeekday || '').trim();
  if (trimmed && !/^\d{4}-\d{2}-\d{2}$/.test(trimmed)) return trimmed;
  const list = t(ctx, 'weekdaysShort', 'Sun,Mon,Tue,Wed,Thu,Fri,Sat').split(',');
  if (list.length === 7 && typeof rawDate === 'string') {
    const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(rawDate.trim());
    if (m) {
      const dt = new Date(Date.UTC(Number(m[1]), Number(m[2]) - 1, Number(m[3])));
      const dow = dt.getUTCDay();
      if (dow >= 0 && dow < 7 && list[dow]) return String(list[dow]).trim();
    }
  }
  return trimmed || ('D+' + index);
}

function buildRangeBarJson(lowC, highC, weekMinC, weekMaxC, currentTempC, isToday) {
  const trackW = 124;
  const totalSpan = Math.max(4, weekMaxC - weekMinC);
  const startRatio = Math.max(0, Math.min(0.84, (lowC - weekMinC) / totalSpan));
  const endRatio = Math.max(startRatio + 0.16, Math.min(1, (highC - weekMinC) / totalSpan));
  const x = Math.round(startRatio * trackW);
  const w = Math.min(trackW - x, Math.max(18, Math.round((endRatio - startRatio) * trackW)));
  const barColor = resolveSpanColorHex(lowC, highC);

  const shapes = [
    {
      type: 'rect',
      x: 0,
      y: 4,
      width: trackW,
      height: 5,
      rx: 2.5,
      fill: '#888888',
      strokeWidth: 0,
      opacity: 0.18
    },
    {
      type: 'rect',
      x: x,
      y: 4,
      width: w,
      height: 5,
      rx: 2.5,
      fill: barColor,
      strokeWidth: 0,
      opacity: 0.94
    }
  ];

  if (isToday && Number.isFinite(currentTempC)) {
    const clampedCur = Math.max(lowC, Math.min(highC, currentTempC));
    const curRatio = Math.max(0.05, Math.min(0.95, (clampedCur - weekMinC) / totalSpan));
    const cx = Math.round(curRatio * trackW);
    shapes.push({
      type: 'circle',
      cx: cx,
      cy: 6.5,
      r: 3.6,
      fill: '#FFFFFF',
      stroke: barColor,
      strokeWidth: 1.8
    });
  }
  return JSON.stringify(shapes);
}

function formatWindFriendly(ctx, windKmh, useF, isZh) {
  const kmh = Number(windKmh);
  if (!Number.isFinite(kmh)) return '--';
  if (!isZh || useF) {
    const val = useF ? Math.round(kmh * 0.621371) : Math.round(kmh);
    return val + ' ' + (useF ? t(ctx, 'windUnitMph', 'mph') : t(ctx, 'windUnitKmh', 'km/h'));
  }
  let level = 1;
  let desc = t(ctx, 'breezeCalm', 'Calm');
  if (kmh < 6) {
    level = 1;
    desc = t(ctx, 'breezeCalm', 'Calm');
  } else if (kmh < 12) {
    level = 2;
    desc = t(ctx, 'breezeLight', 'Light');
  } else if (kmh < 20) {
    level = 3;
    desc = t(ctx, 'breezeCalm', 'Calm');
  } else if (kmh < 29) {
    level = 4;
    desc = t(ctx, 'breezeMod', 'Gentle');
  } else if (kmh < 39) {
    level = 5;
    desc = t(ctx, 'breezeFresh', 'Fresh');
  } else {
    level = 6;
    desc = t(ctx, 'breezeStrong', 'Windy');
  }
  return desc + ' ' + String(t(ctx, 'windLevelFmt', 'Lv.{0}')).replace('{0}', String(level));
}

function formatHumidityFriendly(ctx, humPct) {
  const h = Number(humPct);
  if (!Number.isFinite(h)) return '--';
  const pctStr = Math.round(h) + '%';
  let tag = t(ctx, 'humComfort', 'Ideal');
  if (h < 35) tag = t(ctx, 'humDry', 'Dry');
  else if (h > 75) tag = t(ctx, 'humWet', 'Humid');
  return pctStr + ' · ' + tag;
}

function formatUvFriendly(ctx, uvNum) {
  const u = Number(uvNum);
  if (!Number.isFinite(u)) return '--';
  const rounded = Math.round(u);
  let level = t(ctx, 'uvLow', 'Low');
  if (u >= 8) level = t(ctx, 'uvVeryHigh', 'Very High');
  else if (u >= 6) level = t(ctx, 'uvHigh', 'High');
  else if (u >= 3) level = t(ctx, 'uvMod', 'Mod');
  return level + ' (' + rounded + ')';
}

export function onInit(ctx) {
  const settings = ctx.settings || {};
  const useF = String(settings.tempUnit || 'c').toLowerCase() === 'f';
  const env = ctx.env || {};
  const locale = String(env.locale || ctx.locale || 'zh-CN').toLowerCase();
  const isZh = locale.indexOf('zh') === 0;
  const w = env.weather || {};
  const hasLive = Boolean(w.hasData);
  const weatherStatus = String(w.status || 'idle').trim().toLowerCase();
  const hasError = weatherStatus === 'error' && !hasLive;

  const citySetting = String(settings.city || '').trim();
  if (ctx.state && ctx.state.lastCity !== citySetting) {
    ctx.state.draftCity = citySetting;
    ctx.state.lastCity = citySetting;
  }

  const rawForecast = (ctx.collections && typeof ctx.collections.get === 'function')
    ? (ctx.collections.get('forecast') || [])
    : [];

  const fallbackDays = buildFallbackDays(ctx);
  const forecastSource = [];
  if (rawForecast.length > 0) {
    for (let i = 0; i < Math.min(5, rawForecast.length); i++) {
      forecastSource.push(normalizeForecastDay(ctx, rawForecast[i], i));
    }
  }
  while (forecastSource.length < 5) {
    forecastSource.push(fallbackDays[forecastSource.length]);
  }

  const resolvedLiveCity = String(w.city || '').trim();
  const isPlaceholderCity = !resolvedLiveCity ||
    resolvedLiveCity === '--' ||
    resolvedLiveCity.indexOf('\u5b9a\u4f4d') >= 0 ||
    resolvedLiveCity.toLowerCase().indexOf('locat') >= 0;
  const cityName = !isPlaceholderCity
    ? resolvedLiveCity
    : (citySetting || t(ctx, 'defaultSampleCity', 'Local'));

  const updatedAt = String(w.updatedAt || '').trim();
  const suffix = t(ctx, 'updatedSuffix', '');
  ctx.computed.cityName = cityName;
  ctx.computed.statusMeta = hasError
    ? ''
    : (hasLive && updatedAt && updatedAt !== '--'
      ? (suffix ? updatedAt + ' ' + suffix : updatedAt)
      : t(ctx, 'previewSyncHint', 'Syncing'));

  ctx.computed.hasError = hasError ? 'true' : 'false';
  ctx.computed.errorMessage = String(w.error || '').trim() || t(ctx, 'errFetch', 'Unable to fetch weather.');

  const firstHi = Number.isFinite(Number(forecastSource[0].high)) ? Number(forecastSource[0].high) : 22;
  const firstLo = Number.isFinite(Number(forecastSource[0].low)) ? Number(forecastSource[0].low) : 14;
  const fallbackTemp = Math.round((firstHi * 0.7) + (firstLo * 0.3));

  const tempC = hasLive && Number.isFinite(Number(w.tempNum)) ? Number(w.tempNum) : fallbackTemp;
  const feelsC = hasLive && Number.isFinite(Number(w.feelsLikeNum)) ? Number(w.feelsLikeNum) : (tempC - 1);
  const highC = hasLive && Number.isFinite(Number(w.highNum)) ? Number(w.highNum) : firstHi;
  const lowC = hasLive && Number.isFinite(Number(w.lowNum)) ? Number(w.lowNum) : firstLo;
  const humPct = hasLive && Number.isFinite(Number(w.humidityNum)) ? Number(w.humidityNum) : 56;
  const windKmh = hasLive && Number.isFinite(Number(w.windSpeedKmh)) ? Number(w.windSpeedKmh) : 9;
  const firstPopNum = parseInt(String(forecastSource[0].precip || '10').replace('%', ''), 10);
  const precipPct = hasLive && Number.isFinite(Number(w.precipNum))
    ? Number(w.precipNum)
    : (Number.isFinite(firstPopNum) ? firstPopNum : 10);
  const uvNum = hasLive && Number.isFinite(Number(w.uvNum)) ? Number(w.uvNum) : 4;

  const heroIcon = (hasLive && w.icon && w.icon !== '--')
    ? String(w.icon)
    : String(forecastSource[0].icon || '⛅');
  const conditionText = (hasLive && w.condition && w.condition !== '--')
    ? String(w.condition)
    : (forecastSource[0].condition ? String(forecastSource[0].condition) : t(ctx, 'condSampleCloudy', 'Partly Cloudy'));

  ctx.computed.isLive = hasLive ? 'true' : 'false';
  ctx.computed.heroIcon = heroIcon;
  ctx.computed.tempHero = toDisplayDeg(tempC, useF);
  ctx.computed.conditionText = conditionText;

  const hiLoText = t(ctx, 'highPrefix', 'H:') + ' ' + toDisplayDeg(highC, useF) +
    '  ' + t(ctx, 'lowPrefix', 'L:') + ' ' + toDisplayDeg(lowC, useF);
  const feelsText = t(ctx, 'feelsPrefix', 'Feels') + ' ' + toDisplayDeg(feelsC, useF);
  ctx.computed.rangeAndFeels = hiLoText + ' · ' + feelsText;

  ctx.computed.hasAdvice = 'true';
  if (precipPct >= 45) {
    ctx.computed.adviceTone = 'cyan';
    ctx.computed.adviceTag = t(ctx, 'tagUmbrella', 'Umbrella');
    ctx.computed.adviceText = t(ctx, 'advRain', 'Chance of rain today.');
  } else if (uvNum >= 6 && tempC >= 20) {
    ctx.computed.adviceTone = 'warning';
    ctx.computed.adviceTag = t(ctx, 'tagUv', 'Sun Protection');
    ctx.computed.adviceText = t(ctx, 'advUv', 'Strong midday UV.');
  } else if (tempC <= 7) {
    ctx.computed.adviceTone = 'accent';
    ctx.computed.adviceTag = t(ctx, 'tagWarm', 'Bundle Up');
    ctx.computed.adviceText = t(ctx, 'advCold', 'Cold outside today.');
  } else if (tempC <= 14 || lowC <= 9) {
    ctx.computed.adviceTone = 'accent';
    ctx.computed.adviceTag = t(ctx, 'tagCoolMorning', 'Light Jacket');
    ctx.computed.adviceText = t(ctx, 'advCoolMorning', 'Cool mornings and evenings.');
  } else if (tempC >= 29) {
    ctx.computed.adviceTone = 'warning';
    ctx.computed.adviceTag = t(ctx, 'tagCool', 'Stay Cool');
    ctx.computed.adviceText = t(ctx, 'advHot', 'Warm afternoon ahead.');
  } else if (windKmh >= 28) {
    ctx.computed.adviceTone = 'accent';
    ctx.computed.adviceTag = t(ctx, 'tagWind', 'Breezy');
    ctx.computed.adviceText = t(ctx, 'advWind', 'Breezy outdoors today.');
  } else {
    ctx.computed.adviceTone = 'emerald';
    ctx.computed.adviceTag = t(ctx, 'tagComfort', 'Pleasant');
    ctx.computed.adviceText = t(ctx, 'advComfort', 'Pleasant conditions outside.');
  }

  let weekMin = Infinity;
  let weekMax = -Infinity;
  for (let i = 0; i < 5; i++) {
    const lo = Number(forecastSource[i].low);
    const hi = Number(forecastSource[i].high);
    if (Number.isFinite(lo) && lo < weekMin) weekMin = lo;
    if (Number.isFinite(hi) && hi > weekMax) weekMax = hi;
  }
  if (!Number.isFinite(weekMin)) weekMin = 10;
  if (!Number.isFinite(weekMax)) weekMax = 24;

  const rows = [];
  for (let i = 0; i < 5; i++) {
    const d = forecastSource[i];
    const lo = Number.isFinite(Number(d.low)) ? Number(d.low) : 14;
    const hi = Number.isFinite(Number(d.high)) ? Number(d.high) : 22;
    const rawPop = parseInt(String(d.precip || '0').replace('%', ''), 10);
    const popVal = Number.isFinite(rawPop) ? rawPop : 0;

    rows.push({
      id: String(d.id || ('day-' + i)),
      isToday: i === 0 ? 'true' : 'false',
      label: resolveWeekdayLabel(ctx, i, d.weekday, d.date),
      icon: String(d.icon || '⛅'),
      pop: popVal >= 30 ? (popVal + '%') : '',
      lowText: toDisplayDeg(lo, useF),
      highText: toDisplayDeg(hi, useF),
      barJson: buildRangeBarJson(lo, hi, weekMin, weekMax, tempC, i === 0)
    });
  }
  ctx.computed.forecastRows = rows;

  const sr = (hasLive && w.sunrise && w.sunrise !== '--') ? String(w.sunrise) : '06:24';
  const ss = (hasLive && w.sunset && w.sunset !== '--') ? String(w.sunset) : '17:18';
  ctx.computed.sunLine = t(ctx, 'sunRisePrefix', 'Sunrise') + ' ' + sr + ' · ' + t(ctx, 'sunSetPrefix', 'Sunset') + ' ' + ss;

  ctx.computed.humidityDisplay = formatHumidityFriendly(ctx, humPct);
  ctx.computed.windDisplay = formatWindFriendly(ctx, windKmh, useF, isZh);
  ctx.computed.precipDisplay = Math.round(precipPct) + '%';
  ctx.computed.uvDisplay = formatUvFriendly(ctx, uvNum);

  ctx.computed.capsuleTitle = cityName + ' ' + ctx.computed.tempHero;
  ctx.computed.capsuleSummary = conditionText + ' · ' + ctx.computed.adviceTag;
  ctx.computed.capsuleBadge = precipPct >= 30
    ? ('☔ ' + Math.round(precipPct) + '%')
    : (toDisplayDeg(lowC, useF) + '~' + toDisplayDeg(highC, useF));
}

export function onSettingsChange(ctx) {
  onInit(ctx);
}

export function onSourceData(ctx) {
  onInit(ctx);
}

export function applyCity(ctx) {
  const nextCity = String((ctx.state && ctx.state.draftCity) || '').trim();
  ctx.dispatch([
    { type: 'settings.set', key: 'city', value: nextCity },
    { type: 'popover.close', popoverId: 'cityPop' },
    { type: 'source.refresh', targetId: 'weather' }
  ]);
}

export function useAutoCity(ctx) {
  if (ctx.state) {
    ctx.state.draftCity = '';
  }
  ctx.dispatch([
    { type: 'settings.set', key: 'city', value: '' },
    { type: 'popover.close', popoverId: 'cityPop' },
    { type: 'source.refresh', targetId: 'weather' }
  ]);
}
