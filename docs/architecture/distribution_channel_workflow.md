# DeskBox 双通道开发与打包操作手册

日期：2026-07-06

本文档用于后续开发、调试和发布 DeskBox 时区分两个发布通道：

- 官网/GitHub 版，也称 Direct 版。
- Microsoft Store 版，也称 Store 版。

两个通道共用大部分业务代码，但更新方式、开机自启、安装包形态、关于页入口、商店政策约束和本地验证方式不同。后续新增功能时，默认需要确认它在两个通道下的行为是否一致，或者是否需要显式分流。

## 一、通道总览

| 项目 | Direct 官网版 | Microsoft Store 版 |
| --- | --- | --- |
| 默认构建通道 | 是 | 否，需要显式传入 `DeskBoxDistribution=Store` |
| 安装形态 | Inno Setup 安装包 | MSIX / Partner Center |
| 更新方式 | 应用内更新 + `DeskBox.Updater.exe` + Inno 覆盖安装 | `Windows.Services.Store.StoreContext` + Microsoft Store 更新 |
| 开机自启 | HKCU Run 注册表 | MSIX `StartupTask` |
| 包身份 | 无 package identity | 有 package identity |
| 数据路径 | 当前继续使用 DeskBox 自有本地数据目录 | 首版 Store 也继续使用相同数据目录，避免用户切换通道后数据丢失 |
| 关于页渠道文案 | `官网版` | `Microsoft Store` |
| 商店支持入口 | 显示，跳转付费 Microsoft Store 版 | 隐藏 |
| 国内网盘入口 | 可显示 | 不作为独立卡片显示，更新失败时只提供合适提示 |
| Updater 产物 | 构建并复制 | 不构建、不打包 |
| 运行时依赖 | 安装器检测 .NET / Windows App Runtime | Store 包建议自包含 .NET，Windows App Runtime 由包依赖或 Store 处理 |

当前项目使用的关键 MSBuild 属性：

```xml
<DeskBoxDistribution Condition="'$(DeskBoxDistribution)' == ''">Direct</DeskBoxDistribution>
<DefineConstants Condition="'$(DeskBoxDistribution)' == 'Store'">$(DefineConstants);DESKBOX_STORE</DefineConstants>
```

规则：

- 不传 `DeskBoxDistribution` 时，一律是 Direct。
- 传 `-p:DeskBoxDistribution=Store` 时，启用 Store 编译常量和 MSIX 相关配置。
- 不要在业务代码里到处写 `#if DESKBOX_STORE`。优先通过 `AppDistributionService`、`IAppUpdateService`、`IStartupService` 等服务边界分流。

## 二、开发时怎么跑

### 2.1 日常开发优先跑 Direct

日常 UI、格子、设置、文件、待办、随记、音乐等功能开发，默认跑 Direct 版即可。

```powershell
dotnet build .\src\DeskBox\DeskBox.csproj `
  -c Debug `
  -p:Platform=x64 `
  -v:minimal
```

日常交互调试统一使用脚本启动：

```powershell
.\scripts\start-debug.ps1
```

如果希望启动前顺手重新构建：

```powershell
.\scripts\start-debug.ps1 -Build
```

注意：

- 日常 Debug 启动脚本会固定运行 `src\DeskBox\bin\x64\Debug\<TargetFramework>\DeskBox.exe`。
- Debug 默认使用按 worktree 路径生成的独立 `%LOCALAPPDATA%\DeskBox-Dev\...` 数据目录；不同随记/待办工作树可以同时运行，不会读写正式版数据。需要显式复用正式数据时才使用 `-UseProductionData`，也可用 `-DataRoot` 指定调试数据目录。
- 不要手动运行 `src\DeskBox\bin\x64\Debug\<TargetFramework>\win-x64\DeskBox.exe`，这个目录可能残留旧 RID 构建产物，容易出现“进程启动后立刻崩溃”或“跑的不是最新代码”。
- 只有 Release 发布、Direct 安装器产物、Store/MSIX 打包检查需要显式使用 `RuntimeIdentifier`。

预期：

- 关于页版本行显示 `官网版`。
- 商店支持入口显示，并跳转 Microsoft Store 产品页。
- 应用内更新使用 Direct 下载/安装逻辑。
- 开机自启走注册表。
- 输出目录包含 `DeskBox.Updater.exe`。

### 2.2 Store 编译检查

Store 通道要至少做一次编译检查，确认 Store 专用服务和资源没有编译错误。

```powershell
dotnet build .\src\DeskBox\DeskBox.csproj `
  -c Debug `
  -p:Platform=x64 `
  -p:RuntimeIdentifier=win-x64 `
  -p:DeskBoxDistribution=Store `
  -v:minimal
```

注意：

- 未打包的 Store Debug 输出不一定能直接运行。
- 如果直接运行普通 `DeskBox.exe` 出现 Windows App Runtime 初始化异常，优先按 MSIX 方式验证。
- Store 更新、Store 开机自启、package identity 相关能力必须在 MSIX 安装后测试。

### 2.3 Store 本地真实验证

Store 通道的真实验证方式是构建 MSIX，然后安装运行。

```powershell
.\scripts\build-store-msix.ps1 `
  -Configuration Release `
  -Platform x64
```

默认输出：

```text
artifacts\store-msix\
```

如果只是本地验证，可以使用临时证书签名。正式提交 Store 时必须使用 Partner Center 对应的包身份和发布流程。

本地安装后启动方式：

```powershell
$pkg = Get-AppxPackage DeskBox.Desktop
Start-Process "shell:AppsFolder\$($pkg.PackageFamilyName)!App"
```

预期：

- 进程路径在 `C:\Program Files\WindowsApps\...`。
- 关于页版本行显示 `Microsoft Store`。
- 商店支持入口隐藏。
- `DeskBox.Updater.exe` 不在包内。
- Store 更新入口显示为商店更新逻辑。
- 开机自启走 `StartupTask`。

## 三、打包流程

### 3.1 发版前公共步骤

两个通道发布前都要做：

1. 更新版本号。
   - `src/DeskBox/DeskBox.csproj`
   - `src/DeskBox/Package.appxmanifest`
   - Inno 脚本中的版本信息
   - README / CHANGELOG 中的版本说明

2. 跑基础构建和测试。

```powershell
dotnet build .\src\DeskBox\DeskBox.csproj `
  -c Debug `
  -p:Platform=x64 `
  -p:RuntimeIdentifier=win-x64 `
  -v:minimal

dotnet test .\DeskBox.sln `
  -c Debug `
  -p:Platform=x64 `
  -p:RuntimeIdentifier=win-x64 `
  -v:minimal
```

3. 检查 Git 范围。
   - 可以提交：`src/`、`installer/`、`scripts/`、`tests/`、`docs/architecture/`、README、CHANGELOG。
   - 不要提交：`.codex-temp/`、`artifacts/`、`bin/`、`obj/`、本地签名 MSIX、`.cer`、`.pfx`、`store-assets-html/`、临时截图和本地草稿。
   - 网站 `deskbox-site/` 是否提交要单独决定，不要混进应用发版提交里。

### 3.2 Direct 官网版打包

Direct 官网版继续使用现有 Inno 链路，但零售载荷统一采用 Full Native AOT。

关键要求：

- `DeskBoxDistribution` 保持默认 `Direct`。
- 需要构建并复制 `DeskBox.Updater.exe`。
- 安装器携带匹配架构的 Windows App Runtime，不再下载 `.NET 10` 或 Windows App Runtime。
- 应用内更新 manifest 指向标准命名的 Full Native AOT Direct 安装包。
- 关于页保留官网、GitHub、捐赠等入口。

验收清单：

- 干净机器能安装并启动。
- 覆盖安装旧版本后数据保留。
- 应用内检查更新、下载、安装、重启链路正常。
- 完全离线机器能安装并启动；升级清理只删除历史载荷清单中的旧程序文件。
- 开机自启开关有效。
- 托盘、快捷键、多屏/DPI、文件拖拽、系统音量等底层能力正常。

### 3.2.1 Direct 应用内更新发布清单

Direct 应用内更新默认先读取：

```text
https://deskbox.fun/update/stable.json
```

如果该清单不可用，客户端会兜底读取 GitHub 最新 Release API。官网清单仍然是主通道，因为它可以控制稳定版本、国内网盘入口、SHA-256、灰度和回滚；GitHub 兜底只用于防止清单漏发时完全无法检查更新。

产物命名契约：所有已发布版本的更新器（1.4.3 起）只接受以 `_x64.exe`/`_arm64.exe` 结尾的资产名与清单 URL，因此 `build-stage-7c1-distribution.ps1` 产出的安装包名保持 `DeskBox_Setup_<版本>_<架构>.exe`，发布时**不得追加** `_NativeAot` 等风味后缀。若清单 `downloadUrl` 使用不以 `DeskBox_Setup_` 开头的重定向地址，客户端会放行（用于网盘/CDN 中转）。

离线安装说明：从 1.4.8 起，直发安装包统一为 Full Native AOT，内置匹配架构的 Windows App Runtime 组件，不再下载 .NET 10 或 Windows App Runtime。文件名仍保持 `DeskBox_Setup_<version>_x64.exe` 与 `DeskBox_Setup_<version>_arm64.exe`，不再维护 `_Full` 变体。发布载荷包含 `DeskBox.InstallManifest.txt`；覆盖升级时安装器只删除“上一版清单存在、当前清单不存在”的旧程序文件。对没有清单的历史 Direct/Full 安装，只使用仓库中的精确兼容清单清理已知旧载荷文件，不扫描或删除未知文件。

每次发布 Direct 版本时必须执行：

1. 发布 GitHub Release，并上传：
   - `DeskBox_Setup_x.y.z_x64.exe`
   - `DeskBox_Setup_x.y.z_x64.exe.sha256`

2. 核对 GitHub Release 资产：
   - tag 是 `vx.y.z`
   - Release 不是 Draft
   - Release 不是 Prerelease，除非刻意做预发布
   - 安装包大小和本地 `Output` 一致
   - 安装包 digest / `.sha256` 和本地 `Get-FileHash` 一致

3. 更新并部署官网清单：
   - `deskbox-site/public/update/stable.json`
   - `version`
   - `downloadUrl`
   - `sha256`
   - `size`
   - `releaseNotesUrl`
   - `summary`

4. 部署后从公网验证：

```powershell
curl.exe -i https://deskbox.fun/update/stable.json
```

预期：

- HTTP 200
- `Content-Type` 是 JSON
- `version`、`downloadUrl`、`sha256`、`size` 与 GitHub Release 完全一致

5. 用旧版本实机验证完整链路：
   - 检查更新
   - 下载更新
   - 点击安装
   - DeskBox 退出
   - 安装器继续执行
   - 安装完成后 DeskBox 重启
   - 数据和设置保留

6. 如果后续更新流程、清单字段、下载源、网盘链接、安装器参数或 GitHub 兜底策略有调整，必须同步更新本文档。

### 3.3 Microsoft Store 版打包

Store 版必须显式传入 Store 通道：

```powershell
.\scripts\build-store-msix.ps1 `
  -Configuration Release `
  -Platform x64
```

如果要签名：

```powershell
.\scripts\build-store-msix.ps1 `
  -Configuration Release `
  -Platform x64 `
  -SignPackage `
  -PackageCertificateKeyFile "path\to\DeskBox.pfx"
```

上架前必须替换 `src\DeskBox\Package.appxmanifest` 中的占位信息：

- `Identity Name`
- `Publisher`
- `PublisherDisplayName`
- 必要时同步 Store logo、tile、splash、隐私链接和应用说明。

Store 产品页截图/图标素材如果使用 `store-assets-html/` 生成，该目录只作为本地 HTML 画布。导出的 PNG 可以手动上传 Partner Center，但 `store-assets-html/` 本身不要提交，也不要进入 Direct 安装包或 Store MSIX。

验收清单：

- 包内没有 `DeskBox.Updater.exe`。
- 包内没有支付二维码资源。
- 关于页显示 `Microsoft Store`。
- 商店支持入口隐藏。
- 更新入口使用 Store 文案和 StoreContext 逻辑。
- `StartupTask` 声明存在，开机自启设置不写注册表。
- 运行 Windows App Certification Kit。
- Partner Center 包身份和 manifest 完全一致。

检查 MSIX 内是否误带资源：

```powershell
$msix = "path\to\DeskBox_版本_x64.msix"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($msix)
$zip.Entries | Where-Object {
  $_.FullName -like "*DeskBox.Updater*" -or
  $_.FullName -like "*donation-*" -or
  $_.FullName -like "*store-assets-html*"
} | Select-Object FullName
$zip.Dispose()
```

## 四、新功能开发规则

新增功能时，先问四个问题：

1. 这个功能是否涉及安装、更新、开机自启、文件系统、系统权限、Store 政策？
2. Direct 和 Store 是否应该表现一致？
3. 如果不一致，分流应该放在服务层还是 ViewModel 层？
4. 是否需要为两个通道分别写验证步骤？

推荐做法：

- 更新相关只通过 `IAppUpdateService`。
- 开机自启只通过 `IStartupService`。
- 通道判断只通过 `AppDistributionService`。
- 数据目录只通过 `DeskBoxDataPathService` 或现有统一数据入口。
- UI 尽量绑定 ViewModel 暴露的 `Visibility`、文案、命令，不在 XAML 里写复杂通道判断。
- Store 禁止或不适合出现的入口，优先在 ViewModel 层隐藏，并在 MSIX 包资源层二次移除。

不要做：

- 不要在多个页面散落 `#if DESKBOX_STORE`。
- 不要让 Store 版引用 `DeskBox.Updater`。
- 不要在 Store 包里带支付二维码、外部购买引导等可能触碰政策的资源。
- 不要在 Store 首版随意迁移数据目录。
- 不要用未打包 exe 验证 Store 更新和 StartupTask。

## 五、常见场景判断

### 5.1 为什么 Store Debug 版直接运行会退出？

Store 通道启用 MSIX / Windows App Runtime 相关能力后，普通未打包 exe 不一定具备完整运行环境。遇到 `REGDB_E_CLASSNOTREG` 或 Windows App Runtime 初始化错误时，不要先怀疑业务代码，先改用 MSIX 安装后验证。

### 5.2 为什么官网版能看到商店支持入口？

这是预期行为。官网版通过 Microsoft Store 产品页提供可选的付费支持入口，Store 通道自身不再重复展示。

### 5.3 本地签名证书怎么处理？

本地测试证书只用于开发机安装 MSIX，不要提交到仓库，不要用于正式发布。

如果后续需要清理本地测试包：

```powershell
Get-AppxPackage DeskBox.Desktop | Remove-AppxPackage
```

如果需要清理本地测试证书，先确认 thumbprint，再删除：

```powershell
Get-ChildItem Cert:\CurrentUser\My,Cert:\CurrentUser\Root,Cert:\CurrentUser\TrustedPeople,
  Cert:\LocalMachine\Root,Cert:\LocalMachine\TrustedPeople |
  Where-Object { $_.Subject -eq "CN=DeskBox" } |
  Select-Object Subject, Thumbprint, NotAfter
```

删除本机证书需要管理员权限，操作前要确认不是正式证书。

### 5.4 网站和应用发版要不要一起提交？

默认不要混在一起。

建议：

- 应用代码、安装器、Store 包、更新服务走一个提交。
- 官网内容、截图、SEO、下载页走另一个提交。
- 微信文章、公众号草稿、本地截图不进 Git。
- `store-assets-html/` 这类 Store 截图 HTML 画布不进 Git；需要保留给本机使用时依赖 `.gitignore` 排除。

## 六、推荐发布顺序

一次完整发版建议按这个顺序：

1. 完成业务开发。
2. Direct Debug 自测。
3. Store 编译检查。
4. 跑测试。
5. Direct Release / Inno 打包。
6. Store MSIX 打包。
7. 检查 Direct 安装包和 Store MSIX 包内容。
8. 在干净机器或虚拟机验证 Direct 安装。
9. 在本机或测试机安装 MSIX 验证 Store 版。
10. 更新 README / CHANGELOG。
11. 清理 Git 提交范围。
12. 提交应用代码。
13. 发布 GitHub Release / Direct 安装包。
14. Store 包走 Partner Center 提交流程。

这个顺序的核心是：先确认代码，再确认两个通道的包，最后再更新公开文档和发版。
