const ALL_CHANNELS = [
  { id: 'news', url: 'https://www.chinanews.com.cn/rss/scroll-news.xml', shortKey: 'chNewsShort', fullKey: 'chNewsFull', fallbackShort: 'Top', fallbackFull: 'Top Stories' },
  { id: 'finance', url: 'https://rss.huxiu.com/', shortKey: 'chFinanceShort', fullKey: 'chFinanceFull', fallbackShort: 'Biz', fallbackFull: 'Business' },
  { id: 'sspai', url: 'https://sspai.com/feed', shortKey: 'chLifeShort', fullKey: 'chLifeFull', fallbackShort: 'Life', fallbackFull: 'SSPai' },
  { id: 'trend', url: 'https://www.ifanr.com/feed', shortKey: 'chTrendShort', fullKey: 'chTrendFull', fallbackShort: 'Trends', fallbackFull: 'Trends' },
  { id: 'ithome', url: 'https://www.ithome.com/rss/', shortKey: 'chDigitalShort', fullKey: 'chDigitalFull', fallbackShort: 'Tech', fallbackFull: 'ITHome' },
  { id: 'hn', url: 'https://hnrss.org/frontpage', shortKey: 'chHnShort', fullTitle: 'Hacker News', fallbackShort: 'HN', fallbackFull: 'Hacker News' },
  { id: 'techcrunch', url: 'https://techcrunch.com/feed/', shortKey: 'chTcShort', fullTitle: 'TechCrunch', fallbackShort: 'TC', fallbackFull: 'TechCrunch' },
  { id: 'verge', url: 'https://www.theverge.com/rss/index.xml', shortKey: 'chVergeShort', fullTitle: 'The Verge', fallbackShort: 'Verge', fallbackFull: 'The Verge' },
  { id: 'github', url: 'https://github.blog/feed/', shortKey: 'chGithubShort', fullTitle: 'GitHub Blog', fallbackShort: 'GitHub', fallbackFull: 'GitHub Blog' }
];

const ZH_SLOT_IDS = ['news', 'finance', 'sspai', 'trend', 'ithome'];
const GLOBAL_SLOT_IDS = ['hn', 'techcrunch', 'verge', 'sspai', 'github'];

const CUSTOM_CH = {
  id: 'custom',
  url: '',
  shortKey: 'chCustomShort',
  fallbackShort: 'Custom',
  fallbackFull: 'Custom'
};

function t(ctx, key, fallback) {
  if (ctx && typeof ctx.t === 'function') {
    const res = ctx.t(key, fallback || '');
    if (res) return String(res);
  }
  return fallback || '';
}

function isChineseHostLocale(ctx) {
  const loc = String((ctx && ctx.env && ctx.env.locale) || (ctx && ctx.locale) || '').trim().toLowerCase();
  return !loc || loc.indexOf('zh') === 0;
}

function findChannelById(id) {
  const target = String(id || '').trim().toLowerCase();
  if (target === 'custom') return CUSTOM_CH;
  for (let i = 0; i < ALL_CHANNELS.length; i++) {
    if (ALL_CHANNELS[i].id === target) return ALL_CHANNELS[i];
  }
  return null;
}

function findChannelByUrl(url) {
  const u = String(url || '').trim().toLowerCase().replace(/\/+$/, '');
  if (!u) return null;
  for (let i = 0; i < ALL_CHANNELS.length; i++) {
    const candidate = ALL_CHANNELS[i].url.toLowerCase().replace(/\/+$/, '');
    if (candidate === u) return ALL_CHANNELS[i];
  }
  return null;
}

function resolveChannelShort(ctx, ch) {
  if (!ch) return '';
  if (ch.id === 'news') return t(ctx, 'chNewsShort', 'Top');
  if (ch.id === 'finance') return t(ctx, 'chFinanceShort', 'Biz');
  if (ch.id === 'sspai') return t(ctx, 'chLifeShort', 'Life');
  if (ch.id === 'trend') return t(ctx, 'chTrendShort', 'Trends');
  if (ch.id === 'ithome') return t(ctx, 'chDigitalShort', 'Tech');
  if (ch.id === 'hn') return t(ctx, 'chHnShort', 'HN');
  if (ch.id === 'techcrunch') return t(ctx, 'chTcShort', 'TC');
  if (ch.id === 'verge') return t(ctx, 'chVergeShort', 'Verge');
  if (ch.id === 'github') return t(ctx, 'chGithubShort', 'GitHub');
  if (ch.id === 'custom') return t(ctx, 'chCustomShort', 'Custom');
  return ch.fallbackShort || ch.id;
}

function resolveChannelFullTitle(ctx, ch) {
  if (!ch) return '';
  if (ch.fullTitle) return ch.fullTitle;
  if (ch.id === 'news') return t(ctx, 'chNewsFull', 'Top Stories');
  if (ch.id === 'finance') return t(ctx, 'chFinanceFull', 'Business');
  if (ch.id === 'sspai') return t(ctx, 'chLifeFull', 'SSPai');
  if (ch.id === 'trend') return t(ctx, 'chTrendFull', 'Trends');
  if (ch.id === 'ithome') return t(ctx, 'chDigitalFull', 'ITHome');
  if (ch.id === 'custom') return t(ctx, 'chCustomShort', 'Custom');
  return ch.fallbackFull || ch.id;
}

function resolvePresetChannel(settings) {
  const presetRaw = String((settings && settings.presetFeed) || 'auto').trim();
  if (!presetRaw || presetRaw === 'auto' || presetRaw === 'custom') return null;
  return findChannelByUrl(presetRaw) || findChannelById(presetRaw);
}

function resolveChannelBar(ctx, isZh, activeId) {
  const slotIds = (isZh ? ZH_SLOT_IDS : GLOBAL_SLOT_IDS).slice();
  const presetCh = resolvePresetChannel(ctx && ctx.settings);
  if (presetCh && presetCh.id !== 'custom' && slotIds.indexOf(presetCh.id) < 0) {
    slotIds[4] = presetCh.id;
  } else if (activeId && activeId !== 'custom' && slotIds.indexOf(activeId) < 0) {
    slotIds[4] = activeId;
  }
  return slotIds;
}

function resolveActiveContext(ctx) {
  const isZh = isChineseHostLocale(ctx);
  const baseSlots = isZh ? ZH_SLOT_IDS : GLOBAL_SLOT_IDS;
  const defaultId = baseSlots[0];
  const customUrl = String((ctx.settings && ctx.settings.feedUrl) || '').trim();
  const hasCustomFeed = customUrl.length > 0;
  const presetCh = resolvePresetChannel(ctx && ctx.settings);

  let activeKey = String((ctx.state && ctx.state.activeChannelKey) || '').trim().toLowerCase();
  if (!activeKey || activeKey === 'auto') {
    activeKey = presetCh ? presetCh.id : defaultId;
  }
  if (activeKey === 'custom' && !hasCustomFeed) {
    activeKey = presetCh ? presetCh.id : defaultId;
  }

  let ch = findChannelById(activeKey);
  if (!ch) {
    activeKey = presetCh ? presetCh.id : defaultId;
    ch = findChannelById(activeKey) || ALL_CHANNELS[0];
  }

  const slotIds = resolveChannelBar(ctx, isZh, ch.id);
  const feedUrl = ch.id === 'custom' ? customUrl : ch.url;
  let activeSlot = ch.id === 'custom' ? 'custom' : '';
  if (!activeSlot) {
    const idx = slotIds.indexOf(ch.id);
    activeSlot = idx >= 0 ? ('slot' + idx) : 'slot0';
  }

  return {
    isZh: isZh,
    slotIds: slotIds,
    hasCustomFeed: hasCustomFeed,
    activeKey: ch.id,
    ch: ch,
    feedUrl: feedUrl,
    activeSlot: activeSlot
  };
}

function decodeHtmlEntities(str) {
  if (!str) return '';
  return String(str)
    .replace(/&#124;/g, '\uff5c')
    .replace(/&#39;|&apos;/g, "'")
    .replace(/&quot;/g, '"')
    .replace(/&amp;/g, '&')
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&nbsp;/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
}

function cleanEditorialSummary(raw) {
  let s = decodeHtmlEntities(raw);
  if (!s) return '';
  s = s.replace(/^\u4e2d\u65b0[\u793e\u7f51][^\s\uff09)]{1,16}\d{1,2}\u6708\d{1,2}\u65e5\u7535\s*(\([^)]*\)|\uff08[^\uff09]*\uff09)?\s*/g, '');
  s = s.replace(/^\u672c\u6587\u6765\u81ea\u5fae\u4fe1\u516c\u4f17\u53f7[\uff1a:][^\u3002\uff0c,\n]*([\uff0c,]\s*(\u4f5c\u8005|\u9898\u56fe\u6765\u81ea|\u7f16\u8f91)[\uff1a:][^\u3002\uff0c,\n]*)*[\u3002\uff0c,\s]*/g, '');
  return s.trim();
}

function formatShortTime(rawPub) {
  const s = String(rawPub || '').trim();
  if (!s) return '';
  const hm = /(\d{2}:\d{2})/.exec(s);
  if (hm) return hm[1];
  return s.slice(0, 10);
}

function getSampleSpec(ctx, idx) {
  const isZh = isChineseHostLocale(ctx);
  const url = isZh ? 'https://www.chinanews.com.cn/' : 'https://news.ycombinator.com/';
  if (idx === 0) {
    return {
      id: 'sample-0',
      title: t(ctx, 'sample0Title', 'Seasonal Travel and Everyday Retail Demand Continue to Grow'),
      summary: t(ctx, 'sample0Summary', 'Consumer trade-in programs and cultural tourism initiatives boost weekend travel.'),
      url: url,
      publishedAt: '08:25',
      author: t(ctx, 'sample0Author', 'Briefing'),
      category: t(ctx, 'sample0Cat', 'Economy')
    };
  }
  if (idx === 1) {
    return {
      id: 'sample-1',
      title: t(ctx, 'sample1Title', 'Energy-Efficient Smart Home Appliances Lead New Household Upgrades'),
      summary: t(ctx, 'sample1Summary', 'Practical, quiet everyday home devices gain wide adoption among families.'),
      url: url,
      publishedAt: '08:12',
      author: t(ctx, 'sample1Author', 'Business'),
      category: t(ctx, 'sample1Cat', 'Markets')
    };
  }
  if (idx === 2) {
    return {
      id: 'sample-2',
      title: t(ctx, 'sample2Title', 'Ten-Minute Desk Stretches That Relieve Afternoon Neck and Eye Fatigue'),
      summary: t(ctx, 'sample2Summary', 'Short movement breaks and daylight exposure help sustain steady afternoon focus.'),
      url: isZh ? 'https://sspai.com/' : 'https://www.theverge.com/',
      publishedAt: '07:50',
      author: t(ctx, 'sample2Author', 'Lifestyle'),
      category: t(ctx, 'sample2Cat', 'Wellness')
    };
  }
  if (idx === 3) {
    return {
      id: 'sample-3',
      title: t(ctx, 'sample3Title', 'Intercity Rail Lines Add Extra Weekend Trains for Short Regional Trips'),
      summary: t(ctx, 'sample3Summary', 'Faster transfers and flexible ticketing make weekend getaways smoother.'),
      url: url,
      publishedAt: '07:30',
      author: t(ctx, 'sample3Author', 'Transit'),
      category: t(ctx, 'sample3Cat', 'Cities')
    };
  }
  if (idx === 4) {
    return {
      id: 'sample-4',
      title: t(ctx, 'sample4Title', 'Cross-Device File Sharing Between PC and Phone Gets Simpler'),
      summary: t(ctx, 'sample4Summary', 'Calmer notification defaults and instant nearby sharing reduce daily friction.'),
      url: isZh ? 'https://www.ithome.com/' : 'https://github.blog/',
      publishedAt: '07:15',
      author: t(ctx, 'sample4Author', 'Tech'),
      category: t(ctx, 'sample4Cat', 'Devices')
    };
  }
  return {
    id: 'sample-5',
    title: t(ctx, 'sample5Title', 'City Museums Extend Weekend Evening Hours for New Exhibitions'),
    summary: t(ctx, 'sample5Summary', 'Evening gallery openings and interactive guides attract steady weekend visitors.'),
    url: url,
    publishedAt: '06:58',
    author: t(ctx, 'sample5Author', 'Culture'),
    category: t(ctx, 'sample5Cat', 'Arts')
  };
}

function loadDisplayArticles(ctx, query) {
  const raw = (ctx.collections && typeof ctx.collections.get === 'function')
    ? (ctx.collections.get('articles') || [])
    : [];

  const status = String((ctx.state && ctx.state.sourceStatus) || 'idle').trim().toLowerCase();
  const env = ctx.env || {};
  const isPreview = env.isPreview === true || env.isPreview === 'true';
  const activeKey = String((ctx.state && ctx.state.activeChannelKey) || '').trim().toLowerCase();

  const sourceList = [];
  if (raw.length > 0) {
    for (let i = 0; i < raw.length; i++) {
      const it = raw[i] || {};
      const id = String(it.id || ('art-' + i));
      const m = /^sample-([0-5])$/.exec(id);
      if (m) {
        sourceList.push(getSampleSpec(ctx, Number(m[1])));
      } else {
        sourceList.push({
          id: id,
          title: decodeHtmlEntities(it.title || ''),
          summary: cleanEditorialSummary(it.summary || ''),
          url: String(it.url || it.link || '').trim(),
          publishedAt: formatShortTime(it.publishedAt || it.pubDate || ''),
          author: decodeHtmlEntities(it.author || ''),
          category: decodeHtmlEntities(it.category || '')
        });
      }
    }
  } else if (!query && status !== 'error' && status !== 'loading' && (isPreview || activeKey !== 'custom')) {
    for (let s = 0; s < 6; s++) {
      sourceList.push(getSampleSpec(ctx, s));
    }
  }

  const out = [];
  for (let i = 0; i < sourceList.length; i++) {
    const item = sourceList[i];
    if (!item.title) continue;
    if (query) {
      const hay = (item.title + ' ' + item.summary + ' ' + item.category).toLowerCase();
      if (hay.indexOf(query) < 0) continue;
    }
    out.push(item);
  }
  return out;
}

function project(ctx) {
  const active = resolveActiveContext(ctx);
  if (ctx.state && ctx.state.activeChannelKey !== active.activeKey) {
    ctx.state.activeChannelKey = active.activeKey;
  }

  for (let s = 0; s < active.slotIds.length; s++) {
    const slotCh = findChannelById(active.slotIds[s]);
    ctx.computed['ch' + s + 'Label'] = resolveChannelShort(ctx, slotCh);
  }

  const rawFeedTitle = String((ctx.state && ctx.state.feedTitle) || '').trim();
  const chFull = resolveChannelFullTitle(ctx, active.ch);
  const chShort = resolveChannelShort(ctx, active.ch);
  const channelTitle = active.ch.id === 'custom'
    ? (rawFeedTitle || chShort || t(ctx, 'defaultFeedTitle', 'Daily Briefing'))
    : (chFull || rawFeedTitle || t(ctx, 'defaultFeedTitle', 'Daily Briefing'));

  ctx.computed.hasCustomFeed = active.hasCustomFeed ? 'true' : 'false';
  ctx.computed.activeChannelSlot = active.activeSlot;
  ctx.computed.feedUrl = active.feedUrl;
  ctx.computed.channelTitle = channelTitle;
  ctx.computed.channelShortTitle = chShort;

  const status = String((ctx.state && ctx.state.sourceStatus) || 'idle').trim();
  const updatedAt = String((ctx.state && ctx.state.sourceUpdatedAt) || '').trim();
  const suffix = t(ctx, 'updatedSuffix', '');
  if (status === 'loading') {
    ctx.computed.statusText = t(ctx, 'syncing', 'Updating…');
  } else if (status === 'error') {
    ctx.computed.statusText = t(ctx, 'syncErrorFallback', 'Sync error');
  } else if (updatedAt && updatedAt !== '--:--') {
    ctx.computed.statusText = suffix ? (updatedAt + ' ' + suffix) : updatedAt;
  } else {
    ctx.computed.statusText = chShort;
  }

  const query = String((ctx.state && ctx.state.searchQuery) || '').trim().toLowerCase();
  const matched = loadDisplayArticles(ctx, query);

  const showLead = !query && matched.length > 0;
  if (showLead) {
    const lead = matched[0];
    const metaParts = [chShort];
    if (lead.publishedAt) metaParts.push(lead.publishedAt);
    ctx.computed.hasLead = 'true';
    ctx.computed.leadTitle = lead.title;
    ctx.computed.leadSummary = lead.summary;
    ctx.computed.leadUrl = lead.url;
    ctx.computed.leadMeta = metaParts.join(' · ');
  } else {
    ctx.computed.hasLead = 'false';
    ctx.computed.leadTitle = '';
    ctx.computed.leadSummary = '';
    ctx.computed.leadUrl = '';
    ctx.computed.leadMeta = '';
  }

  const startIdx = showLead ? 1 : 0;
  const maxStreamRows = query ? 7 : 5;
  const stream = [];
  for (let i = startIdx; i < matched.length && stream.length < maxStreamRows; i++) {
    const row = matched[i];
    const rank = i + 1;
    stream.push({
      id: row.id,
      rank: String(rank),
      isTop: rank <= 3 ? 'true' : 'false',
      title: row.title,
      summary: row.summary,
      url: row.url,
      time: row.publishedAt || '',
      author: row.author || chShort,
      category: row.category || chShort
    });
  }

  ctx.computed.streamRows = stream;
  ctx.computed.emptyTitleText = query
    ? t(ctx, 'searchEmptyTitle', 'No matching headlines')
    : t(ctx, 'emptyTitle', 'Syncing headlines…');
  ctx.computed.emptySubText = query
    ? t(ctx, 'searchEmptySubtitle', 'Try another keyword or clear the filter')
    : t(ctx, 'emptySubtitle', 'Switch channels above or click Refresh');

  const topTitle = matched.length > 0 ? matched[0].title : t(ctx, 'defaultTopHeadline', 'Click refresh for headlines');
  ctx.computed.capsuleTitle = chShort || t(ctx, 'defaultFeedTitle', 'Daily Briefing');
  ctx.computed.capsuleSummary = topTitle;
  ctx.computed.capsuleBadge = String(matched.length);
}

function syncSettingsAndProject(ctx) {
  const isZh = isChineseHostLocale(ctx);
  const slotIds = isZh ? ZH_SLOT_IDS : GLOBAL_SLOT_IDS;
  const defaultKey = slotIds[0];
  const presetSetting = String((ctx.settings && ctx.settings.presetFeed) || 'auto').trim();
  const customUrl = String((ctx.settings && ctx.settings.feedUrl) || '').trim();
  const curSettingsSig = presetSetting + '|' + customUrl;
  const lastSig = String((ctx.state && ctx.state.syncedSettingsFeed) || '').trim();
  const prevPreset = lastSig ? lastSig.split('|')[0] : '';

  if (ctx.state) {
    const curKey = String(ctx.state.activeChannelKey || '').trim().toLowerCase();
    if (!curKey || curKey === 'auto') {
      const presetCh = resolvePresetChannel(ctx.settings);
      ctx.state.activeChannelKey = presetCh ? presetCh.id : defaultKey;
    }

    if (curSettingsSig !== lastSig) {
      const prevKey = String(ctx.state.activeChannelKey || '').trim().toLowerCase();
      ctx.state.syncedSettingsFeed = curSettingsSig;
      if (presetSetting === 'custom' && customUrl) {
        ctx.state.activeChannelKey = 'custom';
      } else if (presetSetting && presetSetting !== 'auto' && presetSetting !== 'custom') {
        const matchedCh = findChannelByUrl(presetSetting) || findChannelById(presetSetting);
        if (matchedCh) {
          ctx.state.activeChannelKey = matchedCh.id;
        } else if (lastSig && presetSetting !== prevPreset) {
          ctx.state.activeChannelKey = defaultKey;
        }
      } else if (lastSig && presetSetting !== prevPreset) {
        ctx.state.activeChannelKey = defaultKey;
      }

      if (lastSig && (presetSetting !== prevPreset || ctx.state.activeChannelKey !== prevKey)) {
        ctx.state.feedTitle = '';
        ctx.state.topHeadline = '';
        ctx.state.feedCount = '0';
        if (ctx.collections && typeof ctx.collections.clear === 'function') {
          ctx.collections.clear('articles');
        }
      }
    }
  }

  project(ctx);
}

export function onInit(ctx) {
  syncSettingsAndProject(ctx);
}

export function onSettingsChange(ctx) {
  syncSettingsAndProject(ctx);
}

export function onSourceData(ctx) {
  project(ctx);
}

export function selectChannel(ctx) {
  const rawSel = String(
    (ctx.args && (ctx.args.slot || ctx.args.id)) ||
    (ctx.event && (ctx.event.value || ctx.event.id)) ||
    ''
  ).trim().toLowerCase();
  if (!rawSel) return;

  const isZh = isChineseHostLocale(ctx);
  const curActiveId = String((ctx.state && ctx.state.activeChannelKey) || '').trim().toLowerCase();
  const slotIds = resolveChannelBar(ctx, isZh, curActiveId);
  let targetKey = rawSel;
  const slotMatch = /^slot([0-4])$/.exec(targetKey);
  if (slotMatch) {
    targetKey = slotIds[Number(slotMatch[1])] || slotIds[0];
  }

  const customUrl = String((ctx.settings && ctx.settings.feedUrl) || '').trim();
  const prevActive = resolveActiveContext(ctx);
  let targetUrl = '';
  let nextTitle = '';
  if (targetKey === 'custom') {
    if (!customUrl) return;
    targetUrl = customUrl;
    nextTitle = t(ctx, 'chCustomShort', 'Custom');
  } else {
    const ch = findChannelById(targetKey);
    if (!ch) return;
    targetUrl = ch.url;
    nextTitle = resolveChannelFullTitle(ctx, ch);
  }

  ctx.state.activeChannelKey = targetKey;
  if (targetUrl.toLowerCase() !== prevActive.feedUrl.toLowerCase()) {
    ctx.state.feedTitle = nextTitle;
    ctx.state.topHeadline = '';
    ctx.state.sourceError = '';
    ctx.state.sourceStatus = 'loading';
    if (ctx.collections && typeof ctx.collections.clear === 'function') {
      ctx.collections.clear('articles');
    }
  }
  project(ctx);
  ctx.dispatch({ type: 'source.refresh', targetId: 'mainFeed' });
}

export function toggleSearch(ctx) {
  const open = String((ctx.state && ctx.state.searchOpen) || 'false') === 'true';
  ctx.state.searchOpen = open ? 'false' : 'true';
  if (open) ctx.state.searchQuery = '';
  project(ctx);
}

export function onSearchChange(ctx) {
  if (ctx.event && typeof ctx.event.value === 'string') {
    ctx.state.searchQuery = ctx.event.value;
  }
  project(ctx);
}

export function clearSearch(ctx) {
  ctx.state.searchQuery = '';
  ctx.state.searchOpen = 'false';
  project(ctx);
}

export function openLeadReader(ctx) {
  const active = resolveActiveContext(ctx);
  const chShort = resolveChannelShort(ctx, active.ch);
  const matched = loadDisplayArticles(ctx, '');
  if (!matched.length) return;
  const lead = matched[0];
  ctx.state.readerTitle = lead.title;
  ctx.state.readerSummary = lead.summary;
  ctx.state.readerUrl = lead.url;
  ctx.state.readerMeta = chShort + (lead.publishedAt ? ' · ' + lead.publishedAt : '');
  project(ctx);
}

export function openItemReader(ctx) {
  const args = ctx.args || {};
  ctx.state.readerTitle = String(args.title || '');
  ctx.state.readerSummary = String(args.summary || '');
  ctx.state.readerUrl = String(args.url || '');
  ctx.state.readerMeta = String(args.category || '') + (args.time ? ' · ' + args.time : '');
  project(ctx);
}
