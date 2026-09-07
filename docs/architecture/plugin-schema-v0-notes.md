# DeskBox Plugin Schema v0 — 语义注记

配套 `plugin-schema-v0.json`。v0 是草案：给三路 spike（roadmap 阶段 3.5）、CLI validator（阶段 6）、商店规范（阶段 7）一个共同靶子，会迭代；契约测试只轻钉存在性与词汇，不逐字段冻结。

## 与已定决策的对应

| Schema 条目 | 决策来源 |
|---|---|
| `category` 三枚举 | §13.1（声明式资源包首发品类；WASM/进程外一等 Runtime） |
| `hostApi{min,max}` + 运行期 protocolVersion | §13.4 版本双闸（安装期挡不可能兼容的包，握手期不匹配回结构化错误附 supported 列表） |
| 六模板枚举 | §13.5（Metric/List/Status/Gallery/ActionList/SimpleForm；v0 无自由布局原语，不发明小型 XAML） |
| `payload.version` + per-element fallback | §13.5 分层规则（manifest 严格 / RPC envelope 宽容 / payload 按版本严格、未知元素 fallback→丢弃） |
| `permissions`/`scopes`/`capabilities` + `defaultSet` | §13.2（Tauri ACL 词汇、JSON 格式不抄 TOML、deny 压 allow、防御性默认 deny 集） |
| `activationEvents` | §5.8（实例恢复与运行时激活分离：窗口/布局恢复永远发生，事件只决定 Runtime 何时拉起） |
| 三级 ID 分离 | §7 阶段 7（Package ≠ Widget Type ≠ Instance） |
| `signature` 双字段 | §13.6 三级签名（完整性 SHA256 / 发布者 Ed25519 / Full-Trust 走 Windows 代码签名+Store/WinGet 渠道） |
| `data.*SchemaVersion` | §9 插件 schema 迁移条款（宿主负责备份→迁移→验证→提交/回滚） |

## 通道三分法（引用 §13.4，写进包语义）

- **Capability Call**（插件→宿主，request/response，受权限+scope 控制）——被鼓励的正常流量；
- **Lifecycle/Event**（宿主→插件，typed event，白名单：onActivate/onWidgetVisible/onFileChanged/onTimer/onSettingsChanged…）；
- **任意宿主函数 invoke**——禁止。MCP 弃用反向请求的教训只适用于第三类。

## v0 明确不包含（防止提前冻结）

- 声明式 UI payload 的字段级 schema（六模板各自的 payload 结构留给 spike 输出后定 v1）；
- 商店侧字段（价格/entitlement 是服务端元数据，§15：包本体是免费产物）；
- 进程外/WASM 运行时的传输细节（stdio+LSP framing 是 Process Runtime 的事，不是 manifest 的事）。
