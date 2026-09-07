# Plugin Runtime Spike（roadmap 阶段 3.5）

同一个 GitHub-Stats 插件实现三份实测对比，用数据拍板"代码插件默认 Runtime"。
协议/权限/manifest 的投入无论结果如何全部复用（roadmap §7 阶段 3.5）。

**腿① 重定义（第六轮评审采纳）**：三路对比要公平，三腿必须做**同一件事**（拉取 GitHub API→解析→更新→点击打开仓库）。因此腿① 拆成两半：
- **①A Package/Schema/模板一致性样本**（本目录，✅ 已完成）——验证包结构、六模板词汇、权限声明、integrity/签名链，全部静态数据，不执行任何东西。
- **①B 声明式执行最小闭环**（⬜ 未开始，真正的 Declarative 腿）——需要 schema v0.3 增补声明式执行词汇：`dataSources`（最小=`http-json` + 刷新间隔，经宿主 `network.fetch` 能力代理执行）、`bindings`（最小=JSON path，如 `$.stargazers_count`）、`actions`（最小=`open-url`）。做完 ①B 才能说 Declarative Runtime 跑通，也才有资格进测量矩阵。

## 三腿状态

| 腿 | 内容 | 状态 |
|---|---|---|
| ①A 声明式包样本 | 六模板 + manifest v0.2 + 完整验签链 + 路径文法 | ✅ 已完成（本目录 + `scripts/spike/`） |
| ①B 声明式执行闭环 | http-json 数据源 + JSON path 绑定 + open-url 动作 + 宿主能力消费 | ⬜ 未开始（schema v0.3 草案先行） |
| ② TS 外部进程 | JSON-RPC over stdio，进程治理复用 ThumbnailProxy 模式 | ⬜ 未开始 |
| ③ Rust/TS→WASM | 独立 crate `native/deskbox-wasm-spike`（勿给 deskbox-native 加 wasmtime feature）；Wasmtime 组件模型 + wit-bindgen `deskbox:plugin` world + fuel/epoch/ResourceLimiter | ⬜ 未开始 |

可选补充腿（降级）：Extism（1 天 AOT 冒烟）、wasmtime-dotnet（0.5 天，宿主内置信任脚本引擎定位）。

## 腿①A 交付物

- `github-stats/`：schema v0.2 声明式样本包——六种模板各一个贡献（metric/list/status/gallery/action-list/simple-form）+ network.fetch 权限 scope + 签名块（静态数据：①A 不消费该权限，①B 才会）。`package.integrity` + Ed25519 签名按 v0.2 规则生成；**integrity 路径文法已钉死**（相对+正斜杠+无 `..`/`.`/空段/反斜杠/冒号/盘符+大小写不敏感查重——zip-slip 防御，build 与 validate 双侧强制）。
- `keys/`：**一次性 spike 开发密钥**。以 32 字节 hex seed 文本入库（`dev-ed25519-seed.txt`，明确的测试向量；不用 PEM 形态避免 secret scanner 长期噪音），私钥由脚本现场构造；公钥 base64 同置。真实发布密钥永不入库，由阶段 6 CLI keygen 管理。
- `../scripts/spike/build-package.mjs`：重建 integrity 清单 + 签名（`node scripts/spike/build-package.mjs [pkgDir] [keysDir]`）；违反路径文法的文件会中止构建。
- `../scripts/spike/validate-package.mjs`：结构校验 + 路径文法 + 五步验签链（`node scripts/spike/validate-package.mjs [pkgDir]`，通过=exit 0 + VERIFIED）。
- 行为钉扎：`tests/DeskBox.Tests/DeclarativePackageSpikeTests.cs`（验证通过 / 改 payload 文件被检 / 改 manifest 不重建清单被检 / traversal 路径违反文法被拒）。

**SPIKE-GRADE 声明**：Node 工具链零依赖、JCS 为子集实现（整数 only、排序键、无空白；小数/代理对未按 RFC 8785 全覆盖）。阶段 6 CLI（.NET AOT）必须用完整 JCS 实现并**替换掉 Node 依赖**（当前 dotnet test 需要 node.exe 是 spike 期过渡，不是长期构建前提）；本工具只是脚手架不是参考实现。

## 统一测量矩阵（三腿跑齐后填表拍板）

| 维度 | ① 声明式 | ② TS 进程 | ③ WASM |
|---|---|---|---|
| 冷启动（首帧） | | | |
| 稳态内存增量 | | | |
| 调用延迟（p50/p99） | | | |
| 开发代码量（行） | | | |
| 打包大小 | | | |
| 调试体验 | | | |
| AI 一次生成成功率 | | | |
| 升级兼容（换版本重装） | | | |
| 权限强制点 | | | |
| Crash 恢复 | | | |

测量方法学：进程腿内存=私有提交（非工作集，见 memory 惯例）；每腿同机同电源策略跑 3 次取中位。
