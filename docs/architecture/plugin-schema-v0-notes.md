# DeskBox Plugin Schema v0.1 — 语义注记

配套 `plugin-schema-v0.json`。v0.1 是草案：给三路 spike（roadmap 阶段 3.5）、CLI validator（阶段 6）、商店规范（阶段 7）一个共同靶子，会迭代；契约测试只轻钉存在性与词汇，不逐字段冻结。

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

## v0.1 明确不包含（防止提前冻结）

- 声明式 UI payload 的字段级 schema（六模板各自的 payload 结构留给 spike 输出后定 v1）；
- `contributions[]` 的 command/ai-tool/settings 类型定义（v0.1 只有 widget；加新类型不改包级结构）；
- wasm/process runtime 的入口/构件字段（entryPoint、wasmModule 路径——spike 三条腿的输出决定字段名）；
- 商店侧字段（价格/entitlement 是服务端元数据，§15）；
- 进程外/WASM 运行时的传输细节（stdio+LSP framing 是 Process Runtime 的事）。
