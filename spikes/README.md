# Plugin Runtime Spike（roadmap 阶段 3.5）

同一个 GitHub-Stats 插件实现三份实测对比，用数据拍板"代码插件默认 Runtime"。
协议/权限/manifest 的投入无论结果如何全部复用（roadmap §7 阶段 3.5）。

**腿① 重定义（第六轮评审采纳）**：三路对比要公平，三腿必须做**同一件事**（拉取 GitHub API→解析→更新→点击打开仓库）。因此腿① 拆成两半：
- **①A Package/Schema/模板一致性样本**（`github-stats/`，✅ 已完成）——验证包结构、六模板词汇、权限声明、integrity/签名链、路径文法，全部静态数据。
- **①B 声明式执行闭环**（`github-stats-live/` + `run-declarative.mjs`，✅ 已完成，schema v0.3）——真实数据流的最小闭环：http-json 数据源（宿主代取）+ JSON path 绑定 + open-url 动作 + 宿主权限门。

## 三腿状态

| 腿 | 内容 | 状态 |
|---|---|---|
| ①A 声明式包样本 | 六模板 + manifest v0.2 + 完整验签链 + 路径文法 | ✅ 已完成 |
| ①B 声明式执行闭环 | http-json 数据源 + JSON path 绑定 + open-url 动作 + 宿主权限门（schema v0.3） | ✅ 已完成 |
| ② TS 外部进程 | JSON-RPC over stdio，进程治理复用 ThumbnailProxy 模式 | ⬜ 未开始 |
| ③ Rust/TS→WASM | 独立 crate `native/deskbox-wasm-spike`（勿给 deskbox-native 加 wasmtime feature）；Wasmtime 组件模型 + wit-bindgen `deskbox:plugin` world + fuel/epoch/ResourceLimiter | ⬜ 未开始 |

可选补充腿（降级）：Extism（1 天 AOT 冒烟）、wasmtime-dotnet（0.5 天，宿主内置信任脚本引擎定位）。

## 腿①A 交付物

- `github-stats/`：schema v0.2 声明式样本包——六种模板各一个贡献（metric/list/status/gallery/action-list/simple-form）+ network.fetch 权限 scope + 签名块（静态数据：①A 不消费该权限，①B 才会）。`package.integrity` + Ed25519 签名按 v0.2 规则生成；**integrity 路径文法已钉死**（相对+正斜杠+无 `..`/`.`/空段/反斜杠/冒号/盘符+大小写不敏感查重——zip-slip 防御，build 与 validate 双侧强制）。
- `keys/`：**一次性 spike 开发密钥**。以 32 字节 hex seed 文本入库（`dev-ed25519-seed.txt`，明确的测试向量；不用 PEM 形态避免 secret scanner 长期噪音），私钥由脚本现场构造；公钥 base64 同置。真实发布密钥永不入库，由阶段 6 CLI keygen 管理。
- `../scripts/spike/build-package.mjs`：重建 integrity 清单 + 签名（`node scripts/spike/build-package.mjs [pkgDir] [keysDir]`）；违反路径文法的文件会中止构建。
- `../scripts/spike/validate-package.mjs`（逻辑在 `validate-lib.mjs`，供 harness 复用）：结构校验 + v0.3 词汇 + 路径文法 + 权限消耗清单 + 五步验签链（通过=exit 0 + VERIFIED）。
- 行为钉扎：`tests/DeskBox.Tests/DeclarativePackageSpikeTests.cs`。

## 腿①B 交付物

- `github-stats-live/`：schema v0.3 声明式 live 包——`dataSources.github-repo`（http-json，`https://api.github.com/repos/Tianyu199509/DeskBox`，refreshSeconds 300）+ metric 贡献的 `bindings.value ← $.stargazers_count`（payload 回退值 `…`）+ `actions.open-repo`（open-url → `https://github.com/Tianyu199509/DeskBox`）+ 双权限：`network.fetch`（scope: api.github.com）与 `shell.open`（scope: github.com）。
- `../scripts/spike/run-declarative.mjs`：声明式执行 harness——包校验（复用 validate-lib）→ **宿主策略门**（权限已声明且 URL host 落在 scope.allow 内才放行，host 精确小写匹配；install 期与运行期同规则）→ **宿主代取** http-json（HTTPS-only）→ JSON path 绑定求值（拉取失败/路径缺失=保持 payload 回退值）→ open-url 动作解析（打印宿主 ShellOpen 意图，spike 不真开浏览器）。`--self-test=ok|out-of-scope` 用本地 mock 服务器做确定性测试（ok=同步授予 mock host 走通全链路；out-of-scope=保留原 scope，策略门必须在**任何字节移动之前**拒绝）；`--measure` 输出分段耗时+堆。
- 真实链路已验证：GitHub API 实拉（2026-09-07 实测 3500 stars）→ 绑定生效 → 动作解析。

## 统一测量矩阵（三腿跑齐后填表拍板；腿①=2026-09-07 开发机初步值，mock 取 3 次中位）

| 维度 | ① 声明式 | ② TS 进程 | ③ WASM |
|---|---|---|---|
| 冷启动（包校验+首帧状态） | validate 3ms + bind 0ms | | |
| 稳态内存增量 | 峰值堆 ~9.0MB（含 Node 运行时本身；宿主内嵌渲染器将远低于此——此数是 harness 上界，非插件成本） | | |
| 数据源刷新→绑定延迟（p50） | 32ms（mock；真实 GitHub 数百 ms，网络主导） | | |
| 开发代码量（行） | harness ~260 行（策略门+绑定求值器+mock） | | |
| 打包大小 | manifest+integrity ≈ 4.4KB | | |
| 调试体验 | 纯声明式 JSON，validator 逐条失败原因 | | |
| AI 一次生成成功率 | 待三腿同题测试 | | |
| 升级兼容 | payload per-element fallback + 绑定失败回退=设计内置 | | |
| 权限强制点 | 策略门先于 fetch（out-of-scope 拒绝已被测试钉死） | | |
| Crash 恢复 | 无第三方代码可崩（模型固有优势） | | |

测量方法学：进程腿内存=私有提交（非工作集，见 memory 惯例）；每腿同机同电源策略跑 3 次取中位。

**SPIKE-GRADE 声明**：Node 工具链零依赖、JCS 为子集实现（整数 only、排序键、无空白；小数/代理对未按 RFC 8785 全覆盖）。阶段 6 CLI（.NET AOT）必须用完整 JCS 实现并**替换掉 Node 依赖**（当前 dotnet test 需要 node.exe 是 spike 期过渡，不是长期构建前提）；本目录工具只是脚手架不是参考实现。
