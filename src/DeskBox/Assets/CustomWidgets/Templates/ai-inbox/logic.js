const MODES = {
  translate: { badgeTone: 'cyan' },
  polish: { badgeTone: 'violet' },
  summarize: { badgeTone: 'emerald' },
  todos: { badgeTone: 'cyan' },
  explain: { badgeTone: 'warning' }
};

const SUPPORTED_LANGS = {
  auto: true,
  'zh-CN': true,
  'zh-TW': true,
  'en-US': true,
  'ja-JP': true,
  'ko-KR': true,
  'fr-FR': true,
  'de-DE': true,
  'es-ES': true,
  'pt-BR': true,
  'ru-RU': true,
  'ar-SA': true,
  'hi-IN': true,
  'bn-BD': true
};

const SUPPORTED_OUT_LANGS = {
  auto: true,
  keep: true,
  'zh-CN': true,
  'en-US': true
};

const SUPPORTED_TONES = {
  natural: true,
  professional: true,
  concise: true,
  friendly: true,
  developer: true
};

const LANG_PROMPT_DESC = {
  auto: 'auto-detect',
  'zh-CN': 'Simplified Chinese (简体中文)',
  'zh-TW': 'Traditional Chinese (繁體中文)',
  'en-US': 'English',
  'ja-JP': 'Japanese (日本語)',
  'ko-KR': 'Korean (한국어)',
  'fr-FR': 'French (Français)',
  'de-DE': 'German (Deutsch)',
  'es-ES': 'Spanish (Español)',
  'pt-BR': 'Portuguese (Português)',
  'ru-RU': 'Russian (Русский)',
  'ar-SA': 'Arabic (العربية)',
  'hi-IN': 'Hindi (हिन्दी)',
  'bn-BD': 'Bengali (বাংলা)'
};

const HOST_LOCALE_ENGLISH_NAMES = {
  'zh-cn': 'Simplified Chinese (简体中文)',
  'zh-tw': 'Traditional Chinese (繁體中文)',
  'en-us': 'English',
  'ja-jp': 'Japanese (日本語)',
  'ko-kr': 'Korean (한국어)',
  'fr-fr': 'French (Français)',
  'de-de': 'German (Deutsch)',
  'es-es': 'Spanish (Español)',
  'pt-br': 'Portuguese (Português)',
  'ru-ru': 'Russian (Русский)',
  'ar-sa': 'Arabic (العربية)',
  'hi-in': 'Hindi (हिन्दी)',
  'bn-bd': 'Bengali (বাংলা)'
};

const TONE_PROMPT_DESC = {
  natural: 'natural, idiomatic, fluent',
  professional: 'polite, executive, workplace-appropriate',
  concise: 'concise, clear, everyday spoken style',
  friendly: 'warm, approachable, courteous',
  developer: 'rigorous, formal, structured academic style'
};

const CHECK_GLYPH = 'check';
const TRADITIONAL_HINT_RE = /[繁體與專業翻譯潤飾編輯切換語種紀錄確應這說為時會個國們發學實見嗎麼點線將無還經於現問試驗證預覽]/;
const SIMPLIFIED_HINT_RE = /[简体与专业翻译润色编辑切换语种记录确应这说为时会个国们发学实见吗么点线将无还经于现问题试验证预览]/;

const MODE_I18N = {
  translate: ['modeTranslate', 'phTranslate', 'ctaTranslate', 'clipDirectTranslate', 'insightAlt'],
  polish: ['modePolish', 'phPolish', 'ctaPolish', 'clipDirectPolish', 'insightTone'],
  summarize: ['modeSummarize', 'phSummarize', 'ctaSummarize', 'clipDirectSummarize', 'insightKey'],
  todos: ['modeTodos', 'phTodos', 'ctaTodos', 'clipDirectTodos', 'insightOwner'],
  explain: ['modeExplain', 'phExplain', 'ctaExplain', 'clipDirectExplain', 'insightFix']
};

const SHORT_LANG_KEYS = {
  'zh-CN': 'langZh',
  'zh-TW': 'langZhTw',
  'en-US': 'langEnShort',
  'ja-JP': 'langJaShort',
  'ko-KR': 'langKoShort',
  'fr-FR': 'langFrShort',
  'de-DE': 'langDeShort',
  'es-ES': 'langEsShort',
  'pt-BR': 'langPtShort',
  'ru-RU': 'langRuShort',
  'ar-SA': 'langArShort',
  'hi-IN': 'langHiShort',
  'bn-BD': 'langBnShort'
};

const TONE_LABEL_KEYS = {
  professional: 'toneFormal',
  concise: 'toneConcise',
  friendly: 'toneFriendly',
  developer: 'toneDeveloper'
};

const translateModeLabel = (ctx, mode) => ctx.t((MODE_I18N[mode] || MODE_I18N.translate)[0]);
const translateModePlaceholder = (ctx, mode) => ctx.t((MODE_I18N[mode] || MODE_I18N.translate)[1]);
const translateModeCta = (ctx, mode) => ctx.t((MODE_I18N[mode] || MODE_I18N.translate)[2]);
const translateModeClipDirect = (ctx, mode) => ctx.t((MODE_I18N[mode] || MODE_I18N.translate)[3]);
const translateModeInsight = (ctx, mode) => ctx.t((MODE_I18N[mode] || MODE_I18N.translate)[4]);
const getShortLangLabel = (ctx, code) => ctx.t(SHORT_LANG_KEYS[code] || 'dstAutoShort');

function translateOutLang(ctx, code) {
  if (code === 'keep') return ctx.t('outLangKeep');
  if (code === 'zh-CN') return ctx.t('langZh');
  if (code === 'en-US') return ctx.t('langEnShort');
  return ctx.t('outLangAuto');
}

const translateToneLabel = (ctx, tone) => ctx.t(TONE_LABEL_KEYS[tone] || 'toneNatural');

function resolveUiLocale(ctx) {
  const raw = String((ctx && ctx.env && ctx.env.locale) || (ctx && ctx.locale) || 'en-US').trim();
  return raw || 'en-US';
}

function resolveAutoLangPromptDesc(uiLocale) {
  const norm = String(uiLocale || 'en-US').toLowerCase().replace('_', '-');
  if (norm === 'zh-tw' || norm === 'zh-hk' || norm === 'zh-mo') {
    return 'auto-detect (Traditional Chinese (繁體中文) -> English, non-Chinese -> Traditional Chinese (繁體中文))';
  }
  if (norm.indexOf('zh') === 0) {
    return 'auto-detect (Chinese -> English, non-Chinese -> Simplified Chinese)';
  }
  if (norm.indexOf('en') === 0) {
    return 'auto-detect (English -> Simplified Chinese, non-English -> English)';
  }
  const hostLang = HOST_LOCALE_ENGLISH_NAMES[norm] || uiLocale;
  return `auto-detect (${hostLang} -> English, other languages -> ${hostLang})`;
}

function normalizeMode(raw) {
  const k = String(raw || '').trim().toLowerCase();
  return MODES[k] ? k : 'translate';
}

function normalizeLang(raw) {
  const k = String(raw || '').trim();
  return SUPPORTED_LANGS[k] ? k : 'auto';
}

function normalizeOutLang(raw, fallback) {
  const k = String(raw || '').trim();
  return SUPPORTED_OUT_LANGS[k] ? k : fallback;
}

function normalizeTone(raw) {
  const k = String(raw || '').trim().toLowerCase();
  return SUPPORTED_TONES[k] ? k : 'natural';
}

function normalizeHistoryMode(raw) {
  const k = String(raw || '').trim().toLowerCase();
  return MODES[k] ? k : 'all';
}

function isTrue(val) {
  return val === true || String(val || '').trim().toLowerCase() === 'true';
}

function singleLine(text, maxLen) {
  const cleaned = String(text || '').replace(/\s+/g, ' ').trim();
  if (cleaned.length <= maxLen) return cleaned;
  return cleaned.slice(0, Math.max(1, maxLen - 1)) + '…';
}

function sanitizeUntrustedEcho(text) {
  return String(text || '')
    .replace(/<\/?user_input>/gi, '')
    .trim();
}

function detectLanguageCode(text, uiLocale) {
  const sample = String(text || '').trim();
  if (!sample) return '';
  if (/[\uAC00-\uD7AF\u1100-\u11FF]/.test(sample)) return 'ko-KR';
  if (/[\u3040-\u30FF\u31F0-\u31FF]/.test(sample)) return 'ja-JP';
  if (/[\u0600-\u06FF\u0750-\u077F]/.test(sample)) return 'ar-SA';
  if (/[\u0900-\u097F]/.test(sample)) return 'hi-IN';
  if (/[\u0980-\u09FF]/.test(sample)) return 'bn-BD';
  if (/[\u0400-\u04FF]/.test(sample)) return 'ru-RU';
  if (/[\u4E00-\u9FFF]/.test(sample)) {
    const hasTrad = TRADITIONAL_HINT_RE.test(sample);
    const hasSimp = SIMPLIFIED_HINT_RE.test(sample);
    if (hasTrad && !hasSimp) return 'zh-TW';
    const normUi = String(uiLocale || '').toLowerCase().replace('_', '-');
    if ((normUi === 'zh-tw' || normUi === 'zh-hk' || normUi === 'zh-mo') && !hasSimp) {
      return 'zh-TW';
    }
    return 'zh-CN';
  }
  if (/[äöüßÄÖÜ]/.test(sample)) return 'de-DE';
  if (/[éèêàçœùîôÉÈÊÀÇŒ]/.test(sample)) return 'fr-FR';
  if (/[ñ¿¡]/.test(sample)) return 'es-ES';
  if (/[ãõçÃÕ]/.test(sample)) return 'pt-BR';
  if (/[A-Za-z]/.test(sample)) return 'en-US';
  return '';
}

function formatTextMetric(ctx, text, langCode) {
  const s = String(text || '').trim();
  if (!s) return '';
  if (langCode === 'zh-CN' || langCode === 'zh-TW' || langCode === 'ja-JP' || /[\u4E00-\u9FFF\u3040-\u30FF]/.test(s)) {
    const charCount = s.replace(/\s+/g, '').length;
    return ctx.t('unitChars', { n: String(charCount) });
  }
  const words = s.split(/\s+/).filter(Boolean).length;
  return ctx.t('unitWords', { n: String(Math.max(1, words)) });
}

function isShortDictionaryQuery(text) {
  const s = String(text || '').trim();
  if (!s || s.length > 36 || s.indexOf('\n') !== -1) return false;
  if (/https?:\/\/|[\\\/:=@#{}()<>]/.test(s)) return false;
  if (/[\u4E00-\u9FFF]/.test(s)) {
    return s.length <= 6;
  }
  const parts = s.split(/\s+/).filter(Boolean);
  return parts.length >= 1 && parts.length <= 3;
}

function resolveEffectiveLang(ctx) {
  const ov = String(ctx.state.overrideLang || '').trim();
  if (ov && SUPPORTED_LANGS[ov]) return ov;
  const argLang = ctx.args && ctx.args.lang ? String(ctx.args.lang).trim() : '';
  if (argLang && SUPPORTED_LANGS[argLang]) return argLang;
  return normalizeLang(ctx.settings && ctx.settings.targetLang);
}

function resolveEffectiveTone(ctx) {
  const ov = String(ctx.state.overrideTone || '').trim();
  if (ov && SUPPORTED_TONES[ov]) return ov;
  const argTone = ctx.args && ctx.args.tone ? String(ctx.args.tone).trim() : '';
  if (argTone && SUPPORTED_TONES[argTone]) return argTone;
  return normalizeTone(ctx.settings && ctx.settings.toneStyle);
}

function formatOriginLabel(ctx, origin) {
  if (origin === 'clipboard' || origin === 'capsule') return ctx.t('originClipboard');
  if (origin === 'file') return ctx.t('originFile');
  if (origin === 'history') return ctx.t('originHistory');
  if (origin === 'rerun') return ctx.t('originRerun');
  return ctx.t('originDraft');
}

function buildResultMetaText(ctx) {
  const parts = [];
  const updated = String(ctx.state.aiUpdatedAt || '').trim();
  if (updated) parts.push(updated);
  const rawMeta = String(ctx.state.lastSourceMeta || '');
  if (rawMeta) {
    const segs = rawMeta.split('|');
    if (segs[0]) parts.push(formatOriginLabel(ctx, segs[0]));
    if (segs[1]) parts.push(ctx.t('charCount', { n: segs[1] }));
  }
  return parts.join(' · ');
}

function splitSentences(text) {
  const trimmed = String(text || '').trim();
  if (!trimmed) return [];
  const lines = trimmed.split(/\r?\n/).map((l) => l.trim()).filter(Boolean);
  if (lines.length > 1) return lines;
  const byPunct = trimmed
    .replace(/([。！？!?]+|\.(?=\s+[A-Z0-9\u4E00-\u9FFF]))/g, '$1\n')
    .split('\n')
    .map((s) => s.trim())
    .filter(Boolean);
  return byPunct.length > 0 ? byPunct : [trimmed];
}

function buildBilingualPairs(sourceText, resultText) {
  const srcList = splitSentences(sourceText);
  const dstList = splitSentences(resultText);
  if (srcList.length === 0 || dstList.length === 0) return [];
  if (srcList.length === dstList.length) {
    const out = [];
    for (let i = 0; i < srcList.length && i < 12; i++) {
      out.push({ id: `p${i}`, src: srcList[i], dst: dstList[i] });
    }
    return out;
  }
  if (srcList.length <= 2 || dstList.length <= 2) {
    return [{ id: 'p0', src: String(sourceText || '').trim(), dst: String(resultText || '').trim() }];
  }
  const pairs = [];
  const maxRows = Math.min(8, Math.max(srcList.length, dstList.length));
  for (let i = 0; i < maxRows; i++) {
    pairs.push({
      id: `p${i}`,
      src: srcList[i] || '',
      dst: dstList[i] || ''
    });
  }
  return pairs;
}

function tokenizeForDiff(text) {
  const tokens = [];
  const s = String(text || '');
  const re = /[A-Za-z0-9_'-]+|\s+|[\u4E00-\u9FFF\u3040-\u30FF\uAC00-\uD7AF]|./g;
  let m;
  while ((m = re.exec(s)) !== null) {
    tokens.push(m[0]);
    if (tokens.length >= 140) break;
  }
  return tokens;
}

function buildDiffSpans(sourceText, resultText) {
  const src = String(sourceText || '').trim();
  const dst = String(resultText || '').trim();
  if (!src || !dst) return [];

  const a = tokenizeForDiff(src);
  const b = tokenizeForDiff(dst);
  const n = a.length;
  const m = b.length;
  if (n === 0 || m === 0 || n * m > 19600) {
    return [
      { id: 'd0', text: src + '\n', color: 'danger', strikethrough: 'true', bold: 'false' },
      { id: 'd1', text: dst, color: 'success', strikethrough: 'false', bold: 'true' }
    ];
  }

  const dp = new Array((n + 1) * (m + 1)).fill(0);
  const cols = m + 1;
  for (let i = n - 1; i >= 0; i--) {
    for (let j = m - 1; j >= 0; j--) {
      if (a[i] === b[j]) {
        dp[i * cols + j] = 1 + dp[(i + 1) * cols + (j + 1)];
      } else {
        const down = dp[(i + 1) * cols + j];
        const right = dp[i * cols + (j + 1)];
        dp[i * cols + j] = down >= right ? down : right;
      }
    }
  }

  const rawOps = [];
  let i = 0;
  let j = 0;
  while (i < n && j < m) {
    if (a[i] === b[j]) {
      rawOps.push({ kind: 'same', text: a[i] });
      i++;
      j++;
    } else if (dp[(i + 1) * cols + j] >= dp[i * cols + (j + 1)]) {
      rawOps.push({ kind: 'del', text: a[i] });
      i++;
    } else {
      rawOps.push({ kind: 'add', text: b[j] });
      j++;
    }
  }
  while (i < n) rawOps.push({ kind: 'del', text: a[i++] });
  while (j < m) rawOps.push({ kind: 'add', text: b[j++] });

  const merged = [];
  for (let k = 0; k < rawOps.length; k++) {
    const op = rawOps[k];
    if (merged.length > 0 && merged[merged.length - 1].kind === op.kind) {
      merged[merged.length - 1].text += op.text;
    } else {
      merged.push({ kind: op.kind, text: op.text });
    }
  }

  return merged.map((seg, idx) => ({
    id: `s${idx}`,
    text: seg.text,
    color: seg.kind === 'del' ? 'danger' : (seg.kind === 'add' ? 'success' : 'default'),
    strikethrough: seg.kind === 'del' ? 'true' : 'false',
    bold: seg.kind === 'add' ? 'true' : 'false'
  }));
}

function parseTodosList(rawJsonStr, fallbackMarkdown) {
  if (rawJsonStr && rawJsonStr !== '[]') {
    try {
      const parsed = JSON.parse(rawJsonStr);
      if (Array.isArray(parsed)) {
        return parsed.filter((x) => x && typeof x.text === 'string' && x.text.trim());
      }
    } catch (_e) {
      // fallback to markdown parsing
    }
  }
  const lines = String(fallbackMarkdown || '').split(/\r?\n/);
  const list = [];
  for (let i = 0; i < lines.length && list.length < 15; i++) {
    const line = lines[i].trim();
    if (!line) continue;
    const cb = line.match(/^(?:[-*+]\s*)?\[([ xX])\]\s+(.+)$/);
    if (cb) {
      list.push({ id: `t${list.length}`, text: cb[2].trim(), done: cb[1].toLowerCase() === 'x' ? 'true' : 'false' });
      continue;
    }
    const bullet = line.match(/^(?:[-*+]|\d+\.)\s+(.+)$/);
    if (bullet && bullet[1].trim().length > 1) {
      list.push({ id: `t${list.length}`, text: bullet[1].trim(), done: 'false' });
    }
  }
  return list;
}

function computePrimaryViewLabel(ctx, resultMode, isDict) {
  if (resultMode === 'polish') return ctx.t('rvPrimaryPolish');
  if (resultMode === 'summarize') return ctx.t('rvPrimarySummary');
  if (resultMode === 'explain') return ctx.t('rvPrimaryExplain');
  if (isDict) return ctx.t('rvPrimaryDict');
  return ctx.t('rvPrimaryTrans');
}

const PREVIEW_SEEDS = {
  'zh-tw': [
    "Hi Emma, could we move Thursday's product launch review to 3:00 PM? I've attached the updated schedule for your reference.",
    'Emma 你好，週四的新品發表評審會可以調整到下午 3:00 嗎？最新排程表已隨信附上，供你參考。',
    '職場郵件',
    '「不知週四下午 3 點是否方便同步新品發表排程？」（更顯委婉客氣）',
    'zh-TW'
  ],
  zh: [
    "Hi Emma, could we move Thursday's product launch review to 3:00 PM? I've attached the updated schedule for your reference.",
    'Emma 你好，周四的新品发布评审会可以调整到下午 3:00 吗？最新排期表已随信附上，供你参考。',
    '职场邮件',
    '「不知周四下午 3 点是否方便同步新品发布排期？」（更显委婉客气）',
    'zh-CN'
  ],
  ja: [
    "Hi Emma, could we move Thursday's product launch review to 3:00 PM? I've attached the updated schedule.",
    'Emma さん、木曜日の新製品発表レビューを午後 3 時に変更できますでしょうか。更新したスケジュールを添付いたします。',
    'ビジネス',
    '「木曜 15 時へのご調整は可能でしょうか」（より丁寧な表現）',
    'ja-JP'
  ],
  fr: [
    "Hi Emma, could we move Thursday's product launch review to 3:00 PM? I've attached the updated schedule.",
    'Bonjour Emma, pourrions-nous décaler la revue de lancement produit de jeudi à 15 h 00 ? Voici le planning mis à jour.',
    'E-mail',
    '« Seriez-vous disponible jeudi à 15 h pour faire le point ? »',
    'fr-FR'
  ],
  en: [
    'Bonjour Claire, pourrions-nous décaler la réunion de lancement produit de jeudi à 15h00 ? Voici le planning mis à jour.',
    "Hi Claire, could we move Thursday's product launch meeting to 3:00 PM? Here is the updated schedule.",
    'Email',
    '"Would 3:00 PM on Thursday work for our launch review?"',
    'en-US'
  ]
};

function ensurePreviewSeed(ctx) {
  if (!Boolean(ctx && ctx.env && ctx.env.isPreview)) return;
  if (isTrue(ctx.state.previewSeeded)) return;
  if (String(ctx.state.draft || '').trim() || String(ctx.state.resultText || '').trim()) return;

  ctx.state.previewSeeded = 'true';
  const norm = String(resolveUiLocale(ctx) || 'en-US').toLowerCase().replace('_', '-');
  const seedKey = (norm === 'zh-tw' || norm === 'zh-hk' || norm === 'zh-mo')
    ? 'zh-tw'
    : (norm.indexOf('zh') === 0 ? 'zh' : (norm.indexOf('ja') === 0 ? 'ja' : (norm.indexOf('fr') === 0 ? 'fr' : 'en')));
  const [src, res, tag, insight, ovLang] = PREVIEW_SEEDS[seedKey];
  ctx.state.draft = src;
  ctx.state.lastSourceText = src;
  ctx.state.resultText = res;
  ctx.state.resultTag = tag;
  ctx.state.resultInsight = insight;
  ctx.state.overrideLang = ovLang;
  ctx.state.resultMode = 'translate';
  ctx.state.resultView = 'primary';
  ctx.state.isEditingSource = 'true';
}

function recompute(ctx) {
  const mode = normalizeMode(ctx.state.mode || (ctx.settings && ctx.settings.defaultMode));
  const meta = MODES[mode];
  const uiLocale = resolveUiLocale(ctx);

  const sourceLang = normalizeLang(ctx.state.sourceLang);
  if (ctx.state.sourceLang !== sourceLang) {
    ctx.state.sourceLang = sourceLang;
  }

  const effLang = resolveEffectiveLang(ctx);
  const tone = resolveEffectiveTone(ctx);
  const polishOutLang = normalizeOutLang(ctx.state.polishOutLang, 'keep');
  const otherOutLang = normalizeOutLang(ctx.state.otherOutLang, 'auto');

  const draftText = String(ctx.state.draft || '').trim();
  const refSample = draftText || String(ctx.state.lastSourceText || (ctx.env && ctx.env.clipboard && ctx.env.clipboard.text) || '').trim();
  const detectedCode = detectLanguageCode(refSample, uiLocale);
  const detectedShort = detectedCode ? getShortLangLabel(ctx, detectedCode) : '';

  ctx.computed.srcLangBtnText = sourceLang === 'auto'
    ? (detectedShort ? ctx.t('srcAutoDetected', { lang: detectedShort }) : ctx.t('srcAuto'))
    : getShortLangLabel(ctx, sourceLang);
  ctx.computed.srcAutoMenuLabel = detectedShort
    ? ctx.t('srcAutoMenuDetected', { lang: detectedShort })
    : ctx.t('srcAuto');
  ctx.computed.dstLangBtnText = getShortLangLabel(ctx, effLang);
  ctx.computed.polishOutBtnText = ctx.t('outLangLabel', { lang: translateOutLang(ctx, polishOutLang) });
  ctx.computed.polishStyleBtnText = ctx.t('polishStyleLabel', { style: translateToneLabel(ctx, tone) });
  ctx.computed.otherOutBtnText = ctx.t('outLangLabel', { lang: translateOutLang(ctx, otherOutLang) });

  for (const [k, code] of [['auto', 'auto'], ['zhCN', 'zh-CN'], ['zhTW', 'zh-TW'], ['enUS', 'en-US'], ['jaJP', 'ja-JP'], ['koKR', 'ko-KR'], ['frFR', 'fr-FR'], ['deDE', 'de-DE']]) {
    ctx.computed[`srcIcon_${k}`] = sourceLang === code ? CHECK_GLYPH : '';
  }
  for (const [k, code] of [['zhCN', 'zh-CN'], ['enUS', 'en-US'], ['auto', 'auto'], ['zhTW', 'zh-TW'], ['jaJP', 'ja-JP'], ['koKR', 'ko-KR'], ['frFR', 'fr-FR'], ['deDE', 'de-DE'], ['esES', 'es-ES'], ['ptBR', 'pt-BR'], ['ruRU', 'ru-RU'], ['arSA', 'ar-SA'], ['hiIN', 'hi-IN'], ['bnBD', 'bn-BD']]) {
    ctx.computed[`dstIcon_${k}`] = effLang === code ? CHECK_GLYPH : '';
  }
  for (const [k, code] of [['keep', 'keep'], ['zhCN', 'zh-CN'], ['enUS', 'en-US']]) {
    ctx.computed[`pOutIcon_${k}`] = polishOutLang === code ? CHECK_GLYPH : '';
  }
  for (const [k, code] of [['auto', 'auto'], ['keep', 'keep'], ['zhCN', 'zh-CN'], ['enUS', 'en-US']]) {
    ctx.computed[`oOutIcon_${k}`] = otherOutLang === code ? CHECK_GLYPH : '';
  }
  for (const t of ['natural', 'professional', 'concise', 'friendly', 'developer']) {
    ctx.computed[`toneIcon_${t}`] = tone === t ? CHECK_GLYPH : '';
  }

  const modeLabel = translateModeLabel(ctx, mode);
  ctx.computed.modeBadgeTone = meta.badgeTone;
  ctx.computed.modeLabel = modeLabel;
  ctx.computed.modePlaceholder = translateModePlaceholder(ctx, mode);
  ctx.computed.runBtnText = translateModeCta(ctx, mode);
  ctx.computed.clipDirectLabel = translateModeClipDirect(ctx, mode);
  ctx.computed.runningLabel = ctx.t('runningLabel', { mode: modeLabel });

  const autoCopy = isTrue(ctx.settings && ctx.settings.autoCopyResult) ||
    isTrue(ctx.args && ctx.args.autoCopy);
  const draftCode = detectLanguageCode(draftText, uiLocale);
  const draftMetric = formatTextMetric(ctx, draftText, draftCode);
  if (draftText && draftCode && draftMetric) {
    ctx.computed.composerHint = `${getShortLangLabel(ctx, draftCode)} · ${draftMetric} · Ctrl+Enter`;
  } else {
    ctx.computed.composerHint = autoCopy ? ctx.t('hintAutoCopy') : ctx.t('hintShortcut');
  }

  const clipText = String((ctx.env && ctx.env.clipboard && ctx.env.clipboard.text) || '').trim();
  const clipLangCode = detectLanguageCode(clipText, uiLocale);
  const clipMetric = formatTextMetric(ctx, clipText, clipLangCode);
  if (clipText && clipLangCode) {
    ctx.computed.clipBannerText = ctx.t('clipBannerFmt', {
      lang: getShortLangLabel(ctx, clipLangCode),
      metric: clipMetric
    });
  } else if (clipText && clipMetric) {
    ctx.computed.clipBannerText = ctx.t('clipGenericFmt', { metric: clipMetric });
  } else {
    ctx.computed.clipBannerText = '';
  }

  const resMode = normalizeMode(ctx.state.resultMode || mode);
  const resMeta = MODES[resMode];
  ctx.computed.resultBadgeTone = resMeta.badgeTone;
  ctx.computed.insightHeader = translateModeInsight(ctx, resMode);
  ctx.computed.resultMetaText = buildResultMetaText(ctx);

  const srcText = String(ctx.state.lastSourceText || ctx.state.draft || '');
  const resText = String(ctx.state.resultText || '');
  ctx.computed.sourceAnchorText = ctx.t('sourceAnchorPrefix', {
    text: singleLine(srcText, 44)
  });

  const hasDict = resMode === 'translate' && Boolean(String(ctx.state.dictPhonetic || ctx.state.dictPos || '').trim());
  ctx.computed.isDictResult = hasDict ? 'true' : 'false';
  const primaryLabel = computePrimaryViewLabel(ctx, resMode, hasDict);
  ctx.computed.rvPrimaryLabel = primaryLabel;
  ctx.computed.rvSecondaryLabel = resMode === 'polish'
    ? ctx.t('rvSecondaryDiff')
    : ctx.t('rvSecondaryBilingual');

  const tagStr = String(ctx.state.resultTag || '').trim();
  ctx.computed.resultHeaderBadge = tagStr ? `${primaryLabel} · ${tagStr}` : primaryLabel;

  if (resMode === 'translate' && !hasDict && resText && srcText) {
    ctx.computed.bilingualPairs = buildBilingualPairs(srcText, resText);
  } else {
    ctx.computed.bilingualPairs = [];
  }

  if (resMode === 'polish' && resText && srcText) {
    ctx.computed.diffSpans = buildDiffSpans(srcText, resText);
  } else {
    ctx.computed.diffSpans = [];
  }

  const todoList = resMode === 'todos' ? parseTodosList(ctx.state.todosJson, resText) : [];
  ctx.computed.todoItems = todoList;
  ctx.computed.todosHeader = ctx.t('todosCountHeader', { n: String(todoList.length) });
  ctx.computed.syncTodosLabel = isTrue(ctx.state.todosSynced)
    ? ctx.t('btnTodosSynced')
    : ctx.t('btnSyncTodos');

  ctx.computed.copyBtnLabel = isTrue(ctx.state.copiedJustNow) ? ctx.t('btnCopied') : ctx.t('btnCopy');
  ctx.computed.copyBtnIcon = isTrue(ctx.state.copiedJustNow) ? 'check' : 'copy';

  if (typeof ctx.computed.aiPrompt !== 'string') {
    ctx.computed.aiPrompt = '';
  }

  const rawItems = ctx.collections.get('items') || [];
  const scope = String(ctx.state.historyScope || 'all') === 'starred' ? 'starred' : 'all';
  const hMode = normalizeHistoryMode(ctx.state.historyMode);
  const query = String(ctx.state.historySearch || '').trim().toLowerCase();

  let starredCount = 0;
  const filtered = [];
  for (let i = 0; i < rawItems.length; i++) {
    const it = rawItems[i];
    const isStar = isTrue(it.starred);
    if (isStar) starredCount++;
    if (scope === 'starred' && !isStar) continue;

    const itemMode = normalizeMode(it.mode);
    if (hMode !== 'all' && itemMode !== hMode) continue;

    const hay = `${it.sourceText || ''} ${it.text || ''} ${it.tag || ''} ${it.insight || ''}`.toLowerCase();
    if (query && hay.indexOf(query) === -1) continue;

    const itemMeta = MODES[itemMode];
    const itemModeLabel = translateModeLabel(ctx, itemMode);
    filtered.push({
      id: it.id || '',
      sourceText: it.sourceText || '',
      sourcePreview: it.sourcePreview || singleLine(it.sourceText || '', 56),
      text: it.text || '',
      insight: it.insight || '',
      tag: it.tag || itemModeLabel,
      mode: itemMode,
      modeLabel: itemModeLabel,
      modeBadgeTone: itemMeta.badgeTone,
      starred: isStar ? 'true' : 'false',
      timeText: it.timeText || '',
      origin: it.origin || 'draft'
    });
  }

  ctx.computed.filteredHistory = filtered;
  ctx.computed.historyCountBadge = rawItems.length > 0
    ? `${filtered.length}/${rawItems.length} · ★${starredCount}`
    : '0';
  ctx.computed.historyFilterBtnText = hMode === 'all'
    ? ctx.t('filterAllModes')
    : translateModeLabel(ctx, hMode);
  ctx.computed.historyEmptyText = rawItems.length === 0
    ? ctx.t('emptyHistory')
    : ctx.t('emptyFilteredHistory');

  const latest = rawItems.length > 0 ? rawItems[0] : null;
  ctx.computed.capsuleTitle = rawItems.length > 0
    ? ctx.t('capTitleWithCount', { count: String(rawItems.length) })
    : ctx.t('widgetName');
  ctx.computed.capsuleSummary = singleLine(resText || (latest && latest.text) || ctx.t('capEmptySummary'), 44);
  ctx.computed.capsuleBadge = ctx.state.aiStatus === 'running'
    ? ctx.t('capRunning')
    : (rawItems.length > 0 ? String(rawItems.length) : getShortLangLabel(ctx, effLang));
}

function buildAiPrompt(ctx, mode, lang, tone, rawText) {
  const safeInput = String(rawText || '').replace(/<\/user_input>/gi, '[/user_input]');
  const uiLocale = resolveUiLocale(ctx);
  const sourceLang = normalizeLang(ctx.state.sourceLang);
  const sourcePrefix = sourceLang !== 'auto' && LANG_PROMPT_DESC[sourceLang]
    ? `Source language is explicitly specified as ${LANG_PROMPT_DESC[sourceLang]}. `
    : '';
  const langDesc = lang === 'auto'
    ? resolveAutoLangPromptDesc(uiLocale)
    : (LANG_PROMPT_DESC[lang] || resolveAutoLangPromptDesc(uiLocale));
  const toneDesc = TONE_PROMPT_DESC[tone] || TONE_PROMPT_DESC.natural;

  let taskRule = '';
  let insightRule = '';
  let extraSchema = '';
  if (mode === 'translate') {
    const isDict = isShortDictionaryQuery(safeInput);
    taskRule = isDict
      ? `${sourcePrefix}Task: Translate this concise word or phrase accurately as a dictionary entry. Target language: ${langDesc}. Style/Tone: ${toneDesc}.`
      : `${sourcePrefix}Task: Translate naturally and accurately. Target language: ${langDesc}. Style/Tone: ${toneDesc}. Preserve paragraph breaks and formatting.`;
    insightRule = `"insight": "1 compact line (${uiLocale}): 1-2 natural alternative phrasings for everyday or workplace communication (max 90 chars)."`;
    if (isDict) {
      extraSchema = '\n- "phonetic": IPA transcription (or "" if phrase).\n- "pos": concise part-of-speech tag like "adj." / "n." / "v." (or "").\n- "exampleSrc": 1 brief natural example sentence in the source language.\n- "exampleDst": translation of exampleSrc in the target language.';
    }
  } else if (mode === 'polish') {
    const polishOut = normalizeOutLang(ctx.state.polishOutLang, 'keep');
    const outInstruction = polishOut === 'keep'
      ? 'in the original language'
      : `and output in ${LANG_PROMPT_DESC[polishOut] || polishOut}`;
    taskRule = `Task: Rewrite & polish grammar, clarity, and phrasing ${outInstruction}. Style/Tone: ${toneDesc}. Return only the refined text cleanly in "result" without intro commentary.`;
    insightRule = `"insight": "1 compact line (${uiLocale}): concise summary of the key tone/clarity improvements made (max 90 chars)."`;
  } else if (mode === 'summarize') {
    const otherOut = normalizeOutLang(ctx.state.otherOutLang, 'auto');
    const outLangText = otherOut === 'keep'
      ? 'match input language'
      : (otherOut === 'auto' ? ('match input or ' + uiLocale) : (LANG_PROMPT_DESC[otherOut] || uiLocale));
    taskRule = `Task: Distill core takeaway and 2-4 crisp bullet points. Output language: ${outLangText}. Style/Tone: ${toneDesc}.`;
    insightRule = `"insight": "1 compact line (${uiLocale}): single bottom-line conclusion or next action (max 90 chars)."`;
  } else if (mode === 'todos') {
    const otherOut = normalizeOutLang(ctx.state.otherOutLang, 'auto');
    const outLangText = otherOut === 'keep'
      ? 'match input language'
      : (otherOut === 'auto' ? uiLocale : (LANG_PROMPT_DESC[otherOut] || uiLocale));
    taskRule = `Task: Extract concrete actionable tasks as Markdown checkboxes (- [ ] ...) with owners or deadlines if mentioned. Output language: ${outLangText}. Style/Tone: ${toneDesc}.`;
    insightRule = `"insight": "1 compact line (${uiLocale}): highest-priority item summary (max 90 chars)."`;
  } else {
    const otherOut = normalizeOutLang(ctx.state.otherOutLang, 'auto');
    const outLangText = otherOut === 'keep'
      ? 'match input language'
      : (otherOut === 'auto' ? uiLocale : (LANG_PROMPT_DESC[otherOut] || uiLocale));
    taskRule = `Task: Explain meaning, context, or terminology in clear, plain everyday language. Output language: ${outLangText}. Style/Tone: ${toneDesc}.`;
    insightRule = `"insight": "1 compact line (${uiLocale}): helpful usage note or key takeaway (max 90 chars)."`;
  }

  return [
    `TargetLang=${lang} (${langDesc}); Tone=${tone} (${toneDesc}).`,
    taskRule,
    'Return ONLY a JSON object with exact keys "result", "tag", "insight":',
    '- "result": clean answer (no wrapper labels).',
    `- "tag": ultra-short 2-4 word badge label in ${uiLocale} or target language (max 16 chars).`,
    `- ${insightRule}${extraSchema}`,
    `<user_input>\n${safeInput}\n</user_input>`
  ].join('\n');
}

function tryParseJsonPayload(rawText) {
  const trimmed = String(rawText || '').trim();
  if (!trimmed) return null;

  const candidates = [trimmed];
  const fenceMatch = trimmed.match(/^```(?:json)?\s*([\s\S]*?)\s*```$/i);
  if (fenceMatch && fenceMatch[1]) {
    candidates.push(fenceMatch[1].trim());
  }

  for (let i = 0; i < candidates.length; i++) {
    const c = candidates[i];
    if (!c || (c[0] !== '{' && c[0] !== '[')) continue;
    try {
      const parsed = JSON.parse(c);
      if (parsed && typeof parsed === 'object') return parsed;
    } catch (_e) {
      // ignore parse failures
    }
  }
  return null;
}

function formatArrayReply(arr) {
  if (!Array.isArray(arr) || arr.length === 0) {
    return { result: '', tag: '', insight: '', phonetic: '', pos: '', exampleSrc: '', exampleDst: '' };
  }
  if (arr.length === 1 && arr[0] && typeof arr[0] === 'object' && !Array.isArray(arr[0])) {
    const first = arr[0];
    const direct = first.result || first.output || first.translation || first.summary || first.explanation || first.text || '';
    if (direct) {
      return {
        result: sanitizeUntrustedEcho(direct),
        tag: sanitizeUntrustedEcho(first.tag || first.label || ''),
        insight: sanitizeUntrustedEcho(first.insight || first.note || first.tip || ''),
        phonetic: sanitizeUntrustedEcho(first.phonetic || ''),
        pos: sanitizeUntrustedEcho(first.pos || ''),
        exampleSrc: sanitizeUntrustedEcho(first.exampleSrc || ''),
        exampleDst: sanitizeUntrustedEcho(first.exampleDst || '')
      };
    }
  }
  const lines = [];
  for (let i = 0; i < arr.length; i++) {
    const item = arr[i];
    if (typeof item === 'string') {
      const s = sanitizeUntrustedEcho(item);
      if (s) lines.push(`- ${s}`);
    } else if (item && typeof item === 'object') {
      const title = sanitizeUntrustedEcho(item.result || item.text || item.title || item.task || item.name || item.summary || '');
      if (!title) continue;
      if (typeof item.done === 'boolean') {
        lines.push(`- [${item.done ? 'x' : ' '}] ${title}`);
      } else {
        lines.push(`- ${title}`);
      }
    }
  }
  return { result: lines.join('\n'), tag: '', insight: '', phonetic: '', pos: '', exampleSrc: '', exampleDst: '' };
}

function tryExtractStructuredResult(res) {
  let payload = (res && res.data && typeof res.data === 'object') ? res.data : null;
  if (!payload && res && typeof res.text === 'string' && res.text.trim()) {
    payload = tryParseJsonPayload(res.text);
  }
  if (Array.isArray(payload)) {
    return formatArrayReply(payload);
  }
  if (payload && typeof payload === 'object') {
    const textVal = payload.result || payload.output || payload.translation || payload.summary || payload.explanation || payload.text || '';
    const formatted = Array.isArray(textVal) ? formatArrayReply(textVal).result : sanitizeUntrustedEcho(textVal);
    if (formatted) {
      return {
        result: formatted,
        tag: sanitizeUntrustedEcho(payload.tag || payload.label || ''),
        insight: sanitizeUntrustedEcho(payload.insight || payload.note || payload.tip || ''),
        phonetic: sanitizeUntrustedEcho(payload.phonetic || ''),
        pos: sanitizeUntrustedEcho(payload.pos || ''),
        exampleSrc: sanitizeUntrustedEcho(payload.exampleSrc || ''),
        exampleDst: sanitizeUntrustedEcho(payload.exampleDst || '')
      };
    }
  }
  if (res && typeof res.text === 'string' && res.text.trim()) {
    return {
      result: sanitizeUntrustedEcho(res.text),
      tag: '',
      insight: '',
      phonetic: '',
      pos: '',
      exampleSrc: '',
      exampleDst: ''
    };
  }
  return null;
}

function extractDropOrPickPayload(ctx) {
  const files = (ctx.event && Array.isArray(ctx.event.files) && ctx.event.files.length > 0)
    ? ctx.event.files
    : (ctx.event && ctx.event.drop ? [ctx.event.drop] : []);

  if (files.length > 0) {
    const parts = [];
    const names = [];
    for (let i = 0; i < files.length && i < 3; i++) {
      const f = files[i];
      const txt = String((f && f.text) || '').trim();
      if (!txt) continue;
      names.push(f.name || `file-${i + 1}`);
      parts.push(files.length > 1 ? `[${f.name || 'File'}]\n${txt}` : txt);
    }

    if (parts.length === 0) {
      const firstName = (files[0] && files[0].name) ? files[0].name : '';
      return { ok: false, kind: 'file', text: '', name: firstName };
    }

    const joined = parts.join('\n\n');
    const clipped = joined.length > 12000 ? joined.slice(0, 12000) : joined;
    return { ok: true, kind: 'file', text: clipped, name: names.join(', ') };
  }

  const droppedText = String((ctx.event && (ctx.event.text || ctx.event.value)) || '').trim();
  if (droppedText) {
    const clipped = droppedText.length > 12000 ? droppedText.slice(0, 12000) : droppedText;
    return { ok: true, kind: 'text', text: clipped, name: '' };
  }

  return { ok: false, kind: 'none', text: '', name: '' };
}

function ingestDropOrPick(ctx) {
  const payload = extractDropOrPickPayload(ctx);
  if (!payload.ok) {
    if (payload.kind === 'file') {
      ctx.ui.toast(ctx.t('toastFileUnreadable', { name: payload.name }), 'warning');
      recompute(ctx);
    }
    return;
  }

  ctx.state.draft = payload.text;
  ctx.state.isEditingSource = 'true';
  ctx.state.activeTab = 'workspace';
  ctx.state.aiError = '';
  if (ctx.state.aiStatus === 'error') ctx.state.aiStatus = 'idle';
  if (payload.kind === 'file') {
    ctx.ui.toast(ctx.t('toastFileLoaded', { name: payload.name, count: String(payload.text.length) }), 'info');
  } else {
    ctx.ui.toast(ctx.t('toastTextDropped'), 'info');
  }
  recompute(ctx);
}

function prepareAiRun(ctx, clippedText, origin) {
  const mode = normalizeMode(ctx.state.mode || (ctx.settings && ctx.settings.defaultMode));
  const lang = resolveEffectiveLang(ctx);
  const tone = resolveEffectiveTone(ctx);

  ctx.state.pendingSourceText = clippedText;
  ctx.state.pendingOrigin = origin;
  ctx.state.copiedJustNow = 'false';
  ctx.state.aiError = '';
  recompute(ctx);
  ctx.computed.aiPrompt = buildAiPrompt(ctx, mode, lang, tone, clippedText);
}

function tryAutoRerun(ctx) {
  const hasResult = Boolean(String(ctx.state.resultText || '').trim());
  const src = String(ctx.state.lastSourceText || '').trim();
  const isEditing = isTrue(ctx.state.isEditingSource);
  const isRunning = String(ctx.state.aiStatus || '') === 'running';
  if (!hasResult || !src || isEditing || isRunning) {
    return;
  }
  const activeMode = normalizeMode(ctx.state.mode || (ctx.settings && ctx.settings.defaultMode));
  const resMode = normalizeMode(ctx.state.resultMode || activeMode);
  if (activeMode !== resMode) {
    return;
  }
  prepareAiRun(ctx, src.length > 12000 ? src.slice(0, 12000) : src, 'rerun');
  ctx.dispatch([
    {
      type: 'ai.run',
      jobKey: 'ai-inbox-task',
      onComplete: 'onAiDone',
      prompt: '${$computed.aiPrompt}'
    }
  ]);
}

let _lastSyncedSettings = null;

function readSettingsDefaults(ctx) {
  const s = (ctx && ctx.settings) || {};
  return {
    defaultMode: normalizeMode(s.defaultMode),
    targetLang: normalizeLang(s.targetLang),
    toneStyle: normalizeTone(s.toneStyle)
  };
}

function syncModeTransition(ctx) {
  const nextMode = normalizeMode(ctx.state.mode || (ctx.settings && ctx.settings.defaultMode));
  const resMode = normalizeMode(ctx.state.resultMode || '');
  if (String(ctx.state.resultText || '').trim()) {
    if (nextMode === resMode && !isTrue(ctx.state.previewSeeded)) {
      ctx.state.isEditingSource = 'false';
      ctx.state.resultView = 'primary';
    } else {
      if (!String(ctx.state.draft || '').trim() && String(ctx.state.lastSourceText || '').trim()) {
        ctx.state.draft = String(ctx.state.lastSourceText);
      }
      ctx.state.isEditingSource = 'true';
    }
  }
}

export function onInit(ctx) {
  const defs = readSettingsDefaults(ctx);
  _lastSyncedSettings = defs;
  if (!String(ctx.state.mode || '').trim()) {
    ctx.state.mode = defs.defaultMode;
  }
  if (!String(ctx.state.sourceLang || '').trim()) {
    ctx.state.sourceLang = 'auto';
  }
  if (!String(ctx.state.polishOutLang || '').trim()) {
    ctx.state.polishOutLang = 'keep';
  }
  if (!String(ctx.state.otherOutLang || '').trim()) {
    ctx.state.otherOutLang = 'auto';
  }
  ensurePreviewSeed(ctx);
  recompute(ctx);
}

export function onSettingsChange(ctx) {
  const defs = readSettingsDefaults(ctx);
  const prev = _lastSyncedSettings;
  _lastSyncedSettings = defs;

  if (!prev || defs.defaultMode !== prev.defaultMode || !String(ctx.state.mode || '').trim()) {
    ctx.state.mode = defs.defaultMode;
    syncModeTransition(ctx);
  }
  if (!prev || defs.targetLang !== prev.targetLang) {
    ctx.state.overrideLang = '';
  }
  if (!prev || defs.toneStyle !== prev.toneStyle) {
    ctx.state.overrideTone = '';
  }
  recompute(ctx);
  tryAutoRerun(ctx);
}

export function selectMode(ctx) {
  ctx.state.mode = normalizeMode(ctx.args && ctx.args.mode);
  syncModeTransition(ctx);
  recompute(ctx);
}

export function onTabChanged(ctx) {
  recompute(ctx);
}

export function setSourceLang(ctx) {
  const val = String((ctx.args && ctx.args.lang) || (ctx.event && ctx.event.arg) || '').trim();
  ctx.state.sourceLang = SUPPORTED_LANGS[val] ? val : 'auto';
  recompute(ctx);
  tryAutoRerun(ctx);
}

export function setOverrideLang(ctx) {
  const val = String((ctx.args && ctx.args.lang) || (ctx.event && ctx.event.arg) || '').trim();
  ctx.state.overrideLang = SUPPORTED_LANGS[val] ? val : '';
  recompute(ctx);
  tryAutoRerun(ctx);
}

export function swapLanguages(ctx) {
  const uiLocale = resolveUiLocale(ctx);
  const refSample = String(ctx.state.draft || ctx.state.lastSourceText || (ctx.env && ctx.env.clipboard && ctx.env.clipboard.text) || '').trim();
  const detected = detectLanguageCode(refSample, uiLocale);

  const curSrc = normalizeLang(ctx.state.sourceLang);
  const curDst = resolveEffectiveLang(ctx);

  let resolvedSrc = curSrc;
  if (resolvedSrc === 'auto') {
    resolvedSrc = detected || (String(uiLocale).toLowerCase().indexOf('zh') === 0 ? 'en-US' : 'zh-CN');
  }

  let resolvedDst = curDst;
  if (resolvedDst === 'auto') {
    resolvedDst = resolvedSrc.indexOf('zh') === 0
      ? 'en-US'
      : (String(uiLocale).toLowerCase() === 'zh-tw' ? 'zh-TW' : 'zh-CN');
  }

  if (resolvedSrc === resolvedDst) {
    resolvedDst = resolvedSrc.indexOf('zh') === 0 ? 'en-US' : 'zh-CN';
  }

  ctx.state.sourceLang = SUPPORTED_LANGS[resolvedDst] ? resolvedDst : 'auto';
  ctx.state.overrideLang = SUPPORTED_LANGS[resolvedSrc] ? resolvedSrc : 'en-US';
  recompute(ctx);
  tryAutoRerun(ctx);
}

export function setPolishOutLang(ctx) {
  const val = String((ctx.args && ctx.args.lang) || (ctx.event && ctx.event.arg) || 'keep').trim();
  ctx.state.polishOutLang = SUPPORTED_OUT_LANGS[val] ? val : 'keep';
  recompute(ctx);
  tryAutoRerun(ctx);
}

export function setOverrideTone(ctx) {
  const val = String((ctx.args && ctx.args.tone) || (ctx.event && ctx.event.arg) || '').trim().toLowerCase();
  ctx.state.overrideTone = SUPPORTED_TONES[val] ? val : '';
  recompute(ctx);
  tryAutoRerun(ctx);
}

export function setOtherOutLang(ctx) {
  const val = String((ctx.args && ctx.args.lang) || (ctx.event && ctx.event.arg) || 'auto').trim();
  ctx.state.otherOutLang = SUPPORTED_OUT_LANGS[val] ? val : 'auto';
  recompute(ctx);
  tryAutoRerun(ctx);
}

export function setResultView(ctx) {
  const val = String((ctx.args && ctx.args.view) || (ctx.event && ctx.event.value) || 'primary').trim();
  ctx.state.resultView = (val === 'secondary' || val === 'source') ? val : 'primary';
  recompute(ctx);
}

export function toggleSecondaryView(ctx) {
  ctx.state.resultView = ctx.state.resultView === 'secondary' ? 'primary' : 'secondary';
  recompute(ctx);
}

export function openSourceEditor(ctx) {
  const src = String(ctx.state.lastSourceText || '').trim();
  if (!String(ctx.state.draft || '').trim() && src) {
    ctx.state.draft = src;
  }
  ctx.state.isEditingSource = 'true';
  ctx.state.activeTab = 'workspace';
  recompute(ctx);
}

export function cancelSourceEdit(ctx) {
  if (String(ctx.state.resultText || '').trim()) {
    ctx.state.isEditingSource = 'false';
  }
  recompute(ctx);
}

export function fillClipboard(ctx) {
  const raw = String((ctx.args && ctx.args.text) || (ctx.env && ctx.env.clipboard && ctx.env.clipboard.text) || '').trim();
  if (!raw) {
    ctx.ui.toast(ctx.t('toastEmptyClip'), 'warning');
    recompute(ctx);
    return;
  }
  ctx.state.draft = raw.length > 12000 ? raw.slice(0, 12000) : raw;
  ctx.state.isEditingSource = 'true';
  ctx.state.activeTab = 'workspace';
  ctx.state.aiError = '';
  if (ctx.state.aiStatus === 'error') ctx.state.aiStatus = 'idle';
  ctx.ui.toast(ctx.t('toastClipFilled'), 'info');
  recompute(ctx);
}

export function clearDraft(ctx) {
  ctx.state.draft = '';
  if (isTrue(ctx.state.previewSeeded)) {
    ctx.state.previewSeeded = 'false';
    ctx.state.resultText = '';
    ctx.state.resultTag = '';
    ctx.state.resultInsight = '';
  }
  ctx.state.aiError = '';
  if (ctx.state.aiStatus === 'error' || ctx.state.aiStatus === 'cancelled') {
    ctx.state.aiStatus = 'idle';
  }
  recompute(ctx);
}

export function prepareDraftRun(ctx) {
  const raw = String(ctx.state.draft || '').trim();
  if (!raw) {
    ctx.state.pendingSourceText = '';
    ctx.ui.toast(ctx.t('toastEmptyInput'), 'warning');
    recompute(ctx);
    return;
  }
  const clipped = raw.length > 12000 ? raw.slice(0, 12000) : raw;
  ctx.state.previewSeeded = 'false';
  ctx.state.isEditingSource = 'false';
  prepareAiRun(ctx, clipped, 'draft');
}

export function prepareClipboardRun(ctx) {
  const raw = String((ctx.args && ctx.args.text) || (ctx.env && ctx.env.clipboard && ctx.env.clipboard.text) || '').trim();
  if (!raw) {
    ctx.state.pendingSourceText = '';
    ctx.ui.toast(ctx.t('toastEmptyClip'), 'warning');
    recompute(ctx);
    return;
  }
  const clipped = raw.length > 12000 ? raw.slice(0, 12000) : raw;
  const srcOrigin = (ctx.args && ctx.args.fromCapsule === 'true') ? 'capsule' : 'clipboard';
  ctx.state.previewSeeded = 'false';
  ctx.state.activeTab = 'workspace';
  ctx.state.isEditingSource = 'false';
  prepareAiRun(ctx, clipped, srcOrigin);
}

export function prepareDropRun(ctx) {
  const payload = extractDropOrPickPayload(ctx);
  if (!payload.ok) {
    ctx.state.pendingSourceText = '';
    if (payload.kind === 'file') {
      ctx.ui.toast(ctx.t('toastFileUnreadable', { name: payload.name }), 'warning');
    } else {
      ctx.ui.toast(ctx.t('toastEmptyInput'), 'warning');
    }
    recompute(ctx);
    return;
  }

  ctx.state.previewSeeded = 'false';
  ctx.state.activeTab = 'workspace';
  ctx.state.isEditingSource = 'false';
  prepareAiRun(ctx, payload.text, payload.kind === 'file' ? 'file' : 'draft');
}

export function onAiDone(ctx, res) {
  if (res && (res.cancelled === true || res.cancelled === 'true')) {
    ctx.state.pendingSourceText = '';
    ctx.state.aiStatus = 'idle';
    ctx.state.aiError = '';
    recompute(ctx);
    return;
  }

  const srcText = String(ctx.state.pendingSourceText || ctx.state.draft || ctx.state.lastSourceText || '').trim();
  const origin = String(ctx.state.pendingOrigin || 'draft');
  ctx.state.pendingSourceText = '';

  if (!res || !res.ok) {
    ctx.state.aiStatus = 'error';
    if (res && res.error) {
      ctx.state.aiError = String(res.error);
    }
    ctx.state.isEditingSource = 'true';
    recompute(ctx);
    return;
  }

  const parsed = tryExtractStructuredResult(res);
  const resultText = parsed ? parsed.result : sanitizeUntrustedEcho(res.text || '');
  let resultTag = parsed ? parsed.tag : '';
  let resultInsight = parsed ? parsed.insight : '';

  const mode = normalizeMode(ctx.state.mode || (ctx.settings && ctx.settings.defaultMode));
  if (resultTag.length > 18) resultTag = resultTag.slice(0, 18);
  if (resultInsight.length > 180) resultInsight = resultInsight.slice(0, 179) + '…';

  ctx.state.aiStatus = 'idle';
  ctx.state.aiError = '';
  ctx.state.resultText = resultText;
  ctx.state.resultTag = resultTag;
  ctx.state.resultInsight = resultInsight;
  ctx.state.resultMode = mode;
  ctx.state.resultView = 'primary';
  ctx.state.isEditingSource = 'false';
  ctx.state.copiedJustNow = 'false';
  ctx.state.todosSynced = 'false';
  ctx.state.lastSourceText = srcText.slice(0, 8000);
  ctx.state.lastSourceMeta = `${origin}|${srcText.length}`;

  if (mode === 'translate' && parsed && (parsed.phonetic || parsed.pos) && isShortDictionaryQuery(srcText)) {
    ctx.state.dictWord = srcText;
    ctx.state.dictPhonetic = parsed.phonetic || '';
    ctx.state.dictPos = parsed.pos || '';
    const exParts = [];
    if (parsed.exampleSrc) exParts.push(parsed.exampleSrc);
    if (parsed.exampleDst) exParts.push(parsed.exampleDst);
    ctx.state.dictExample = exParts.join('\n');
  } else {
    ctx.state.dictWord = '';
    ctx.state.dictPhonetic = '';
    ctx.state.dictPos = '';
    ctx.state.dictExample = '';
  }

  if (mode === 'todos' && resultText) {
    const todoList = parseTodosList('', resultText);
    ctx.state.todosJson = JSON.stringify(todoList);
  } else {
    ctx.state.todosJson = '[]';
  }

  if (origin === 'draft' && resultText) {
    ctx.state.draft = '';
  }

  if (resultText) {
    const timeText = (ctx.env && ctx.env.time) ? String(ctx.env.time).slice(0, 5) : '';
    const added = ctx.collections.prepend('items', {
      sourceText: srcText.slice(0, 8000),
      sourcePreview: singleLine(srcText, 64),
      text: resultText,
      insight: resultInsight,
      tag: resultTag || translateModeLabel(ctx, mode),
      mode: mode,
      starred: 'false',
      timeText: timeText,
      origin: origin
    });
    ctx.state.resultId = added ? added.id : '';
    ctx.state.resultStarred = 'false';

    if (origin === 'capsule' || isTrue(ctx.settings && ctx.settings.autoCopyResult)) {
      ctx.ui.copy(resultText);
      ctx.state.copiedJustNow = 'true';
      ctx.ui.toast(ctx.t('toastDoneCopied'), 'success');
    }

    if (isTrue(ctx.settings && ctx.settings.soundCue)) {
      ctx.dispatch([{ type: 'audio.cue', value: 'soft' }]);
    }
  }

  recompute(ctx);
}

export function onFileLoaded(ctx) {
  ingestDropOrPick(ctx);
}

export function chainResultToDraft(ctx) {
  const txt = String(ctx.state.resultText || '').trim();
  if (!txt) return;
  ctx.state.draft = txt.length > 12000 ? txt.slice(0, 12000) : txt;
  ctx.state.isEditingSource = 'true';
  ctx.state.activeTab = 'workspace';
  ctx.ui.toast(ctx.t('toastChained'), 'info');
  recompute(ctx);
}

export function copyResultWithFeedback(ctx) {
  const txt = String(ctx.state.resultText || '').trim();
  if (!txt) return;
  ctx.ui.copy(txt);
  ctx.state.copiedJustNow = 'true';
  ctx.ui.toast(ctx.t('toastCopied'), 'info');
  recompute(ctx);
}

export function toggleCurrentStar(ctx) {
  const nextVal = isTrue(ctx.state.resultStarred) ? 'false' : 'true';
  ctx.state.resultStarred = nextVal;
  const activeId = String(ctx.state.resultId || '').trim();
  if (activeId) {
    ctx.collections.update('items', activeId, { starred: nextVal });
  }
  recompute(ctx);
}

export function toggleExtractedTodo(ctx) {
  const targetId = String((ctx.args && ctx.args.id) || (ctx.event && ctx.event.item && ctx.event.item.id) || '').trim();
  if (!targetId) return;
  const list = parseTodosList(ctx.state.todosJson, ctx.state.resultText);
  let mutated = false;
  for (let i = 0; i < list.length; i++) {
    if (list[i].id === targetId) {
      list[i].done = isTrue(list[i].done) ? 'false' : 'true';
      mutated = true;
      break;
    }
  }
  if (mutated) {
    ctx.state.todosJson = JSON.stringify(list);
    ctx.state.todosSynced = 'false';
  }
  recompute(ctx);
}

export function syncTodosToDeskBox(ctx) {
  const list = parseTodosList(ctx.state.todosJson, ctx.state.resultText);
  const pending = list.filter((it) => !isTrue(it.done) && String(it.text || '').trim());
  const targets = pending.length > 0 ? pending : list.filter((it) => String(it.text || '').trim());
  if (targets.length === 0) {
    ctx.ui.toast(ctx.t('toastTodosNone'), 'warning');
    recompute(ctx);
    return;
  }

  const actions = [];
  for (let i = 0; i < targets.length && i < 12; i++) {
    actions.push({ type: 'todo.add', title: String(targets[i].text).trim() });
  }
  ctx.dispatch(actions);
  ctx.state.todosSynced = 'true';
  ctx.ui.toast(ctx.t('toastTodosSynced', { count: String(actions.length) }), 'success');
  recompute(ctx);
}

export function toggleItemStar(ctx) {
  const id = String((ctx.args && ctx.args.id) || '').trim();
  if (!id) return;
  const items = ctx.collections.get('items') || [];
  let nextVal = 'true';
  for (let i = 0; i < items.length; i++) {
    if (items[i].id === id) {
      nextVal = isTrue(items[i].starred) ? 'false' : 'true';
      break;
    }
  }
  ctx.collections.update('items', id, { starred: nextVal });
  if (id === String(ctx.state.resultId || '').trim()) {
    ctx.state.resultStarred = nextVal;
  }
  recompute(ctx);
}

export function loadHistoryItem(ctx) {
  const id = String((ctx.args && ctx.args.id) || '').trim();
  if (!id) return;
  const items = ctx.collections.get('items') || [];
  let found = null;
  for (let i = 0; i < items.length; i++) {
    if (items[i].id === id) {
      found = items[i];
      break;
    }
  }
  if (!found) return;

  const restSource = String(found.sourceText || found.sourcePreview || '');
  const restoredMode = normalizeMode(found.mode);
  ctx.state.previewSeeded = 'false';
  ctx.state.draft = restSource;
  ctx.state.lastSourceText = restSource;
  ctx.state.lastSourceMeta = `history|${restSource.length}`;
  ctx.state.resultText = String(found.text || '');
  ctx.state.resultInsight = String(found.insight || '');
  ctx.state.resultTag = String(found.tag || '');
  ctx.state.resultMode = restoredMode;
  ctx.state.resultId = found.id || '';
  ctx.state.resultStarred = isTrue(found.starred) ? 'true' : 'false';
  ctx.state.mode = restoredMode;
  ctx.state.resultView = 'primary';
  ctx.state.isEditingSource = 'false';
  ctx.state.dictWord = '';
  ctx.state.dictPhonetic = '';
  ctx.state.dictPos = '';
  ctx.state.dictExample = '';
  ctx.state.todosJson = restoredMode === 'todos' ? JSON.stringify(parseTodosList('', String(found.text || ''))) : '[]';
  ctx.state.todosSynced = 'false';
  ctx.state.copiedJustNow = 'false';
  ctx.state.activeTab = 'workspace';
  ctx.state.aiStatus = 'idle';
  ctx.state.aiError = '';
  ctx.ui.toast(ctx.t('toastRestored'), 'info');
  recompute(ctx);
}

export function removeHistoryItem(ctx) {
  const id = String((ctx.args && ctx.args.id) || '').trim();
  if (!id) return;
  ctx.collections.remove('items', id);
  if (id === String(ctx.state.resultId || '').trim()) {
    ctx.state.resultId = '';
    ctx.state.resultStarred = 'false';
  }
  recompute(ctx);
}

export function setHistoryScope(ctx) {
  const s = String((ctx.args && ctx.args.scope) || 'all').trim();
  ctx.state.historyScope = s === 'starred' ? 'starred' : 'all';
  recompute(ctx);
}

export function applyHistoryFilter(ctx) {
  recompute(ctx);
}

export function setHistoryMode(ctx) {
  const val = String((ctx.args && ctx.args.mode) || 'all').trim().toLowerCase();
  ctx.state.historyMode = normalizeHistoryMode(val);
  recompute(ctx);
}

export function clearHistorySearch(ctx) {
  ctx.state.historySearch = '';
  recompute(ctx);
}

export function clearHistory(ctx) {
  const scope = String((ctx.args && ctx.args.scope) || 'unstarred').trim().toLowerCase();
  if (scope === 'all') {
    ctx.collections.clear('items');
    ctx.state.resultId = '';
    ctx.state.resultStarred = 'false';
    ctx.ui.toast(ctx.t('toastHistoryCleared'), 'info');
    recompute(ctx);
    return;
  }
  const removed = ctx.collections.remove('items', (it) => !isTrue(it.starred));
  const remaining = ctx.collections.get('items') || [];
  const activeId = String(ctx.state.resultId || '').trim();
  if (activeId && !remaining.some((it) => it && it.id === activeId)) {
    ctx.state.resultId = '';
    ctx.state.resultStarred = 'false';
  }
  if (removed > 0) {
    ctx.ui.toast(ctx.t('toastUnstarredCleared', { count: String(removed) }), 'info');
  } else if (remaining.length > 0) {
    ctx.ui.toast(ctx.t('toastOnlyStarredLeft'), 'info');
  }
  recompute(ctx);
}
