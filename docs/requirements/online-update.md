# DeskBox 在线更新方案

## 背景

DeskBox 当前是 WinUI 3 / .NET 8 桌面应用，发布方式为 Inno Setup 安装包。安装包支持覆盖安装，并会保留用户设置、格子配置和收纳目录内容。因此第一阶段不建议做复杂的差分更新或热替换，优先采用“检查更新 -> 展示更新日志 -> 下载新版安装包 -> 校验 -> 启动安装器覆盖安装”的稳定方案。

用户已有一台 VPS 和备案域名，后续还计划建设官网。国内访问 GitHub 不稳定，因此更新清单和安装包下载应优先走自有域名。

## 备份资料判断

备份目录：`E:/桌面文件收纳/临时文件/hermes-backup-20260609/`

可用信息：

- 发现 `vps-nginx-timepic.top.conf`，里面有 `timepic.top` / `www.timepic.top` 的 Nginx 配置。
- 当前配置是 80 端口 HTTP，根目录为 `/opt/sallo-research/client/dist`，并将 `/api/` 反代到 `127.0.0.1:3001`。
- 这说明旧 VPS 至少曾经运行过“Nginx 静态站点 + 本地 API 服务”的部署结构，适合承载官网、更新清单、安装包下载入口。
- 备份内有 SSH 公私钥和 `.env` 等敏感文件，不能提交到仓库，也不建议继续长期使用旧私钥。

不能直接复用的地方：

- 没有发现现成的 DeskBox 更新服务。
- 旧 Nginx 配置没有 HTTPS 段，正式更新必须使用 HTTPS。
- 旧配置服务的是 `sallo-research`，路径和站点名称都需要为 DeskBox 重新规划。
- `hosts` 文件主要是内网/测试域名映射，对面向用户的更新服务没有直接价值。

结论：这份备份可以作为“恢复 VPS 登录和参考 Nginx 配置”的线索，但 DeskBox 更新系统需要新建目录、域名和发布流程。

## 推荐部署结构

建议使用两个稳定子域名：

- `update.your-domain.com`：只负责更新清单、版本日志 API，轻量、长期兼容。
- `download.your-domain.com`：负责安装包下载，后续可迁移到对象存储或 CDN。

也可以先用一个域名：

- `https://deskbox.your-domain.com/update/stable/win-x64.json`
- `https://deskbox.your-domain.com/download/DeskBox_Setup_1.0.5_x64.exe`
- `https://deskbox.your-domain.com/releases/1.0.5`

第一版更推荐静态文件方案，不需要数据库和后端服务：

```text
/var/www/deskbox/
  index.html
  update/
    stable/
      win-x64.json
      win-arm64.json
  releases/
    1.0.5.json
    1.0.5.html
  downloads/
    DeskBox_Setup_1.0.5_x64.exe
```

后续官网复杂后，再把官网前端、后台、更新清单分开即可。

## 更新清单格式

客户端检查更新时请求：

```text
GET https://update.your-domain.com/deskbox/v1/stable/win-x64.json
```

返回示例：

```json
{
  "manifestVersion": 1,
  "channel": "stable",
  "platform": "win-x64",
  "latestVersion": "1.0.5",
  "minimumVersion": "1.0.0",
  "publishedAt": "2026-06-18T12:00:00+08:00",
  "force": false,
  "rolloutPercent": 100,
  "download": {
    "url": "https://download.your-domain.com/deskbox/DeskBox_Setup_1.0.5_x64.exe",
    "fileName": "DeskBox_Setup_1.0.5_x64.exe",
    "size": 22546453,
    "sha256": "..."
  },
  "releaseNotes": {
    "title": "DeskBox 1.0.5",
    "summary": "优化上传场景、快捷键唤起和收纳目录入口。",
    "url": "https://deskbox.your-domain.com/releases/1.0.5",
    "items": [
      {
        "type": "feature",
        "text": "新增全局快捷键唤起格子，默认 F7，可在设置中关闭或修改。"
      },
      {
        "type": "improvement",
        "text": "新增打开收纳目录和固定到资源管理器快速访问入口。"
      },
      {
        "type": "fix",
        "text": "优化托盘左键临时置顶后点击其他窗口的层级恢复逻辑。"
      }
    ]
  }
}
```

字段设计原则：

- `manifestVersion` 用于以后扩展清单格式，老客户端不认识的新字段应忽略。
- `minimumVersion` 用于标记最低可升级版本。
- `force` 只在严重问题或安全问题时使用。
- `rolloutPercent` 用于灰度发布。
- `sha256` 是必须项，客户端下载完成后必须校验。
- `releaseNotes.items` 直接用于应用内展示更新日志。
- `releaseNotes.url` 用于打开官网完整日志。

## 应用内体验

设置页或关于页增加“检查更新”区域：

- 显示当前版本。
- 按钮：`检查更新`。
- 检查中显示加载状态。
- 已是最新版：显示“当前已是最新版本”。
- 有新版本：显示版本号、发布时间、更新摘要、功能点列表。
- 操作按钮：`下载更新` / `稍后再说` / `查看完整日志`。

托盘菜单可选增加：

- `检查更新`

不要在启动时直接打扰用户。建议后台静默检查，但只有发现新版本后，在设置页、托盘菜单或轻量 Toast 中提示。

## 下载与安装流程

第一阶段建议：

1. 客户端请求更新清单。
2. 对比本地版本和 `latestVersion`。
3. 有新版本时展示更新日志。
4. 用户点击下载。
5. 下载到 `%LocalAppData%/DeskBox/updates/`。
6. 下载完成后计算 SHA-256。
7. 校验通过后启动安装器。
8. 当前应用退出。
9. Inno Setup 覆盖安装。

安装器参数可以先使用普通交互模式。后续如要降低打扰，可研究 `/SILENT`，但由于当前安装目录在 `Program Files`，仍可能触发 UAC。

## 安全策略

第一版最低要求：

- 全部使用 HTTPS。
- 安装包下载后必须校验 SHA-256。
- 更新清单域名长期稳定，不直接写 VPS IP。
- 下载失败、校验失败、安装失败时不影响当前版本继续使用。

第二阶段建议：

- 对更新清单增加签名，例如 Ed25519。
- 公钥内置在客户端，私钥只在发布机器上保存。
- 客户端先验签，再信任下载地址、版本号和 SHA-256。

代码签名：

- 如果安装包没有代码签名，Windows SmartScreen 仍可能提示未知发布者。
- 代码签名不是在线更新的硬性前置，但会明显影响安装体验。
- 早期可以先不买，等公开用户量上来后再购买代码签名证书。

## 服务器迁移兼容

核心原则：客户端只内置稳定域名，不内置 IP。

比如所有版本都访问：

```text
https://update.your-domain.com/deskbox/v1/stable/win-x64.json
```

以后换 VPS、换对象存储、换 CDN，只要保持这个域名和路径仍能访问，老版本就能继续检查更新。

为了兼容老版本：

- 不要删除旧的 `/deskbox/v1/...` 路径。
- 新字段只追加，不改变旧字段含义。
- 旧安装包和旧 release notes 至少保留一段时间。
- 如果必须换域名，旧域名要长期保留并返回跳转或新的清单。

## 成本预估

当前 DeskBox 安装包大约 22.5 MB。

粗略流量：

- 100 次下载约 2.25 GB。
- 1000 次下载约 22.5 GB。
- 10000 次下载约 225 GB。

早期直接放 VPS：

- 成本主要是 VPS 已有费用、域名、备案、HTTPS 证书。
- 风险是带宽峰值、下载慢、服务器故障。

用户量起来后：

- 安装包迁到对象存储/CDN。
- 更新清单仍可由 VPS 或静态站提供。
- 客户端不用变，只更新清单里的 `download.url`。

## 实施优先级

P0：服务端静态更新清单

- 新建 `stable/win-x64.json`。
- 新建 release notes JSON/HTML。
- Nginx 配置 HTTPS、缓存策略和下载路径。

P1：客户端检查更新

- 增加 `UpdateService`。
- 增加版本比较、网络请求、超时、错误处理。
- 设置页展示最新版本和更新日志。

P2：下载与校验

- 增加下载进度。
- 下载到本地 update 缓存目录。
- 校验 SHA-256。
- 启动安装器并退出当前应用。

P3：发布脚本

- 构建安装包。
- 自动计算 SHA-256 和文件大小。
- 自动生成 `win-x64.json`。
- 上传安装包和清单到服务器。

P4：增强安全

- 清单签名。
- 灰度发布。
- 多下载镜像。
- 强制更新策略。

## 近期建议

第一版先做静态清单和应用内更新弹窗，不做后台强制更新。用户点击“检查更新”后能看到：

- 最新版本号。
- 发布时间。
- 更新摘要。
- 功能点、优化、修复列表。
- 下载更新按钮。
- 查看完整日志链接。

这能覆盖当前真实需求，同时保持实现成本和风险都比较低。
