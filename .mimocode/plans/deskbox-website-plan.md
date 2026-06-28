# DeskBox 官网开发规划文档

## 一、项目定位

**目标**：为 DeskBox 桌面整理工具打造一个微软 Fluent Design 风格的官方网站，兼顾产品宣传、SEO 引流、版本管理和用户社区。

**核心诉求**：
1. SEO 优化，让搜索引擎能索引到 DeskBox 的功能和下载信息
2. 完全仿照微软官方风格（Fluent Design System 2）
3. 提供版本更新、历史版本下载、功能介绍、未来规划
4. 集成微信公众号入口

---

## 二、技术选型

| 维度 | 方案 | 理由 |
|------|------|------|
| 框架 | **Next.js 14 (App Router)** | SSR/SSG 支持好，SEO 友好，React 生态丰富 |
| 样式 | **Tailwind CSS + Fluent UI Web Components** | 快速开发 + 微软风格组件 |
| 动效 | **Framer Motion** | React 生态最成熟的动效库，支持手势和过渡 |
| 部署 | **Vercel** 或 **Cloudflare Pages** | 免费、全球 CDN、自动 HTTPS |
| 域名 | `deskbox.app` 或 `deskbox.cc` | 简短好记 |
| CMS | **MDX 文件 + Git 管理** | 版本更新日志直接从 CHANGELOG.md 生成 |
| 图标 | **Fluent UI System Icons** | 与 Windows 11 图标风格一致 |

---

## 三、页面结构

### 3.1 首页 (`/`)

```
┌─────────────────────────────────────────────┐
│  导航栏（Logo + 功能/下载/更新日志/关于）    │
├─────────────────────────────────────────────┤
│  Hero 区域                                   │
│  ┌─────────────┐  ┌───────────────────────┐ │
│  │ 产品封面图   │  │ 标题：DeskBox         │ │
│  │ (带动效)     │  │ 副标题：轻量桌面整理   │ │
│  │              │  │ [下载按钮] [了解更多]  │ │
│  └─────────────┘  └───────────────────────┘ │
├─────────────────────────────────────────────┤
│  功能亮点（3-4 个卡片，带图标和简短描述）     │
│  ┌──────┐ ┌──────┐ ┌──────┐ ┌──────┐       │
│  │收纳格子│ │文件夹│ │随记  │ │快捷键│       │
│  │      │ │映射  │ │      │ │      │       │
│  └──────┘ └──────┘ └──────┘ └──────┘       │
├─────────────────────────────────────────────┤
│  产品截图轮播（支持明暗主题切换）             │
├─────────────────────────────────────────────┤
│  下载区域（最新版本 + 系统要求）              │
├─────────────────────────────────────────────┤
│  页脚（GitHub / 微信公众号 / MIT License）    │
└─────────────────────────────────────────────┘
```

**动效设计**：
- Hero 区域：Logo 从三个图层组装的动画（复用 `deskbox-motion-01-layer-assemble.svg`）
- 功能卡片：鼠标悬停时轻微上浮 + 阴影加深（Fluent Design 的 elevation 效果）
- 截图轮播：淡入淡出过渡，支持手势左右滑动
- 下载按钮：按压时有 Fluent 的按压反馈动画

**SEO 关键词**：
- 中文：桌面整理、Windows 11 工具、文件管理、桌面格子、桌面小组件
- English: desktop organizer, Windows 11 widget, file manager, desktop widget, quick capture

### 3.2 功能介绍页 (`/features`)

分模块详细介绍三大核心功能：

**收纳格子**：
- 功能说明（创建、排序、拖拽、右键菜单）
- 截图（图标视图 / 列表视图）
- 使用场景举例

**文件夹映射**：
- 功能说明（映射已有文件夹、不移动文件）
- 与收纳格子的对比
- 截图

**随记**：
- 功能说明（剪贴板记录、文本/链接/截图）
- 三个 Tab 的说明（记录/置顶/最近）
- 截图

**其他功能**：
- 全局快捷键
- 托盘管理
- 动画效果
- 拖拽诊断

每个功能模块使用 Fluent Design 的卡片布局，左侧说明右侧截图。

### 3.3 下载页 (`/download`)

```
┌─────────────────────────────────────────────┐
│  最新版本下载                                │
│  ┌───────────────────────────────────────┐  │
│  │ DeskBox v1.1.3                        │  │
│  │ [下载 x64 安装包] (21.7 MB)           │  │
│  │ 发布日期：2026-06-27                   │  │
│  └───────────────────────────────────────┘  │
│                                             │
│  系统要求                                   │
│  • Windows 11 (推荐) / Windows 10           │
│  • .NET 8 Runtime x64 (自动安装)            │
│  • Windows App Runtime 2.1.3 (自动安装)     │
│                                             │
│  历史版本                                   │
│  ┌───────────────────────────────────────┐  │
│  │ v1.1.2  2026-06-26  [下载] [更新日志] │  │
│  │ v1.1.1  2026-06-26  [下载] [更新日志] │  │
│  │ v1.1.0  2026-06-26  [下载] [更新日志] │  │
│  │ ...                                   │  │
│  └───────────────────────────────────────┘  │
└─────────────────────────────────────────────┘
```

**数据来源**：通过 GitHub API 自动获取 releases 列表和下载链接。

### 3.4 更新日志页 (`/changelog`)

直接从 `CHANGELOG.md` 渲染，支持：
- 按版本折叠/展开
- 中英文切换
- 锚点链接（可分享特定版本）

### 3.5 关于页 (`/about`)

- 开发者介绍（简短个人故事）
- 技术栈（WinUI 3 / .NET 8 / Windows App SDK）
- 开源信息（MIT License / GitHub 链接）
- 微信公众号二维码
- 未来规划

### 3.6 未来规划页 (`/roadmap`)

基于现有代码中的规划文档和 TODO：
- 在线更新功能（已有 requirements 文档）
- 插件系统（已有可行性分析文档）
- 更多格子类型
- 性能持续优化

---

## 四、Fluent Design 风格规范

### 4.1 配色

| 用途 | 亮色 | 暗色 |
|------|------|------|
| 主色 | `#0078D4` (Blue) | `#60CDFF` |
| 背景 | `#F3F3F3` | `#202020` |
| 卡片背景 | `#FFFFFF` | `#2D2D2D` |
| 文字主色 | `#1A1A1A` | `#FFFFFF` |
| 文字次色 | `#616161` | `#9E9E9E` |

### 4.2 字体

```
font-family: 'Segoe UI Variable', 'Segoe UI', system-ui, -apple-system, sans-serif;
```

### 4.3 圆角

- 卡片：`border-radius: 8px`（对应 DWM Corner Round）
- 按钮：`border-radius: 4px`
- 输入框：`border-radius: 4px`

### 4.4 阴影（Elevation）

```css
/* 悬停时 */
box-shadow: 0 2px 8px rgba(0, 0, 0, 0.08);
/* 按压时 */
box-shadow: 0 1px 4px rgba(0, 0, 0, 0.06);
```

### 4.5 动效

- 进入动画：`ease-out` 300ms
- 退出动画：`ease-in` 200ms
- 悬停反馈：`ease-out` 150ms
- 按压反馈：`ease-in` 100ms

### 4.6 交互规范

- 所有可点击元素有 `pointer` 光标和悬停态
- 按钮有 Fluent 的按压缩放效果（scale 0.98）
- 卡片悬停有轻微上浮（translateY -2px）
- 页面切换有淡入过渡

---

## 五、SEO 策略

### 5.1 Meta 标签

```html
<title>DeskBox - 轻量级 Windows 11 桌面整理工具</title>
<meta name="description" content="DeskBox 是一个基于 WinUI 3 的 Windows 11 桌面整理工具，用轻量桌面格子帮你收纳文件、映射文件夹、管理剪贴板。" />
<meta name="keywords" content="桌面整理,Windows 11,桌面格子,文件管理,桌面小组件,DeskBox" />
```

### 5.2 结构化数据

- `SoftwareApplication` schema（应用名称、版本、下载链接、评分）
- `FAQPage` schema（常见问题）
- `BreadcrumbList` schema（导航路径）

### 5.3 内容策略

- 每个功能页有独立的 URL 和详细的中英文描述
- 更新日志页自动从 CHANGELOG.md 生成，保持内容新鲜
- 未来规划页展示活跃开发状态

### 5.4 技术 SEO

- SSR/SSG 确保所有页面可被爬虫索引
- `sitemap.xml` 自动生成
- `robots.txt` 允许所有爬虫
- Open Graph / Twitter Card 元标签
- 规范的 URL 结构（`/features`, `/download`, `/changelog`）

---

## 六、数据来源和自动化

| 数据 | 来源 | 更新方式 |
|------|------|---------|
| 版本信息 | GitHub Releases API | 构建时自动拉取 |
| 更新日志 | `CHANGELOG.md` | Git push 触发重新构建 |
| 下载链接 | GitHub Releases API | 自动 |
| 安装包大小 | GitHub Releases API | 自动 |
| 功能截图 | `docs/images/` 目录 | 手动更新 |
| 品牌素材 | `docs/images/brand/` | 手动更新 |

---

## 七、目录结构

```
website/
├── public/
│   ├── favicon.ico
│   ├── og-image.png
│   └── screenshots/
├── src/
│   ├── app/
│   │   ├── layout.tsx          # 根布局（导航栏 + 页脚）
│   │   ├── page.tsx            # 首页
│   │   ├── features/
│   │   │   └── page.tsx        # 功能介绍
│   │   ├── download/
│   │   │   └── page.tsx        # 下载页
│   │   ├── changelog/
│   │   │   └── page.tsx        # 更新日志
│   │   ├── about/
│   │   │   └── page.tsx        # 关于
│   │   └── roadmap/
│   │       └── page.tsx        # 未来规划
│   ├── components/
│   │   ├── Navbar.tsx
│   │   ├── Footer.tsx
│   │   ├── HeroSection.tsx
│   │   ├── FeatureCard.tsx
│   │   ├── DownloadButton.tsx
│   │   ├── ChangelogEntry.tsx
│   │   ├── ScreenshotCarousel.tsx
│   │   └── ThemeToggle.tsx
│   ├── lib/
│   │   ├── github.ts           # GitHub API 工具
│   │   ├── changelog.ts        # CHANGELOG.md 解析
│   │   └── seo.ts              # SEO 工具函数
│   └── styles/
│       └── fluent.css          # Fluent Design 自定义样式
├── CHANGELOG.md -> ../CHANGELOG.md  # 符号链接
├── next.config.js
├── tailwind.config.js
├── package.json
└── tsconfig.json
```

---

## 八、开发优先级

| 阶段 | 内容 | 预估工时 |
|------|------|---------|
| P0 | 首页 + 下载页 + 基础框架 | 2-3 天 |
| P1 | 功能介绍页 + 更新日志页 | 1-2 天 |
| P2 | 关于页 + 未来规划页 | 1 天 |
| P3 | 动效打磨 + 暗色主题 + 响应式 | 1-2 天 |
| P4 | SEO 优化 + 结构化数据 + 部署 | 1 天 |

总计约 **6-9 天**。

---

## 九、后续可扩展

- **版本更新检测**：官网提供 API endpoint，DeskBox 客户端定期检查是否有新版本
- **在线更新**：客户端直接从 GitHub Releases 下载更新包
- **用户反馈**：嵌入 GitHub Discussions 或 Issue 模板
- **多语言**：后续可扩展日语、韩语等
- **博客**：技术文章和使用教程
