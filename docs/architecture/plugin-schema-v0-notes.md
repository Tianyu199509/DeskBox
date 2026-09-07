# DeskBox Plugin Schema v0.3 — 语义注记

配套 `plugin-schema-v0.json`。v0.x 是草案：给三路 spike（roadmap 阶段 3.5）、CLI validator（阶段 6）、商店规范（阶段 7）一个共同靶子，会迭代；契约测试只轻钉存在性与词汇+关键校验语义，不逐字段冻结。

## v0.2 → v0.3 变更（第六轮评审：声明式执行词汇，腿 1B 前置）

| 变更 | 内容 |
|---|---|
| 根级 `dataSources` | v0.3 仅 `http-json`：`{type, url, refreshSeconds≥10}`。**HTTPS-only**（声明式 fetcher 不得被引到明文）；**宿主代取**（runtime:none 包不执行第三方代码，宿主替它执行受控能力）；`network.fetch` 权限必须声明且 URL host 落在 scope.allow 内——install 期与运行期**双查** |
| 贡献级 `bindings` | payload 字段名 → `{source, path}`；path=最小 JSON path（`$.a.b[0].c`）。**拉取失败/路径缺失=保持 payload 回退值**（与 §13.5 per-element fallback 一致）——声明式 UI 永远有东西可渲染 |
| 根级 `actions` | v0.3 仅 `open-url`：`{type, url}`，字面量 HTTPS（模板化留 v1）。payload 里引用的 actionId 在 actions 映射存在时必须可解析（v0.2 老包无 actions 映射不追溯）。**消耗 `shell.open` 权限**（新权限词汇）且 URL host 须在 scope 内——roadmap §14"声明式≠无害"条款落地 |
| 权限消耗清单 | validator/安装器检查：http-json 数据源→network.fetch 声明+scope 容纳；open-url 动作→shell.open 声明+scope 容纳。**scope 匹配=host 精确匹配（小写）**，子域/通配符留 v1 |

**为什么现在做**：三路 spike 要公平比较，三腿必须做同一件事（真实 GitHub 数据流）；没有数据源/绑定/动作词汇，Declarative 腿只有静态数据，冷启动/延迟/AI 生成率等维度无从测起（第六轮评审裁定）。

## v0.1 → v0.2 变更（第四轮外部评审吸收）

| 变更 | 原因 |
|---|---|
| widget 贡献收进 `$defs/widgetContribution`，`required: [type, id, displayName, template]` + `additionalProperties: false` | v0.1 只有共享 `required: [type, id]`，`{"type":"widget","id":"x"}` 这种残缺贡献能过校验，多余字段也不报错；后续 command/ai-tool/settings 类型作为新增 `$defs` 条目加入，不改包级结构 |
| `signature` 块内部 `required: [contentHash, publisherSignature]` | 块整体可选（dev 模式）但**一旦存在必须完整**——v0.1 里 `"signature": {}` 是合法的 |
| 新增根级必填 `publisherPublicKey`（Ed25519 公钥，raw 32 字节 base64），`publisher` 语义改为 `sha256(publisherPublicKey)` 的十六进制指纹 | 只有指纹**不能验签**（指纹是身份句柄不是验证材料）；无账号阶段（GitHub manifest 仓库运营）包必须自带公钥（评审方案 A）。校验器必须检查 `publisher == sha256(publisherPublicKey)` |
| 哈希规则钉死为 **JCS (RFC 8785) + package.integrity 清单**，废弃"排序键+无空白"的口述规范化 | 自发明的规范化在数字格式/转义/嵌套键序/ZIP 顺序/路径分隔符上都会产生逻辑等价但字节不同的哈希；JCS 是有测试向量的正式标准。包内容枚举同样需要确定性：`package.integrity` 文件按 `sha256  <path>` 行、路径正斜杠、ordinal 排序，`contentHash = sha256(package.integrity)` |

### 验签流程（v0.2 语义，validator/安装器实现于阶段 6；编码细则=第五轮评审钉死）

**哈希域**：`package.integrity` 列出全部 payload 文件 + `manifest.json`（以 JCS 规范形参与，signature 置 null）；**`package.integrity` 自身永不列入清单**——它自己的完整性由传递闭包覆盖（contentHash 哈希它、publisher 签名覆盖 contentHash），列入则自引用无解。清单行格式 `<sha256 小写 hex>␣␣<path>`（摘要+恰好两个空格+正斜杠相对路径），LF 行尾，按路径 ordinal 排序。

1. 读 manifest，canonicalize（signature 字段置 null，JCS/RFC 8785）→ 得到 manifest 规范形；
2. 读 `package.integrity`，找到 manifest.json 对应行，比对 `sha256(manifest 规范形)`——防"清单与 manifest 不一致"；
3. `contentHash = sha256(package.integrity 文件字节)`，与 `signature.contentHash`（小写 hex 表示）比对；
4. 用 `publisherPublicKey`（base64，先解码为 raw 32 字节公钥）验 Ed25519 `publisherSignature`——**签名输入是 contentHash 的 raw 32 字节摘要，不是 hex 字符串**；
5. 检查 `publisher == lowercase-hex(sha256(raw 公钥 32 字节))`——**哈希对象是解码后的公钥字节，不是 base64 文本**；指纹同时是首次安装时的信任锚（用户批准的就是这个指纹，升级必须一致）。

无签名的 dev 包跳过 3-4，但 1-2 的哈希一致性仍然检查（防手改文件后清单对不上）。

> 编码细则（哈希对象、hex/base64、大小写、行格式）一旦 TS CLI、C# 安装器、Rust 运行时三套实现各自理解一套就全线失配，故在 schema 描述与本节双重钉死。

## v0.2 补充钉死（第五轮外部评审）

| 钉死项 | 内容 |
|---|---|
| integrity 自引用排除 | `package.integrity` 不进入自身清单（传递闭包覆盖），哈希域=payload+JCS(manifest) |
| 指纹推导 | `sha256(raw 32 字节公钥)`，先 base64 解码再哈希，小写 hex——不是对 base64 文本哈希 |
| 签名输入 | `publisherSignature` 签 contentHash 的 raw 32 字节摘要，非 hex 字符串 |
| contentHash 表示 | 小写 hex 存储；清单行 `<sha256 小写 hex>␣␣<path>`（摘要+两空格+路径），LF 行尾 ordinal 排序 |

## v0 → v0.1 变更（第三轮外部评审吸收，roadmap 16.7）

| 变更 | 原因 |
|---|---|
| `widgets[]` → `contributions[]` + `type` discriminator | 消除 Plugin=WidgetPlugin 隐含；Package 可贡献 Command/AITool/Settings 等不含 widget 的类型，v0.1 只实现 widget 但结构不改 |
| `category` → `runtime`（none/wasm/process） | 原枚举混合了内容类型（resource-pack）与运行时技术（wasm/out-of-proc）；runtime 描述执行技术，声明式 UI 在所有 runtime 下都是宿主渲染 |
| `typeId` pattern + description 矛盾修复 | 原 description 说"以 package id 为前缀"（含点）但 pattern 禁点号；改为 local id + 宿主派生 canonical id（`{package-id}/{local-id}`） |
| `signature` 自引用修复 | contentHash 覆盖域定义为"除 signature 块自身外的全部文件，manifest 规范化（signature=null、排序键、无空白）后参与哈希" |
| `publisherKey` → `publisherSignature` | 字段名与语义错位（装的是签名不是公钥）；公钥指纹在顶层 publisher 字段 |
| `capabilities[]` + `defaultSet` 删除 | 安全模型缺陷：不可信第三方不应通过 manifest 自我授权"默认授予"；改为纯 permissions[] + required/scope，授予决策归宿主策略引擎 |
| `signature` 从 required 移除 | 开发模式（dev/pack 前）不应强制签名；签名是分发 envelope 的职责，商店安装时才强制 |

## 与已定决策的对应

| Schema 条目 | 决策来源 |
|---|---|
| `runtime` 三枚举 | §13.1 三分类（none=resource-pack / wasm / process） |
| `hostApi{min,max}` + 运行期 protocolVersion | §13.4 版本双闸 |
| 六模板枚举 | §13.5（不发明小型 XAML） |
| `payload.version` + per-element fallback | §13.5 分层规则 |
| `permissions[]` + `required` + scope | §13.2（Tauri 词汇，但授予决策归宿主——非 Tauri 的 default-set 语义） |
| `activationEvents` | §5.8（实例恢复与运行时激活分离） |
| 三级 ID 分离 | §7 阶段 7（Package ≠ Contribution ≠ Instance） |
| `signature` 双字段 | §13.6 三级签名（contentHash + publisherSignature；Full-Trust 加 Windows 代码签名） |
| `data.*SchemaVersion` | §9 插件 schema 迁移条款 |

## 通道三分法（引用 §13.4，写进包语义）

- **Capability Call**（插件→宿主，request/response，受权限+scope 控制）
- **Lifecycle/Event**（宿主→插件，typed event，白名单）
- **任意宿主函数 invoke**——禁止

## v0.2 明确不包含（防止提前冻结）

- 声明式 UI payload 的字段级 schema（六模板各自的 payload 结构留给 spike 输出后定 v1）；
- `contributions[]` 的 command/ai-tool/settings 类型定义（v0.1 只有 widget；加新类型不改包级结构）；
- wasm/process runtime 的入口/构件字段（entryPoint、wasmModule 路径——spike 三条腿的输出决定字段名）；
- 商店侧字段（价格/entitlement 是服务端元数据，§15）；
- 进程外/WASM 运行时的传输细节（stdio+LSP framing 是 Process Runtime 的事）。
