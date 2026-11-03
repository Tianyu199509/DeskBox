# 自定义格子（Custom Widget）远程模板库与 AI 提示词 OTA 运维手册

本文档面向开发者与自动化编程 Agent，定义了在不发客户端新版安装包的前提下，通过**官网（`deskbox.fun`）+ GitHub 双通道**远程上架新格子、推送现有官方模板热更新、下架/恢复模板，以及更新 AI 生成提示词规则的完整标准流程。

---

## 一、双通道架构与核心路径对照

客户端启动后（延迟 15 秒及此后每 6 小时）、打开「格子工坊」时（30 分钟节流冷却），或用户点击工坊右上角「↻ 同步」按钮（`force: true` 携带 `Cache-Control: no-cache` 强制穿透）时，按**主通道优先、GitHub 镜像自动兜底**顺序拉取：

| 资源类型 | 1. 官网主通道（国内低延迟首选） | 2. GitHub 兜底镜像（`main` 分支） | 仓库内源文件 | 客户端解析服务 |
| --- | --- | --- | --- | --- |
| **官方格子模板目录** | `https://deskbox.fun/widgets/catalog.v1.json` | `https://raw.githubusercontent.com/Tianyu199509/DeskBox/main/catalog/widgets.v1.json` | `catalog/widgets.v1.json` | `CustomWidgetRemoteCatalogService` |
| **AI 提示词与规范包** | `https://deskbox.fun/widgets/prompts.v1.json` | `https://raw.githubusercontent.com/Tianyu199509/DeskBox/main/catalog/prompts.v1.json` | `catalog/prompts.v1.json` | `CustomWidgetRemotePromptService` |

> **关键路径差异提醒（必读）**：
> - 仓库内的模板目录文件名为 **`catalog/widgets.v1.json`**；
> - 官网服务器部署的目标 URL 路径为 **`https://deskbox.fun/widgets/catalog.v1.json`**（文件名是 `catalog.v1.json`，不是 `widgets.v1.json`）。
> - 使用发布脚本时加上 `--website-out ./artifacts/website-widgets/catalog.v1.json` 即可自动生成同名官网部署副本，避免手误改错文件名。

---

## 二、内置官方模板清单（共 10 个）

客户端安装包内自带以下 10 个官方模板（源码位于 `src/DeskBox/Assets/CustomWidgets/Templates/<id>/`）。当 `catalog/widgets.v1.json` 为空数组 `[]` 时，客户端默认展示本地内置版本；一旦远程目录中出现同 `id` 记录，客户端按版本号与下架标志合并：

| 短 ID（`--builtin` 参数值） | Manifest ID | 默认名称 | 工坊分类 |
| --- | --- | --- | --- |
| `hydration` | `starter.hydration` | 喝水打卡 | 习惯打卡 (`Habits`) |
| `pomodoro` | `deskbox.pomodoro` | 番茄专注 & 久坐提醒 | 习惯打卡 (`Habits`) |
| `countdown` | `deskbox.countdown` | 倒数日 | 效率小工具 (`Tools`) |
| `world-clock` | `deskbox.world-clock` | 世界时钟 | 效率小工具 (`Tools`) |
| `dev-toolbox` | `deskbox.dev-toolbox` | 开发者工具箱 | 效率小工具 (`Tools`) |
| `sys-pulse` | `deskbox.sys-pulse` | 系统监控 | 实时数据 (`Live`) |
| `news-rss` | `starter.news-rss` | 热点资讯 | 实时数据 (`Live`) |
| `ai-inbox` | `starter.ai-inbox` | AI 随手译与润色 | AI 助手 (`Ai`) |
| `ai-pulse` | `deskbox.ai-pulse` | AI 监控 | AI 助手 (`Ai`) |
| `agent-hud` | `starter.agent-hud` | Agent 任务看板 | AI 助手 (`Ai`) |

---

## 三、常用操作标准流程（CLI 一键执行）

所有目录增删改查统一通过仓库脚本 `scripts/publish_widget_catalog.py` 完成，**禁止手写拼接未校验的 JSON**。

### 场景 1：查看与静态校验当前远程目录

```powershell
# 查看当前 catalog/widgets.v1.json 中的所有模板、版本与在架状态
python scripts/publish_widget_catalog.py list

# 静态校验 schemaVersion、语义化版本号、manifest 结构及字节体积上限
python scripts/publish_widget_catalog.py validate
```

### 场景 2：修改内置模板并向全量用户推送热更新（Hotfix / 升级）

1. 修改 `src/DeskBox/Assets/CustomWidgets/Templates/<id>/` 下的 `manifest.json`、`logic.js` 或 `locales/*.json`。
2. 运行 `upsert --builtin` 自动提取完整工作区包并递增版本号（**注意：新 `version` 必须严格大于客户端内置及用户桌面现有版本号，否则客户端不会触发升级**）：

```powershell
# 示例：将 pomodoro 版本号自动 +0.0.1（patch），同时输出官网部署文件
python scripts/publish_widget_catalog.py `
  --website-out ./artifacts/website-widgets/catalog.v1.json `
  upsert --builtin pomodoro --bump patch
```

- 也可显式指定版本号：`--version 5.1.0`（或 `--bump minor` / `--bump major`）。
- 建议同步把更新后的版本号写回 `src/DeskBox/Assets/CustomWidgets/Templates/<id>/manifest.json`，保持下一次发客户端安装包时的基线一致。
- **多语言标题保留机制**：若仅修复布局或 JS 逻辑、未修改模板中文名，工坊会自动保留客户端内置的 13 语种标题词条；若修改了 `name` / `description` 或 `locales/*.json` 中的 `meta.name` / `meta.description`，工坊卡片文案会同步更新，并在卡片标题旁展示「已更新」微标。

### 场景 3：远程上架一个全新的官方模板（免发客户端安装包）

1. 准备一个符合 DeskBox V2 规范的格子文件夹（例如 `./my-widgets/habit-ring/`），目录结构如下：
   ```text
   habit-ring/
   ├── manifest.json       # 必选：含 schemaVersion: 1, id, name, defaultSize, view 等
   ├── logic.js            # 可选：Jint 沙箱脚本（不得超过 128 KB）
   └── locales/            # 可选：多语言词条目录（建议至少包含 zh-CN.json 与 en-US.json）
       ├── _meta.json
       ├── zh-CN.json
       └── en-US.json
   ```
2. 执行 `upsert --dir` 打包进远程目录：

```powershell
python scripts/publish_widget_catalog.py `
  --website-out ./artifacts/website-widgets/catalog.v1.json `
  upsert --dir ./my-widgets/habit-ring --id habit-ring --version 1.0.0
```

- 若该模板依赖新版客户端才有的组件或数据源，**必须**加上 `--min-app-version 1.x.y`（见下方「版本门禁」章节）。
- 上架后，客户端工坊「精选模板」会自动出现该卡片（带「新」微标），并按 ID/名称关键词自动归入对应分类（`ai/agent/llm` -> AI 助手，`rss/news/monitor/weather/stock/hardware` -> 实时数据，`habit/water/check/todo/pomodoro` -> 习惯打卡，其余 -> 效率小工具）。

### 场景 4：远程下架模板（`deprecate` 与 `remove` 的区别）

```powershell
# 推荐：将模板标记为下架（同时适用于「内置 10 个模板」和「纯远程新增模板」）
python scripts/publish_widget_catalog.py `
  --website-out ./artifacts/website-widgets/catalog.v1.json `
  deprecate --id hydration
```

> **切勿用 `remove` 下架内置模板！**
> - `deprecate --id <id>` 会在远程目录中保留/写入 `"deprecated": true` 墓碑记录。客户端读到 `deprecated: true` 后，会在工坊画廊与协议安装入口中隐藏该模板（哪怕它是安装包内置的 10 个模板之一）。
> - `remove --id <id>` 会直接从 JSON 数组中删掉该条目：
>   - 如果删掉的是**内置模板**，客户端见不到墓碑记录，会重新回退显示本地内置版！
>   - 如果删掉的是**曾推送过的纯远程模板**，正常同步后会从工坊消失（不会影响用户已添加到桌面的实例）。因此 `remove` 仅用于清理从未正式对外或不再需要墓碑的纯远程条目。

### 场景 5：恢复已下架模板

```powershell
python scripts/publish_widget_catalog.py `
  --website-out ./artifacts/website-widgets/catalog.v1.json `
  undeprecate --id hydration
```

- 注意：如果此前对一个未曾在远程发布完整包体的内置模板执行过 `deprecate`（仅生成了 `999.0.0` 墓碑记录），恢复上架内置原版时请使用 `remove --id <内置ID>` 移除墓碑记录，或重新执行 `upsert --builtin <内置ID> --version <实际版本>`。

---

## 四、客户端热升级行为与用户数据安全边界

Agent 在协助规划或答复模板变更影响时，请严格依据以下客户端真实机制（见 `CustomWidgetViewModel.ApplyPendingRemoteCatalogUpgradeAsync` 与 `CustomWidgetDataStore`）：

1. **工坊画廊（未安装态）**：
   - 同步完成后，工坊底部网格局部无感刷新；悬停卡片 Tooltip 可直接核对生效的 `vX.Y.Z` 版本号与来源状态（`官方内置` / `官方云端更新` / `官方云端新上架`）。
2. **用户桌面运行中实例（已安装态）**：
   - **自动热升级条件**：实例满足 `origin == "official"` 且 `isCustomized == false`，且远程非下架条目的 `version` **严格大于**实例当前 `manifest.version`，且客户端版本满足 `minAppVersion` / `minSchemaVersion`。
   - **用户数据零丢失**：热升级仅原子替换实例目录下的 `manifest.json`、`logic.js` 与 `locales/`，**绝不覆盖 `store.json` 中的用户运行时状态（打卡记录、列表数据）、用户设置值（`settings`）以及 DPAPI 加密凭证（`secrets`）**。若新模板退役了旧版的 `logic.js`，更新时会自动清理残留旧脚本。
   - **用户魔改保护**：只要用户在工坊里用 AI 或代码编辑器改动并保存过该格子（`isCustomized: true`），客户端**永远跳过**远程自动覆盖，100% 保护用户的个人定制成果。
   - **下架不删桌面**：`deprecated: true` 仅从工坊新装列表隐藏，**绝不会删除或停用用户桌面上已经放置的实例**。

---

## 五、安全白名单、版本门禁与体积红线（违反将被客户端静默丢弃）

客户端在 `CustomWidgetNetworkSecurity` 与 `CustomWidgetScriptValidator` 中执行零信任校验，**任何一条不合规都会导致该远程条目被客户端静默忽略并回退本地缓存**：

1. **受信任域名硬白名单（`IsTrustedOfficialRemoteHost`）**：
   - 协议必须为 `https://`（默认 443 端口），且禁止重定向到非白名单域名或内网/回环 IP。
   - 若目录条目使用外链字段（`bundleUrl` / `manifestUrl` / `logicUrl`）或官网使用 `deskbox://install-widget?url=...` 深链，URL 域名**只能**是：
     - `https://deskbox.fun/...` 或 `https://*.deskbox.fun/...`
     - `https://raw.githubusercontent.com/Tianyu199509/DeskBox/...` 或 `https://github.com/Tianyu199509/DeskBox/...`
   - 严禁把包体放到第三方 OSS、Gist 或其他 GitHub 仓库路径下，否则客户端安全层会直接拦截。
   - **最佳实践**：日常中小型模板优先直接用 `publish_widget_catalog.py upsert` 生成内联 `manifest` + `logicJs` + `locales`（单次请求原子拉取，无二次子资源请求失败风险）；仅当包含多语言大包或二进制资产时再使用 `bundleUrl`（`.dbbox`）。
2. **硬性体积上限**：
   - `catalog.v1.json` 整体响应体：`<= 512 KB`（若未来模板数量增长接近 `400 KB`，应将大模板改为 `bundleUrl` 外链 `.dbbox` 模式以保持主目录轻量）
   - 单个 `manifest.json`：`<= 512 KB`
   - 单个 `logic.js`：`<= 128 KB`（且必须通过 Jint 沙箱 `onInit` 干跑校验）
   - 单个模板 `locales` 总和：`<= 1 MB`
   - 外链 `.dbbox` 包（`bundleUrl`）：`<= 4 MB`
3. **向前兼容门禁（`minAppVersion` / `minSchemaVersion`）**：
   - 如果新模板使用了新版本客户端才支持的视图节点或数据源，必须在 `upsert` 时传入 `--min-app-version <x.y.z>`。
   - 旧版客户端拉取后：
     - 若是**内置模板升级**，旧客户端会自动忽略高版本远程包，继续安全运行本地内置版；
     - 若是**纯远程新模板**，旧客户端会在工坊显示置灰卡片并提示需升级 DeskBox 客户端版本，不会崩溃。

---

## 六、官网（`deskbox.fun`）与 GitHub 完整部署上线步骤

每次完成模板调整后，Agent 请按以下 Checklist 依次执行：

### 第 1 步：本地静态校验与沙箱回归测试

```powershell
# 1. 校验远程目录 JSON 结构、版本号与字节上限，并导出官网副本
python scripts/publish_widget_catalog.py `
  --website-out ./artifacts/website-widgets/catalog.v1.json `
  validate

# 2. 运行 x64 沙箱与远程目录合并回归测试（确保所有内联脚本通过 Jint onInit 干跑）
dotnet test .\tests\DeskBox.Tests\DeskBox.Tests.csproj `
  --no-restore --verbosity:minimal -p:Platform=x64 -p:RuntimeIdentifier=win-x64 /nr:false `
  --filter "FullyQualifiedName~Phase35FinalHardeningRegressionTests|FullyQualifiedName~Phase3EcosystemFlywheelTests"
```

### 第 2 步：推送 GitHub 镜像通道（`Tianyu199509/DeskBox`）

1. 确认 `catalog/widgets.v1.json`（及可选的 `catalog/prompts.v1.json`）变更无误。
2. 提交并推送至 `main` 分支：
   - **红线约束**：根据仓库 `.githooks/commit-msg` 与 CI 门禁，Commit Message **严禁包含任何 `Co-Authored-By`、`Generated with` 或 AI/工具署名**，提交者身份保持 `Simon <1047078635@qq.com>`。
3. 推送后，GitHub Raw 边缘缓存通常在 1～5 分钟内刷新：`https://raw.githubusercontent.com/Tianyu199509/DeskBox/main/catalog/widgets.v1.json`。

### 第 3 步：部署官网主通道（`deskbox.fun`）

1. 将 `./artifacts/website-widgets/catalog.v1.json` 上传覆盖至官网静态站点目录对应的路径：
   - 目标地址：**`https://deskbox.fun/widgets/catalog.v1.json`**
   - 若同步修改了 AI 提示词包，将 `catalog/prompts.v1.json` 上传至：**`https://deskbox.fun/widgets/prompts.v1.json`**
2. 官网静态服务器 / CDN 响应头要求：
   - `Content-Type: application/json; charset=utf-8`
   - 保留标准 `ETag` 响应头（客户端后台轮询依赖 `If-None-Match` 实现 `304 Not Modified` 零流量检查）
   - 建议 `Cache-Control: public, max-age=300`（5 分钟短缓存，兼顾 CDN 命中率与工坊手动强制同步实时性；若使用 Cloudflare 等 CDN，加急发版后可对 `/widgets/catalog.v1.json` 执行一次单 URL Purge Cache）
   - **切勿开启跨站重定向到非 `*.deskbox.fun` 域名**。

### 第 4 步：端到端真机验收

1. 打开 DeskBox -> 进入「格子工坊」；
2. 点击「精选模板」栏右上角的 **「↻」同步按钮**；
3. 核对按钮旁提示 `已同步 N 项更新`，并将鼠标悬停在目标模板卡片上，确认 Tooltip 显示的版本号已变为最新发布的 `vX.Y.Z`。

---

## 七、附：官网页面一键安装深链协议（`deskbox://`）与远程提示词包

### 7.1 官网展示页唤醒客户端深链格式

若在官网（`deskbox.fun`）制作模板画廊网页，「一键添加到桌面」按钮可直接使用客户端已注册的 `deskbox://` 协议：

- **按模板 ID 安装（推荐，自动先强制同步最新远程目录再安装）**：
  ```text
  deskbox://install-widget?id=habit-ring
  ```
- **按受信任 HTTPS 直链安装 `.dbbox` 或 `manifest.json`**：
  ```text
  deskbox://install-widget?url=https%3A%2F%2Fdeskbox.fun%2Fwidgets%2Fpackages%2Fhabit-ring.dbbox
  ```
- **打开格子工坊并预填 AI 创作提示词**：
  ```text
  deskbox://open-studio?idea=%E5%81%9A%E4%B8%80%E4%B8%AA%E6%9E%81%E7%AE%80%E8%82%A1%E7%A5%A8%E7%9C%8B%E6%9D%BF
  ```

### 7.2 远程热更新 AI 提示词规则（`catalog/prompts.v1.json`）

当发现某款新大模型生成格子的特定写法容易出错、需要补充 Prompt 约束而不想重发客户端时，编辑 `catalog/prompts.v1.json` 并同步部署至 `https://deskbox.fun/widgets/prompts.v1.json`：

```json
{
  "schemaVersion": 1,
  "promptVersion": 2,
  "minAppVersion": "1.6.0",
  "maxAppVersion": null,
  "updatedAtUtc": "2026-11-08T12:00:00Z",
  "extraDesignRules": "补充追加到内置提示词末尾的设计与布局硬规则...",
  "systemPrompt": null,
  "skillMarkdown": null,
  "webPromptTemplateZh": null,
  "webPromptTemplateEn": null,
  "repairPromptTemplateZh": null,
  "repairPromptTemplateEn": null
}
```

- **生效条件**：`schemaVersion` 必须为 `1`，且 **`promptVersion` 必须严格大于客户端内置 `BuiltInPromptVersion`（当前为 `1`，即至少设为 `2` 并每次递增）**。
- 为 `null` 的字段会自动回退使用客户端内置版本，建议优先只填 `"extraDesignRules"` 做增量规则追加，风险最小、兼容性最好。
