# Plugin Runtime Spike（roadmap 阶段 3.5）

2026-09-08：官方六功能包的当前执行依据见 [首发执行计划](../docs/architecture/official-widget-packages-plan.md)，原生界面试点见 [Glance NativeAOT](glance-native/README.md)。本页保留声明式、Process 和 WASM 样本的历史结果。

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
| ② TS 外部进程 | ndjson JSON-RPC over stdio + 同款三层能力门 + 进程治理（deadline/kill/退出码传播） | ✅ 已完成（schema 增 `entry` 入口字段） |
| ③ Rust→WASM | 独立 crate `native/deskbox-wasm-spike`（红线：不进 app 构建）；Wasmtime 48 组件模型 + wit-bindgen 0.61 `deskbox:plugin` world + **fuel/epoch/ResourceLimiter** | ✅ 已完成 |

可选补充腿（降级）：Extism（1 天 AOT 冒烟）、wasmtime-dotnet（0.5 天，宿主内置信任脚本引擎定位）。

## 腿③ 交付物

- `native/deskbox-wasm-spike/`（**独立 workspace，红线钉死：app csproj/audit/retail 脚本永不引用，有测试钉扎**）：
  - `wit/deskbox-plugin.wit`：`deskbox:plugin@0.1.0` world——能力只有 host import（`network-fetch`/`shell-open`/`widget-update`），guest 导出 `activate`/`invoke-action`；三腿共享的能力边界在类型层成文。
  - `guest/`：**no_std（core+alloc）纯组件，零 WASI 导入**——`wasm32-unknown-unknown` 构建 + `wasm-tools component embed/new` 包装（37.9KB）。无 WASI 依赖=宿主纯同步嵌入，不挂 wasmtime-wasi；敌意 guest 的 `spin` 分支用于 fuel 治理测试。
  - `host/`：Wasmtime 48 嵌入——**fuel 2M（确定性预算）+ epoch 挂钟后备 + ResourceLimiter 内存 64MB 上限**三层治理；ureq 取数（redirects(0)+显式 3xx 拒绝+2MB 流式上限）；手写 mock HTTP 服务器（无依赖五模式）；Rust 版同一三层能力门（声明∧scope∧授予，与 Node 腿语义逐条对齐，消息文本一致）。
- `spikes/github-stats-wasm/`：`runtime:"wasm"` 包——`entry.main=plugin/plugin.wasm`（37.9KB 组件产物入库，integrity+签名覆盖）。
- `../scripts/spike/build-wasm-spike.ps1`：一键构建+包装+重签（wasm 非字节可重现，重构建必须走脚本刷新 integrity 链）。
- 真实链路已验证（GitHub 实拉 3505 stars 经 WASM guest→host 能力往返）；七场景（happy/未授予/敌意越界/redirect/5xx/spin-fuel 耗尽/真实网络）全部手动验证。**WASM 场景运行不进 dotnet test**（CI 无 Rust 工具链；C# 侧钉扎=包验签+组件头+WIT world+宿主治理标记+红线）。

## 腿② 交付物

- `github-stats-process/`：进程腿样本包——`runtime: "process"` + **`entry.main`（spike 提案的入口字段，普通 integrity 清单内负载文件；runtime:none 禁止 entry，process/wasm 必填）** + 与腿①相同的 metric 贡献/回退 payload/双权限。
- `plugin/main.mjs`：**第三方代码本体**——ndjson JSON-RPC over stdio（宿主↔插件：`activate`/`action.invoke` 通知；插件→宿主：`network.fetch`/`shell.open` 能力请求+`widget.update` 状态推送）。插件**从不直接碰网络/Shell**，一切经宿主能力调用；能力被拒/数据失败→回退 payload 继续渲染（与声明式腿同一韧性契约）。
- `../scripts/spike/run-process.mjs`：进程宿主 harness——spawn（node 子进程）→ 15s 总 deadline+kill、stderr 捕获、**退出码传播（首状态后崩溃也报 FAILED，不吞成功）**；能力调用经 **`host-capabilities.mjs` 共享库**（与腿①完全同款三层门/redirect 拒绝/2MB 上限——两腿零策略漂移）；`--self-test=ok|redirect|server-error|evil-ask|crash` 五模式（evil-ask=模拟敌意插件请求范围外 URL，门必须拒绝该调用）。
- 真实链路已验证：GitHub 实拉（2026-09-08 实测 3503 stars）经插件→宿主能力调用→绑定；五个场景（happy/未授予/敌意越界/redirect/崩溃）全部测试钉死。

## 腿①A 交付物

- `github-stats/`：schema v0.2 声明式样本包——六种模板各一个贡献（metric/list/status/gallery/action-list/simple-form）+ network.fetch 权限 scope + 签名块（静态数据：①A 不消费该权限，①B 才会）。`package.integrity` + Ed25519 签名按 v0.2 规则生成；**integrity 路径文法已钉死**（相对+正斜杠+无 `..`/`.`/空段/反斜杠/冒号/盘符+大小写不敏感查重——zip-slip 防御，build 与 validate 双侧强制）。
- `keys/`：**一次性 spike 开发密钥**。以 32 字节 hex seed 文本入库（`dev-ed25519-seed.txt`，明确的测试向量；不用 PEM 形态避免 secret scanner 长期噪音），私钥由脚本现场构造；公钥 base64 同置。真实发布密钥永不入库，由阶段 6 CLI keygen 管理。
- `../scripts/spike/build-package.mjs`：重建 integrity 清单 + 签名（`node scripts/spike/build-package.mjs [pkgDir] [keysDir]`）；违反路径文法的文件会中止构建。
- `../scripts/spike/validate-package.mjs`（逻辑在 `validate-lib.mjs`，供 harness 复用）：结构校验 + v0.3 词汇 + 路径文法 + 权限消耗清单 + 五步验签链（通过=exit 0 + VERIFIED）。
- 行为钉扎：`tests/DeskBox.Tests/DeclarativePackageSpikeTests.cs`。

## 腿①B 交付物（第七轮安全语义已补齐）

- `github-stats-live/`：schema v0.3 声明式 live 包——`dataSources.github-repo`（http-json，`https://api.github.com/repos/Tianyu199509/DeskBox`，refreshSeconds 300）+ metric 贡献的 `bindings.value ← $.stargazers_count`（payload 回退值 `…`）+ `payload.primaryActionId: "open-repo"`（"点击 widget→动作"链路）+ `actions.open-repo`（open-url）+ 双权限：`network.fetch`（scope: api.github.com）与 `shell.open`（scope: github.com）。
- `../scripts/spike/run-declarative.mjs`：声明式执行 harness——包校验（复用 validate-lib）→ **三层能力门**（manifest 声明 ∧ URL host 在请求 scope 内 ∧ **宿主已授予**；`--grant=<id>=<host>` 重复传参，**无授予=全部拒绝**（fail-closed），requested≠granted 模型与腿②③共享）→ **宿主代取** http-json（HTTPS-only、**redirect 不跟随直接拒绝**、**响应体 2MB 硬上限**流式执行）→ JSON path 绑定求值（**数据失败≠致命**：源标记失败+绑定回退 payload，widget 照常产出状态；策略失败=REFUSED exit 1）→ 动作解析（`--invoke-widget=<贡献id>` 经 primaryActionId 走通点击链路；`--invoke=<actionId>` 解析根动作；打印 ShellOpen 意图不真开）。`--self-test=ok|out-of-scope|redirect|server-error|huge` 五模式本地 mock；`--measure` 输出分段耗时+堆。
- 真实链路已验证：GitHub API 实拉（2026-09-07 实测 3500 stars）→ 绑定生效 → 动作解析；五个安全/回退场景（无授予/超 scope/redirect/5xx/超限）全部有测试钉死。

## 统一测量矩阵（三腿跑齐后填表拍板；腿①=2026-09-07 开发机初步值，mock 取 3 次中位）

| 维度 | ① 声明式 | ② TS 进程 | ③ WASM |
|---|---|---|---|
| 冷启动（包校验+首帧状态） | validate 3ms + bind 0ms | spawn 43ms + activate→首状态 50ms（mock） | **compile 14ms + instantiate ~0ms + activate 1ms ≈ 真实冷启动 15ms**（此前"instantiate 0ms"是排除 `Component::new` 编译的暖启动读数，第八轮修正计时边界；真实产品可用安装期预编译+缓存序列化组件把冷启动压回 deserialize+instantiate） |
| 稳态内存增量 | 峰值堆 ~9.0MB（含 Node 运行时本身；宿主内嵌渲染器将远低于此——此数是 harness 上界，非插件成本） | 宿主 ~9.2MB + 子进程 Node 运行时（子进程 RSS 待正式测量轮统一采） | 插件本体 37.9KB 组件 + store 按需（内存 64MB 硬顶）；宿主 exe 12.7MB（含 Wasmtime） |
| 数据源刷新→绑定延迟（p50） | 32ms（mock；真实 GitHub 数百 ms，网络主导） | 50ms（mock，含 stdio 往返；真实 GitHub 700ms 网络主导） | 1-2ms（mock，含能力往返+guest 解析；真实 GitHub 641ms 网络主导） |
| 开发代码量（行） | harness ~350 行（权限门+绑定求值器+mock） | 插件 ~120 行 + harness ~290 行（共享能力库两腿复用） | WIT ~25 行 + guest ~95 行 + host ~400 行（三腿唯一需要 WIT 的） |
| 打包大小 | manifest+integrity ≈ 4.6KB | manifest+integrity+plugin ≈ 5.7KB | manifest+integrity+**组件 37.9KB** |
| 调试体验 | 纯声明式 JSON，validator 逐条失败原因 | 独立进程可断点/打日志，stderr 由宿主捕获转发 | fuel 确定性消耗计数；trap 分类（fuel/epoch/其他）；断点需 wasm DWARF 支持 |
| AI 一次生成成功率 | 待三腿同题测试 | 待三腿同题测试 | 待三腿同题测试 |
| 升级兼容 | payload per-element fallback + 绑定失败回退=设计内置 | 协议版本化待定（ndjson JSON-RPC 为 spike 选择） | 组件模型版本化+WIT world 版本化（`deskbox:plugin@0.1.0`） |
| 权限强制点 | 请求∧scope∧授予三层门先于 fetch；redirect 拒绝；2MB 上限（五场景测试钉死） | **同一共享库同一语义**（未授予/敌意越界/redirect 调用级拒绝+回退，测试钉死） | **同一语义 Rust 实现**（七场景手动验证+源码钉扎） |
| Crash 恢复 | 无第三方代码可崩（模型固有优势） | 进程崩溃→宿主检出（退出码传播，首状态后崩溃不吞成功） | **最强：trap 隔离（fuel 耗尽/epoch 超时→宿主存活报错）+内存 64MB 硬顶+确定性 fuel 计数**（验证精度：**fuel=行为已验证**；epoch/内存=机制已接入、行为场景待补——见第八轮注） |

**三方安全定位（第八轮钉死）**：**声明式/WASM=真实技术强制**（声明式一切经宿主；WASM 无 WASI 导入、网络/文件/Shell 天然不可见）；**Process=Full Trust**——子进程拥有 OS 用户全部权限，可以完全绕过 broker，Capability Broker 对它只是**推荐 API/UX/审计/兼容边界，不是 OS 安全边界**（schema 的 `process = full-trust executable` 一直是这么写的；商店走 L1 人工审核+Windows 代码签名通道）。据此的定位收敛：声明式=安全默认/AI 与普通用户；WASM=沙箱代码第一候选（社区商店）；Process=Full Trust 扩展（专业/重型/原生集成）——"WASM 是否默认代码 Runtime"仍等 AI 同题实验+公平基准再拍板。

**三腿初步读数（正式对比仍按下方方法学，等最小 C# 宿主执行器再定论）**：WASM 修正后仍占优（真实冷启动 ~15ms vs 进程 spawn+激活 ~93ms；崩溃隔离最强制；代价=37.9KB 产物+Rust 宿主工具链+WIT 面）。AI 可生成性与真实集成成本待三腿同题实测——这是拍板前最后的坑。宿主 exe 12.7MB 是**磁盘体积不是内存数据**，不能与 Node RSS 比较。

测量方法学（第七轮修订）：**上表腿①数字是 scaffolding observation（Node 脚手架观察值），不能与腿②③直接横向定胜负**——产品形态下声明式执行器内嵌在 DeskBox C# 宿主里，Node 堆/启动数与产品无对应关系。正式对比拆两层：**Runtime intrinsic**（activation/协议/绑定延迟、增量内存，排除网络）与 **E2E**（从"宿主要求激活"到"首个绑定状态就绪"，统一包含 process spawn / WASM instantiate / declarative parse，三腿测量边界完全一致）。腿①正式数等最小 C# 宿主执行器就绪后再填。

**Backlog（不阻塞腿②③）**：①File Widget 枚举/watch 过滤 `.import-*.tmp`（大文件导入期间避免视觉噪音）；②`network.fetch` 与 `network.local` 拆分——默认禁 loopback/私网/link-local/元数据地址（DNS 重绑定类 SSRF 防御，归 Capability Broker 安全 backlog）。

**SPIKE-GRADE 声明**：Node 工具链零依赖、JCS 为子集实现（整数 only、排序键、无空白；小数/代理对未按 RFC 8785 全覆盖）。阶段 6 CLI（.NET AOT）必须用完整 JCS 实现并**替换掉 Node 依赖**（当前 dotnet test 需要 node.exe 是 spike 期过渡，不是长期构建前提）；本目录工具只是脚手架不是参考实现。
