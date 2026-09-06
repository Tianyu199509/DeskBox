# DeskBox 发版提交范围对照清单

日期：2026-07-06

> ⚠️ **时效提示（2026-09-06）**：本文写于 Direct/Store 双通道时代。自 1.4.8 起，Direct 通道只发布 Full 安装包（内置运行时，安装包文件名不带 Full 后缀），并引入 InstallManifest 与更新清理脚本机制；Store 通道的打包入口见 `store_msix_build_notes.md`，发布通道与分发流程以 [`distribution_channel_workflow.md`](distribution_channel_workflow.md) 为准。使用本清单前，先按现行流程逐条复核，与上述文档冲突时以上述文档为准。

本文档用于每次发版前整理 Git 提交范围，避免把本地构建产物、测试包、公众号草稿、网站临时改动或 Store 本地签名文件混进应用发布提交。

如果后续 DeskBox 的发布通道、打包脚本、网站发布策略、文档目录或自动更新流程发生调整，必须及时更新本文档。不要只改代码不改这份清单，否则后面发版很容易再次混入不该提交的文件。

## 一、基本原则

1. 应用代码和官网网站不要默认混在一个提交里。
2. Direct 官网版和 Microsoft Store 版可以在同一轮应用提交里处理，但必须明确通道差异。
3. 本地构建产物、临时签名包、证书、测试输出不进 Git。
4. 微信公众号文章、临时宣传图、桌面截图草稿默认不进 Git。
5. README、CHANGELOG、架构文档可以随应用发版提交，但内容必须和本次版本实际变化一致。
6. 如果某个文件不确定是否该提交，先放到“待确认”列表，不要顺手 `git add .`。

## 二、本次应用发版建议提交

本次 `.NET 10 + Windows App SDK 2.2 + Direct/Store 双通道` 相关改动，建议纳入应用发布提交：

| 范围 | 是否提交 | 说明 |
| --- | --- | --- |
| `src/DeskBox/` | 提交 | 主程序、Store/Direct 通道、更新服务、设置页、定位服务等应用代码 |
| `src/DeskBox.Updater/` | 提交 | Direct 官网版应用内更新器 |
| `tests/DeskBox.Tests/` | 提交 | 与本次改动相关的单元测试 |
| `installer/` | 提交 | Direct 官网版 Inno 安装器、运行时依赖检测 |
| `scripts/` | 提交 | Store MSIX 构建脚本等发版脚本 |
| `docs/architecture/` | 提交 | 架构说明、升级方案、双通道操作手册、提交范围清单 |
| `docs/requirements/online-update.md` | 提交 | 如果更新服务设计有同步调整 |
| `README.md` | 提交 | 英文说明同步版本、运行时、下载和功能变化 |
| `README.zh-CN.md` | 提交 | 中文说明同步版本、运行时、下载和功能变化 |
| `CHANGELOG.md` | 提交 | 版本更新日志 |
| `DeskBox.sln` | 提交 | 如果新增项目或解决方案结构变化 |

## 三、需要单独判断的内容

这些内容不是永远不能提交，但不应该无脑混进应用发版提交。

| 范围 | 默认处理 | 何时可以提交 |
| --- | --- | --- |
| `deskbox-site/` | 不跟应用提交混在一起 | 官网内容确实要同步发布时，单独做网站提交 |
| 临时宣传文案与排版草稿 | 默认不提交 | 只有明确要把公开素材纳入仓库时，才整理到正式文档或图片目录 |
| `store-assets-html/` | 默认不提交 | 仅作为本地 Microsoft Store 截图/图标 HTML 画布；导出的 PNG 可手动上传 Partner Center，但源目录不进应用提交 |
| 宣传封面、产品长图、公众号配图 | 默认不提交 | README 或官网实际引用时才提交到合适目录 |
| 新截图 | 单独确认 | README/官网引用则提交；临时测试截图不提交 |
| `Package.appxmanifest` 的 Identity | 提交并逐次核对 | 已使用 Partner Center 正式身份，打包后仍需复核生成清单 |
| Store logo/tile/splash 图片 | 可以提交 | 如果是正式 Store 包资源 |
| 网站 SEO 文件，如 `robots.txt`、`sitemap.xml`、`llms.txt` | 不跟应用提交混在一起 | 官网上线提交时统一处理 |

## 四、明确不提交

以下内容不进入 Git：

| 范围 | 原因 |
| --- | --- |
| `.codex-temp/` | 本地 SDK、构建输出、签名 MSIX、测试证书、临时文件 |
| `artifacts/` | 打包输出 |
| `bin/` / `obj/` | 编译产物 |
| `Output/` | 安装器输出 |
| `TestResults/` | 测试输出 |
| `*.pfx` / `*.cer` | 证书和签名材料 |
| `*.msix` / `*.msixbundle` / `*.msixupload` | 打包产物 |
| `*.log` | 日志 |
| 临时公众号文案与导出 HTML | 本地宣传草稿 |
| `store-assets-html/` | 本地 Store 截图/图标生成画布，不应进入 Git、Direct 安装包或 Store MSIX |
| 临时截图、剪贴板图片 | 本地验证材料 |
| 微信/支付宝收款码测试副本 | Store 政策敏感且不应误进 Store 包或公开提交 |

`.gitignore` 当前已经包含 `.codex-temp/`、`artifacts/`、`bin/`、`obj/` 等主要本地产物目录。新增本地临时目录时，要同步检查 `.gitignore` 和本文档。

## 五、发版前检查命令

先看总览：

```powershell
git status --short
git diff --stat
```

看未跟踪文件：

```powershell
git ls-files --others --exclude-standard
```

检查是否混入构建产物：

```powershell
git status --short | Select-String -Pattern '\.codex-temp|artifacts|bin/|obj/|\.msix|\.pfx|\.cer|TestResults|Output|store-assets-html'
```

检查 Store 包是否误带 Direct 更新器或捐赠二维码：

```powershell
$msix = "path\to\DeskBox_版本_x64.msix"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($msix)
$zip.Entries | Where-Object {
  $_.FullName -like "*DeskBox.Updater*" -or
  $_.FullName -like "*donation-*"
} | Select-Object FullName
$zip.Dispose()
```

## 六、推荐提交流程

不要使用：

```powershell
git add .
```

推荐按范围 staged：

```powershell
git add src/DeskBox src/DeskBox.Updater tests/DeskBox.Tests installer scripts
git add README.md README.zh-CN.md CHANGELOG.md DeskBox.sln
git add docs/architecture docs/requirements/online-update.md
```

如果本轮不提交官网：

```powershell
git restore --staged deskbox-site
```

如果本轮要提交官网，建议单独提交：

```powershell
git add deskbox-site
git commit -m "Update DeskBox website for version x.y.z"
```

应用代码提交建议：

```powershell
git commit -m "Prepare .NET 10 and Store distribution channel"
```

## 七、PR / Release 前人工核对

提交前人工确认：

- `git status --short` 中没有 `.codex-temp/`、`artifacts/`、MSIX、证书、测试日志。
- `git status --short` 中没有 `store-assets-html/` 这类本地 Store 素材画布目录。
- 网站改动没有误混进应用提交。
- README 和 CHANGELOG 只写真实已经完成的内容。
- Store 版资源不包含捐赠二维码。
- Direct 版仍保留官网更新、网盘下载、捐赠入口。
- Store 版隐藏 Direct 更新器和捐赠入口。
- 生成包内的 Package/Publisher Identity 与 Partner Center 正式身份一致。

## 八、文档维护要求

以下情况发生时，必须更新本文档：

1. 新增或删除发布通道。
2. Direct 安装器、Store MSIX、更新服务或开机自启方案变化。
3. 新增发版脚本、证书目录、打包输出目录。
4. 官网是否跟随应用仓库一起提交的策略变化。
5. README、CHANGELOG、宣传文案、官网截图的存放目录变化。
6. Store 政策相关资源处理方式变化，例如捐赠入口、外部下载入口、支付入口。
7. `.gitignore` 中新增或删除与发版相关的忽略规则。

维护原则：凡是会影响“这次发版到底该提交什么”的变化，都要同步改本文档。
