# 网页格子（WidgetKind.Web）需求与实施方案

- 日期：2026-10-27
- 状态：**已拍板**（Simon 当日 8 条决策全部按建议批准，见 §2）
- 范围：批 1（MVP）详细设计 + 批 2-4 路线 + 市场预留设计
- 前置调研：开源/社区生态调研、微软 AI 尝试调研、商店风险分析（2026-10-27 会话，结论摘录见 §1.3）

---

## 1. 背景与定位

### 1.1 产品定位

网页格子不是「第 8 种格子」，而是 **DeskBox 的用户扩展平台**：HTML 即插件格式，WebView2 即沙箱与运行时，胶囊收纳体系即宿主。零售 NativeAOT 无法动态加载程序集，原生插件路线已被架构判死；WebView2 提供沙箱、进程外、AI 生成友好（HTML/JS 是所有大模型最擅长生成的目标格式）三重属性。多格子多窗口产品形态不变。

### 1.2 硬约束

- 胶囊体系既有验收全部保留：hover 零卡顿展开、组员切换 150ms、边界动画 320ms 硬顶（`WidgetWindowBase.Collapse.cs`）。
- 主进程 commit 目标增量 ≤10-15MB（N 个格子）：WebView2 全部重开销住在 msedgewebview2.exe 进程树，主进程只持控制器包装。
- 零 AI 内置红线：DeskBox 本体不含 AI、零推送；AI 能力只有两条外挂路径——粘贴流（批 1）与 MCP（批 3）。
- 工程门禁照常：13 语言 parity（一次提交加全）、NativeAOT 冒烟、全量测试绿、新 DispatcherTimer 必须登记 PerformanceLogger。

### 1.3 调研结论摘要（决策依据）

- HTML/JS 插件运行时已被 uTools（3000-4000 插件）、Raycast（1000+，PR 审核制商店）、Wallpaper Engine（HTML5 壁纸+创意工坊）、Scriptable（JS 组件+awesome list 分发）四生态验证；「一句话生成 HTML 小工具」在国内已是大众行为。
- DeskBox 自身社区存在「敢加AI就卸载」情绪 → 零打扰红线与「AI 只在你调用时参与」话术。
- 微软教训：小组件板（内容分发位+第三方门槛）与 Dev Home（独立仪表盘载体）均失败 → 格子必须长在用户桌面收纳体系里、创作入口必须当天能用；微软作业可抄：信任语法（默认关/显式同意/可见状态/可撤销）、sparkle+shimmer 视觉语法、Adaptive Cards 式声明 UI。
- 商店风险分析：先当主编（策展制），别当运营商（开放上传平台化后置）。

---

## 2. 拍板记录（2026-10-27，Simon 全部按建议批准）

| # | 决策项 | 结论 |
|---|---|---|
| 1 | 创建入口 | 托盘「新建」菜单直达创建向导（`GetCreateEntryDescriptors` 第二个成员，`WidgetContentFactory.cs:218-223`）；不走 FeatureWidget 设置页路线 |
| 2 | 默认展开尺寸 | **360×480**（`WidgetManager.FeatureWidgets.cs:428-441` 加 case） |
| 3 | 在线 URL | 批 1 放开，创建向导内带内存量级提示文案 |
| 4 | 本地 HTML 网络策略 | 批 1 默认放行；格子右键可关（`Web.NetworkPolicy=Off`）；市场批（批 4）切换为 manifest 白名单 |
| 5 | 存储隔离 | `PartitionId` 在 SDK 1.0.2651.64 / 1.0.3719.77 均不存在（桌面预研二进制证实，§6-S3）→ **采用 per-widget 独立虚拟主机名** `deskbox-{safeId}.local`：源隔离+存储隔离，代价是每格独立 renderer（~50-80MB，住在 msedgewebview2 树，不进主进程）；SDK 升级时复查分区 API |
| 6 | 命名 | `WidgetKind.Web` / 中文「网页格子」/ 图标 globe（`Assets/WidgetTitleIcons/web.svg`） |
| 7 | Onboarding Step5 卡片 | 延批 2 |
| 8 | 节奏 | 批 1 MVP（本方案 §4-§7）→ 批 2 契约+bridge+设置分区 → 批 3 MCP+skill → 批 4 模板+市场（Phase 1 编辑部模式） |

---

## 3. 总体形态

- **多实例 kind**，照 Glance 路线（唯一多实例样板：`WidgetManager.FeatureWidgets.cs:456-639`），但批 1 不实现设置窗分区与实例管理 UI。
- **批 1 零全局设置**：不建 `WebSettingsSlice`、不动 243 门面计数与 13 slice 断言、不写 Migration_15_To_16、不进棘轮 manifest、无设置 IPC 命令——全部 defer 批 2（届时按 `WeatherSettingsSlice.cs` + `AppSettings.cs:685` passthrough + 空迁移范式一次做齐）。
- 持久化：per-widget 配置走 `WidgetConfig.Metadata`（零迁移，`WeatherWidgetViewModeSettings.cs:7-47` 范式）；HTML 资产落 `data/widget-assets/{id}/web/`（删除清理由既有钩子 `WidgetManager.cs:2074` 自动覆盖）。
- 创建/编辑向导跑在主进程内（改 widget-layout 域），不经设置进程。

---

## 4. 批 1 详细设计

### 4.1 WebWidgetRuntime（新增服务，唯一基础设施）

进程级单例，职责：

| 职责 | 设计 |
|---|---|
| 共享 environment（懒建） | 首个网页格子物化时创建；user data folder 固定 `data/webview2-profile/`；**refcount 管理：最后一个网页格子销毁 → environment 释放 → msedgewebview2 进程树退出**（「关闭=完全回收」实现点） |
| 虚拟主机 | `SetVirtualHostNameToFolderMapping("deskbox-{safeId}.local", …/{id}/web/)`，每格独立主机名 = 源隔离 + 存储隔离（拍板 5） |
| 网络策略挂点 | `WebResourceRequested` 处理器批 1 即存在；策略对象 per-widget、数据源 Metadata；批 1 默认 permissive（本地 HTML 放行、右键可关），市场批换白名单——**执行点先埋，将来只改策略不改管线** |
| 状态控制 | per-widget `IsVisible`、`MemoryUsageTargetLevel`、`TrySuspend/TryResume`（SDK API 已二进制证实存在） |
| 快照管线 | WebView2 自带 `CapturePreviewAsync`（能抓网页内容，绕开 RenderTargetBitmap 抓不到 HWND 的死穴）；胶囊解码宽度照抄 Glance 限流算法（`GlanceWidgetContentAdapter.cs:80-119`） |

### 4.2 状态阶梯（全部挂既有生命周期回调，不发明新机制）

| 状态 | 挂钩锚点 | 动作 |
|---|---|---|
| 展开可见 | — | 正常运行 |
| 收起成胶囊 | `OnCompactStateChanged(true)`（`WidgetContentAdapterBase.cs:127`） | 截屏 → 隐藏宿主 → `IsVisible=false` + `MemoryUsageTargetLevel=Low` |
| 窗口隐藏 | `OnWindowVisibilityChanged(false)`（`:115`） | 同上 + 挂起候选 |
| 长隐藏保养 | `OnWindowLongHidden()`（`:123`，`ContentSwitching.cs:210-222` 驱动） | `TrySuspend()` |
| 全部格子隐藏 | 复用 AllRecreatable 清理档（`App.xaml.cs:6133+` 武装条件 + background cleanup generation） | dispose 全部 controller；runtime refcount=0 时销毁 environment |
| 边界动画期间 | `OnCompactBoundsTransitionActiveChanged(true)`（`:131`） | 冻结 web 侧重工作 |

在线页胶囊快照刷新：仅收起瞬间截取（默认）；`Web.SnapshotRefreshMinutes>0` 时收起态定时刷新（timer 登记 PerformanceLogger）。

### 4.3 胶囊契约对齐

胶囊是封闭词汇表（`WidgetCompactPresentation.cs:12-63`，唯一通道、无自定义视觉注入）。网页格子的胶囊呈现 = `ContentWidgetWindow.xaml.cs:141-166` 加 `CreateWebCompactPresentation` 分支：

- `Title`：用户命名或页面标题；
- `Thumbnail` + `UseFullBleedBackground`：**收起瞬间的快照图铺满胶囊**（与 Glance 胶囊背景同形态，视觉=「缩小的网页」）；
- `LiveStateKey`：快照时间戳（驱动胶囊层按需重建）。

**HWND 铁律**（对冲探查确认的 8 处 HWND 断点：胶囊层盖穿/Opacity 失效/交叉淡切失效/RenderTargetBitmap 空白/首帧判定失明/预热无效/无嵌入先例/裁剪不同步）：**任何非稳定展开态，WebView2 视觉必须不可见**——收起/动画/组切换期间只显示 XAML 快照层（可被胶囊层覆盖、参与全部 XAML 动画）。挂钩点：收起=OnCompactStateChanged；组切出=`CommitPreparedContent` 的 `outgoing.OnDeactivated()`；动画=OnCompactBoundsTransitionActiveChanged。

**就绪门**：`IsCompactExpansionWarmupContentReady`（重写点 `ContentWidgetWindow.xaml.cs:115-116`）扩展「web 首帧已渲染」条件——预热机制对 web 内容无收益（只暖布局），超时落 320ms live-layout fallback（`Collapse.cs:3333+`，可接受）。

### 4.4 组切换

**缓存型成员**（实现 `IWidgetGroupContentCacheable`，判定点 `ContentSwitching.cs:141-165`）：WebView2 重建是全管线最贵操作，与 File/Todo 选缓存型同一逻辑。`CaptureTransientState`（`IWidgetTransientStateContent.cs:7-12`）保存滚动位置 + URL。150ms 切换验收靠 `TryResume`（毫秒级）+ 快照层补位满足。

### 4.5 per-widget 配置与右键菜单

Metadata 键（`WebWidgetSourceSettings` 存取器，照 `WeatherWidgetViewModeSettings.cs` 范式：key 常量/值白名单/TryGet+Set/changed 返回）：

| 键 | 值域 | 默认 |
|---|---|---|
| `Web.SourceKind` | LocalHtml / OnlineUrl | 创建时定 |
| `Web.Url` | string（在线 URL 或资产相对路径） | — |
| `Web.ScaleMode` | Fit / Scale / Scroll | Fit |
| `Web.SnapshotRefreshMinutes` | 0-60 | 0（仅收起时截） |
| `Web.NetworkPolicy` | Default / Off | Default（放行） |
| `Web.CreatedVia` | Manual / Paste / Agent / Template（市场批启用） | Manual |

右键菜单（照 `GlanceWidgetContextMenuBuilder.cs:26-38`，挂点 `ContentWidgetWindow.Commands.cs:283`）：刷新 / 缩放模式（Radio）/ 快照刷新间隔 / 网络开关 / 重新编辑（重开向导）/ 删除。

### 4.6 创建向导（批 1 最大新 UI）

- 三 tab：**粘贴代码 / 本地文件 / 在线网址**；确定前实时预览（真 WebView2 渲染，可切深浅色）。
- 「复制引导词」按钮：内置格子契约（视口尺寸/DPI/深浅色 CSS 变量 `--deskbox-*`/单文件约束），批 1 的质量开关。
- 轻量窗口载体（重命名窗/冲突窗一档），不用 ContentDialog 大表单。
- `.deskboxw` 兼容预留：文件 tab 接受未来包格式（zip = index.html + manifest.json），解包走同一条管线。
- 在线 tab 带内存量级提示文案（每个不同源网页 ≈100-300MB，住独立进程）。

### 4.7 资产与清理

HTML 及其资源落 `data/widget-assets/{id}/web/`；校验沿用 `WidgetTitleIconAssetStore.CopyAssetAsync` 的扩展名白名单 + 大小上限模式（上限建议 10MB 同款）。删除格子时 `WidgetManager.cs:2074` 的 `DeleteWidgetAssets` 自动清掉 `web/` 子目录（`data/widget-assets` 下任何东西不得活得比 config 久）。

### 4.8 WebView2 Runtime 缺失降级

启动格子时 `CoreWebView2Environment.GetAvailableBrowserVersionString()` 探测；缺失 → 内容控件显示引导卡态（13 语言文案 + 官网说明链接），绝不内置在线下载器（直装包离线铁律）。

### 4.9 诊断纪律

- PerformanceLogger 新增计数器对：web 快照解码（Count+EstimatedBytes）、活跃 controller/env 计数。
- 所有新 DispatcherTimer 一律 `RecordTransientUiTimerCreated/Released`（`PerformanceLogger.cs:169-177`）。

### 4.10 背景材质与缩放（第四轮 spike 实测定稿，2026-10-28）

- **材质透出：可行——但仅限自宿主路径（终审定稿，含两次判据纠偏）**。最终真相表（判据方法论：**只有屏幕级捕获（GetDC(0)+窗口矩形）是地面真值**；PrintWindow/窗口 DC 对 GPU 合成子窗会漏层，两度造成伪判）：
  - XAML WebView2 控件 + `DefaultBackgroundColor=Transparent`：❌ 屏幕上不透明（A 区零透出），且 resize 往返复现渲染崩坏（社区坑 #2419）→ 双重判死；
  - **自宿主子 HWND**（`CoreWebView2ControllerWindowReference.CreateFromWindowHandle(ulong)` → `CreateCoreWebView2ControllerAsync`（返回 IAsyncOperation，`AsTask()` 取 controller）→ `Controller.DefaultBackgroundColor=Transparent` + `Controller.Bounds` 跟随）：✅ 屏幕级真透明实证（判据页蓝条 53k px 与橙底 90.8k px 同时可见），Simon 真机目检确认云母透出；
  - **批 1 架构决策更新：web 宿主层不走 XAML WebView2 控件，改为自宿主 controller**——布局跟随=SetBounds 随格子 resize、输入由子 HWND 原生承接（无需合成宿主的输入转发）、快照/虚拟主机/状态阶梯设计全部不变；合成宿主（CompositionController）不再必要，仅留档；
  - **云母激活态：零额外成本**——web 格子沿用格子窗口既有 `MicaController + SystemBackdropConfiguration.IsInputActive = true`（`WidgetWindowBase.Backdrop.cs` 同款），不聚焦也常显激活云母，透明 web 区域透出的即是该状态（spike T14 已换同款 controller 并加 IsInputActive 切换按钮演示）；
  - **材质完全跟随实证（第 18/19 轮）**：spike 已移植 `WidgetMaterialVisualCalculator` 同款公式做成材质控制台——云母 Base/BaseAlt、浓度三档（`CalculateMica`：Base 档 LuminosityOpacity 0.78→0.94，浓度满格最亮）、毛玻璃（`CalculateAcrylic`）、**纯色与全透明（`WinUIEx.TransparentTintBackdrop` 2.9.3，与格子 `WidgetWindowBase.Backdrop.cs:308` 同库同类：tint 不透明=纯色、alpha=0=全透明；注意这是三方库 WinUIEx，web 格子经同一 backdrop 管线天然共用）**，全部生效（Simon 实机把玩确认）。此前"spike 云母偏暗"的根因 = controller 默认参数 vs 格子公式的 LuminosityOpacity；"全透明"必须走 TransparentTintBackdrop（去掉 backdrop 只留空背景时 WinUI 窗口仍画不透明底）。**web 格子零成本继承全部材质状态**：材质是窗口级（ContentWidgetWindow 走 `WidgetWindowBase.Backdrop` 既有管线），透明 web 区域透出的即当前材质的任意状态（含 IsInputActive 常激活、深浅色、材质切换、自定义背景图——背景图走 XAML 层，自宿主透出的橙 Grid 已证下层 XAML 可见）。
  - 批 1 真机验收补充：真格子（多屏/DPI/收起胶囊/材质切换）下「云母-透明-webview」三层合成矩阵。`Web.BackgroundMode=FollowMaterial` = 控件预置透明 + 页面 body 留空即透材质。
- **宽度自适应+纵向滚动：可行**。响应式页面（AI 生成本地格子主场景）零成本即得；非响应式页面（在线 URL）`ScaleMode=Fit` 档用 **CSS zoom** 实现（`ZoomFactor` 投影不可达）：加载完成后读 `scrollWidth/clientWidth` 计算 zoom 应用、resize 防抖重算、范围钳制——实测 1200px 固定宽页面 zoom 0.63 后水平溢出归零、纵向滚动原生。`ScaleMode=Scroll` 档为原生不缩放；另可注入 `img,video{max-width:100%}` 强制响应式兜底。

---

## 5. 市场预留（现在埋、批 4 用）

1. **导入格式**：`.deskboxw` = zip（index.html + manifest.json{name/version/author/scaleMode/networkDomains[]}）→ manifest 映射 Metadata、资产解包 `web/`——与手选 HTML 同管线。
2. **权限与签名接缝**：网络策略对象以 Metadata 为源（市场 manifest 直接填白名单）；预留 `IWebWidgetPackageVerifier` 空接口（签名校验批 4 接入）。
3. **隔离地基批 1 即完成**（独立主机名），分享功能上线前无需数据迁移。
4. **Phase 1 编辑部模式**：应用内市场 = 人工审核的 manifest 清单（JSON 索引）+ GitHub/官网 CDN 下载 + 签名校验 + kill switch；开放上传无限期后置（商店风险分析结论：先当主编，别当运营商）。
5. 信任三件套（确认卡/活跃指示/可撤销+审计）随批 3 MCP 一起落地，复用 agent 确认卡范式。

---

## 6. Spike 结论（2026-10-27 四项全结，独立工程 `D:\project\wingezi-webview-spike\`）

独立工程（仓库外；WinUI3 x64 Debug + NativeAOT Release 双形态），自动化测试结果见工程内 `spike-results.txt`（JIT 与 AOT 两轮全绿）。

| # | 项 | 结论 |
|---|---|---|
| S1 | API 面与环境路由 | **PASS**。真实引用程序集 = NuGet 的 `Microsoft.Web.WebView2.Core.Projection.dll`（lib_manual 投影——既不是 net462 全版、也不是 WinUI.dll 内的残根；此前二进制字符串扫描扫错了文件，教训记入）。API 完整但命名有差异（见下表）。XAML 控件**无 env 注入属性** → 控件路径 UDF 统一走进程级 `WEBVIEW2_USER_DATA_FOLDER`（实测生效，EBWebView 落盘）；自宿主路径有 `CreateWithOptionsAsync(3参)` + Controller/CompositionController 全套可用 |
| S2 | 宿主形态 | **窗口式（HWND 岛）实锤**：RenderTargetBitmap 只捕到 2 色 → 网页像素不在 XAML 合成树，**HWND 铁律 + 快照层策略为必需**（设计即按此写，零改动）。`DefaultBackgroundColor=Transparent` setter 通过，实际透明与否待目检（窗口式通常非真透明 → 胶囊内嵌活网页不可行，胶囊呈现=快照图，与设计一致） |
| S3 | 虚拟主机与隔离 | **PASS**。`SetVirtualHostNameToFolderMapping(String,String,CoreWebView2HostResourceAccessKind)` 可用，a/b.deskbox.local 导航成功；**源隔离成立**（干净 UDF 下 B 读 A 的 probe=null、A 回读=A，两轮实证；上轮"FAIL"为残留数据假阳性）。**Bonus 发现：localStorage 跨进程重启持久** → 批 1 需补「删除格子=清该 origin 存储」闭环（ClearVirtualHostNameToFolderMapping + Profile 清理） |
| S4 | AOT | **PASS**。NativeAOT 发布（8.6MB 单文件）+ 运行时全部测试绿（CapturePreview 快照与 JIT 字节级一致）。工具链结论：须 MSVC **14.44.35207**（14.42 缺静态 CRT）+ `-p:IlcUseEnvironmentalTools=true` + VS 环境（PATH 里 MSVC bin 必须在 Git Bash 的 GNU link 之前），可用脚本 `publish-aot.cmd` 复现 |

**成员命名差异表（投影 vs 独立 .NET SDK）**：`CreateWithOptionsAsync(3参)`↔独立版 `CreateAsync(3参)`；投影 `CreateAsync(0参)`；`SetVirtualHostNameToFolderMapping`（无 Async 后缀）；`TrySuspendAsync`/`IsSuspended` 直接在 CoreWebView2 上（控件路径可达，无需 Controller）；`Navigate(string)`；控件初始化事件名 `CoreWebView2Initialized`；另有 `ExecuteScriptWithResultAsync`/`NavigateWithWebResourceRequest`/`ScreenCaptureStarting` 等新 API。

**带入批 1 的定论**：方案零结构性改动——虚拟主机隔离、快照走 `CapturePreviewAsync`（RTB 空白已证不可作替代）、状态阶梯（挂起=异步生效，脚本/导航自动唤醒）、HWND 铁律与窗口式宿主，全部实证支撑。§10 风险表第 1 行（env 注入不支持）已由 env var 路由解除。

---

## 7. 批 1 文件清单（最小集 ≈14 文件 + 本方案增量）

| 类别 | 内容 | 锚点/样板 |
|---|---|---|
| 枚举+注册 | `WidgetKind.Web` 枚举值；descriptor（DefaultGlyph globe、StatusLabel/Description、HasSettingsPage=false、IsFeatureWidget=true 多实例语义、ChromeCategory/DefaultChromeMode）；provider 行；Registry 行；`WebWidgetContentProvider.cs`（19 行） | `WidgetConfig.cs:292`、`WidgetContentFactory.cs:26-138/:271-285`、`WidgetRegistry.cs:57-71` |
| 内容 | `WebWidgetContent.xaml(.cs)`（快照层+宿主位+骨架 shimmer+引导/错误态）；`WebWidgetContentAdapter.cs`（生命周期转发+Cacheable+TransientState+ResponsiveLayout）；`WebWidgetViewModel.cs` + AotBindableProperties 分部 | 照抄 `GlanceWidgetContentAdapter.cs` |
| 基础设施 | `WebWidgetRuntime`；`WebWidgetSourceSettings` | 新文件；无 store（无大块数据） |
| UI | 创建向导窗（三 tab+预览+引导词）；`WebWidgetContextMenuBuilder`；`CreateWebCompactPresentation` 分支；三处标题 switch；`WidgetSettingsMenuHelper` 两 switch（批 1 无设置分区→「设置」项指向重新编辑或隐藏）；`WidgetTitleIconMode` 四 switch + `Assets/WidgetTitleIcons/web.svg` | `ContentWidgetWindow.xaml.cs:141-166/:1455-1465`、`WidgetManager.FeatureWidgets.cs:380-390`、`WidgetManager.Groups.cs:694-705`、`WidgetTitleIconMode.cs:48-175` |
| 管理器 | `CreateFeatureWidgetHandlers`/`CreateWindowProviders`/`CreateWidgetOfKindAsync`/默认尺寸 case；删除清理零改动（widget-assets 自动钩子覆盖） | `WidgetManager.cs:589-691/:1055-1097`、`FeatureWidgets.cs:428-441` |
| 语言 | Strings ×13 约 40-60 键（向导/右键/错误/引导卡/内存提示），一次提交加全 | `LocalizationResourceContractTests.cs:27-63` |
| 测试 | 五个契约测试加行 + 新增 Runtime 生命周期（refcount/dispose）、快照策略、Metadata 存取、状态阶梯挂钩 | `FeatureWidgetKindContractTests.cs:16-29` 等 |
| AOT | AotBindableProperties + 两个 persistence smoke + `App.AotManagedUiSmoke.cs:988` ternary | Glance 三件套 |

**批 1 明确不做**（接缝已留）：设置窗分区与 IPC 五件套、Onboarding 卡片、schema 迁移、slice、MCP、bridge API、市场。

---

## 8. 批 2/3/4 路线概览

- **批 2（契约+bridge+设置分区）**：CSS 变量与 `deskbox.*` bridge v1（storage/theme/widget/capture 四件套，存储已按主机名隔离天然达成）；`WebSettingsSlice` + 设置分区（Glance 实例管理范式 + IPC 五件套）；Onboarding Step5 卡片。
- **批 3（MCP+skill）**：`DeskBox.Mcp.exe`（stdio MCP + 控制管，抄 SettingsIpc 管道模式）+ P0 工具面（create_web_widget/布局/Todo/随记）+ 信任三件套（确认卡/活跃指示/可撤销+审计）+ `deskbox-agent` skill 文档（含快照自检闭环 read_widget_snapshot）。
- **批 4（模板+市场）**：3 个声明式原生模板（DeskBox 卡片，Adaptive Cards 思路）+ `.deskboxw` 导入导出 + 签名/kill switch + 官网画廊 + awesome 仓库。

---

## 9. 验收标准

- **交互**：hover 展开/收起动画无 web 残影（快照层覆盖）；组切换 ≤150ms（缓存+resume）；胶囊显示快照；边界动画期间 web 无渲染参与。
- **内存**（用「提交大小」口径）：主进程 commit 增量 ≤10-15MB（N 格）；msedgewebview2 树单独计量并进内存矩阵新行（`docs/acceptance/r10-retail-memory-matrix-*` 扩展）；全部格子隐藏超时后进程树归零。
- **门禁**：全量测试绿；AOT 冒烟绿；13 语言 parity 过；新 timer/解码计数器全部登记。

---

## 10. 风险与缓解

| 风险 | 缓解 |
|---|---|
| WinUI3 控件 env 注入面不支持（S1 失败） | 兜底=自宿主 `CoreWebView2Controller`（仓库无先例但 API 齐备）；S2 合并验证 |
| 每格独立 renderer 的内存量级（在线页 100-300MB/源） | 创建向导明示文案；独立主机名仅本地资产格子共享度受限于拍板 5（可接受：本地小页面 renderer 远小于重网页）；SDK 分区 API 出现后迁移 |
| WebView2 Runtime 缺失（部分 Win10） | 引导卡降级 + 官网文档；不内置下载器（离线安装铁律） |
| 裁剪/裁剪锯齿与裁剪器交互（msedgewebview2 不受主进程裁剪器管辖） | 状态阶梯即其裁剪器；验收项单列 |
| 社区「敢加AI就卸载」情绪 | 零打扰红线：批 1 无任何 AI 字样入 UI（除引导词按钮）；MCP 默认未启用 |

---

## 11. 代码锚点索引（2026-10-27 三路探查结论）

- 新增 kind 全链路：`WidgetContentFactory.DescriptorList`（唯一清单真源）、WidgetManager 四表、三处标题 switch、五个契约测试文件——完整触碰清单见会话记录（最小集 14 文件 / Glance 完整集 72 文件 8604 行）。
- 胶囊契约：`WidgetCompactPresentation`（封闭词汇表）、`ContentWidgetWindow.CreateCompactPresentation`、收起=Visibility+行高归零（`WidgetShell.xaml.cs:4519-4529`）、动画=逐帧 SetWindowPos+Opacity（`Collapse.cs:3431-3640`）、预热=PrimeSlice+WarmLayout（对 HWND 无收益）、就绪门 `IsCompactExpansionWarmupContentReady`。
- 设置纪律：slice 不门面（`SettingsSliceOwnershipContractTests` 棘轮 243/13）、Metadata 零迁移（`WeatherWidgetViewModeSettings`）、资产自动清理（`WidgetManager.cs:2074`）、13 语言一次加全。
- 仓库自陈架构路线图：`docs/architecture/widget_contribution_seam.md`（Step1 已完成；本方案遵守其接缝原则）。
