function buildDefaultTiles(ctx) {
  const tagLaunch = ctx.t('tagLaunch');
  const tagOpen = ctx.t('tagOpen');
  const tagCopy = ctx.t('tagCopy');
  return [
    {
      id: 'd-snip',
      group: 'daily',
      kind: 'open',
      icon: '✂️',
      title: ctx.t('dSnipTitle'),
      subtitle: ctx.t('dSnipSub'),
      tag: tagLaunch,
      tone: 'cyan',
      body: 'ms-screenclip:',
    },
    {
      id: 'd-dl',
      group: 'daily',
      kind: 'open',
      icon: '📂',
      title: ctx.t('dDlTitle'),
      subtitle: ctx.t('dDlSub'),
      tag: tagOpen,
      tone: 'accent',
      body: 'downloads',
    },
    {
      id: 'd-calc',
      group: 'daily',
      kind: 'open',
      icon: '🧮',
      title: ctx.t('dCalcTitle'),
      subtitle: ctx.t('dCalcSub'),
      tag: tagLaunch,
      tone: 'emerald',
      body: 'ms-calculator:',
    },
    {
      id: 'd-docs',
      group: 'daily',
      kind: 'open',
      icon: '📄',
      title: ctx.t('dDocsTitle'),
      subtitle: ctx.t('dDocsSub'),
      tag: tagOpen,
      tone: 'violet',
      body: 'documents',
    },
    {
      id: 'd-tax',
      group: 'daily',
      kind: 'copy',
      icon: '🧾',
      title: ctx.t('dTaxTitle'),
      subtitle: ctx.t('dTaxSub'),
      tag: tagCopy,
      tone: 'warning',
      body: ctx.t('dTaxBody'),
    },
    {
      id: 'd-mail',
      group: 'daily',
      kind: 'copy',
      icon: '✉️',
      title: ctx.t('dMailTitle'),
      subtitle: ctx.t('dMailSub'),
      tag: tagCopy,
      tone: 'subtle',
      body: 'contact@example.com',
    },
    {
      id: 'p-1',
      group: 'phrases',
      kind: 'copy',
      icon: '🧾',
      title: ctx.t('p1Title'),
      subtitle: ctx.t('p1Sub'),
      tag: tagCopy,
      tone: 'warning',
      body: ctx.t('p1Body'),
    },
    {
      id: 'p-2',
      group: 'phrases',
      kind: 'copy',
      icon: '📍',
      title: ctx.t('p2Title'),
      subtitle: ctx.t('p2Sub'),
      tag: tagCopy,
      tone: 'emerald',
      body: ctx.t('p2Body'),
    },
    {
      id: 'p-3',
      group: 'phrases',
      kind: 'copy',
      icon: '💬',
      title: ctx.t('p3Title'),
      subtitle: ctx.t('p3Sub'),
      tag: tagCopy,
      tone: 'cyan',
      body: ctx.t('p3Body'),
    },
    {
      id: 'p-4',
      group: 'phrases',
      kind: 'copy',
      icon: '📹',
      title: ctx.t('p4Title'),
      subtitle: ctx.t('p4Sub'),
      tag: tagCopy,
      tone: 'accent',
      body: ctx.t('p4Body'),
    },
    {
      id: 'p-5',
      group: 'phrases',
      kind: 'copy',
      icon: '📱',
      title: ctx.t('p5Title'),
      subtitle: ctx.t('p5Sub'),
      tag: tagCopy,
      tone: 'violet',
      body: ctx.t('p5Body'),
    },
    {
      id: 'p-6',
      group: 'phrases',
      kind: 'copy',
      icon: '✒️',
      title: ctx.t('p6Title'),
      subtitle: ctx.t('p6Sub'),
      tag: tagCopy,
      tone: 'subtle',
      body: ctx.t('p6Body'),
    },
    {
      id: 'dev-1',
      group: 'dev',
      kind: 'copy',
      icon: '🌿',
      title: ctx.t('dev1Title'),
      subtitle: 'git status -sb',
      tag: 'Git',
      tone: 'cyan',
      body: 'git status -sb',
    },
    {
      id: 'dev-2',
      group: 'dev',
      kind: 'copy',
      icon: '🔄',
      title: ctx.t('dev2Title'),
      subtitle: 'git pull --rebase',
      tag: 'Git',
      tone: 'cyan',
      body: 'git pull --rebase',
    },
    {
      id: 'dev-3',
      group: 'dev',
      kind: 'copy',
      icon: '📜',
      title: ctx.t('dev3Title'),
      subtitle: 'git log --oneline -15',
      tag: 'Git',
      tone: 'accent',
      body: 'git log --oneline -15',
    },
    {
      id: 'dev-4',
      group: 'dev',
      kind: 'copy',
      icon: '🐳',
      title: ctx.t('dev4Title'),
      subtitle: 'docker compose ps',
      tag: 'Docker',
      tone: 'emerald',
      body: 'docker compose ps',
    },
    {
      id: 'dev-5',
      group: 'dev',
      kind: 'copy',
      icon: '📦',
      title: ctx.t('dev5Title'),
      subtitle: 'npm run dev',
      tag: 'npm',
      tone: 'warning',
      body: 'npm run dev',
    },
    {
      id: 'dev-6',
      group: 'dev',
      kind: 'copy',
      icon: '⚙️',
      title: ctx.t('dev6Title'),
      subtitle: 'dotnet build --no-restore',
      tag: '.NET',
      tone: 'violet',
      body: 'dotnet build --no-restore',
    },
  ];
}

function syncComputed(ctx) {
  const items = ctx.collections.get('snippets') || [];
  const activeGroup = String((ctx.state && ctx.state.group) || 'daily').trim() || 'daily';
  const count = items.filter((item) => String(item.group || '') === activeGroup).length;
  const fmt = String(ctx.t('capsuleBadgeFmt') || '{0}');
  ctx.computed.capsuleBadge = fmt.replace('{0}', String(count));
}

export function onInit(ctx) {
  const existing = ctx.collections.get('snippets') || [];
  if (existing.length === 0 && ctx.state.seeded !== true && ctx.state.seeded !== 'true') {
    ctx.collections.set('snippets', buildDefaultTiles(ctx));
    ctx.state.seeded = true;
  }
  const curGroup = String((ctx.state && ctx.state.group) || '');
  if (!['daily', 'phrases', 'dev'].includes(curGroup)) {
    ctx.state.group = 'daily';
  }
  if (!ctx.state.draftKind) {
    ctx.state.draftKind = 'copy';
  }
  ctx.state.lastFeedback = '';
  ctx.computed.ready = true;
  syncComputed(ctx);
}

export function setGroup(ctx) {
  const g = String((ctx.args && ctx.args.group) || 'daily');
  ctx.state.group = g;
  ctx.state.draftGroup = g;
  syncComputed(ctx);
}

export function addSnippet(ctx) {
  const body = String((ctx.state && ctx.state.draftBody) || '').trim();
  if (!body) return;

  const kind = String((ctx.state && ctx.state.draftKind) || 'copy') === 'open' ? 'open' : 'copy';
  const titleRaw = String((ctx.state && ctx.state.draftTitle) || '').trim();
  const title = titleRaw || (body.length > 16 ? body.slice(0, 16) + '…' : body);
  const subRaw = String((ctx.state && ctx.state.draftSubtitle) || '').trim();
  const flatBody = body.replace(/\s+/g, ' ');
  const subtitle = subRaw || (flatBody.length > 22 ? flatBody.slice(0, 21) + '…' : flatBody);
  const group = String((ctx.state && (ctx.state.draftGroup || ctx.state.group)) || 'daily').trim() || 'daily';
  const icon = kind === 'open' ? '🚀' : '📌';
  const tag = kind === 'open' ? ctx.t('tagOpen') : ctx.t('tagCopy');
  const tone = kind === 'open' ? 'cyan' : 'emerald';
  const editingId = String((ctx.state && ctx.state.editingId) || '').trim();

  const record = {
    id: editingId || ('tile-' + String((ctx.env && ctx.env.nowMs) || Date.now())),
    group,
    kind,
    icon,
    title,
    subtitle,
    tag,
    tone,
    body,
  };

  if (editingId !== '') {
    const items = ctx.collections.get('snippets') || [];
    const idx = items.findIndex((item) => String(item.id) === editingId);
    if (idx >= 0) {
      const next = items.slice();
      next[idx] = { ...next[idx], ...record };
      ctx.collections.set('snippets', next);
    } else {
      ctx.collections.prepend('snippets', record);
    }
  } else {
    ctx.collections.prepend('snippets', record);
  }

  ctx.state.group = group;
  ctx.state.editingId = '';
  ctx.state.draftTitle = '';
  ctx.state.draftSubtitle = '';
  ctx.state.draftBody = '';
  syncComputed(ctx);
}

export function removeSnippet(ctx) {
  const target = String((ctx.args && ctx.args.id) || (ctx.event && ctx.event.arg) || '').trim();
  if (!target) return;
  ctx.collections.remove('snippets', (item) => String(item.id) === target);
  syncComputed(ctx);
}

export function resetSnippets(ctx) {
  ctx.collections.set('snippets', buildDefaultTiles(ctx));
  ctx.state.seeded = true;
  ctx.state.group = 'daily';
  ctx.state.editingId = '';
  ctx.state.draftTitle = '';
  ctx.state.draftSubtitle = '';
  ctx.state.draftBody = '';
  ctx.state.lastFeedback = '';
  syncComputed(ctx);
}
