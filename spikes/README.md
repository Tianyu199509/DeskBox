# Plugin Runtime Spike（roadmap 阶段 3.5）

同一个 GitHub-Stats 插件实现三份实测对比，用数据拍板"代码插件默认 Runtime"。
协议/权限/manifest 的投入无论结果如何全部复用（roadmap §7 阶段 3.5）。

## 三腿状态

| 腿 | 内容 | 状态 |
|---|---|---|
| ① 声明式 | 六模板 + manifest v0.2 + 完整验签链 | ✅ 已完成（本目录 + `scripts/spike/`） |
| ② TS 外部进程 | JSON-RPC over stdio，进程治理复用 ThumbnailProxy 模式 | ⬜ 未开始 |
| ③ Rust/TS→WASM | 独立 crate `native/deskbox-wasm-spike`（勿给 deskbox-native 加 wasmtime feature）；Wasmtime 组件模型 + wit-bindgen `deskbox:plugin` world + fuel/epoch/ResourceLimiter | ⬜ 未开始 |

可选补充腿（降级）：Extism（1 天 AOT 冒烟）、wasmtime-dotnet（0.5 天，宿主内置信任脚本引擎定位）。

## 腿① 交付物

- `github-stats/`：schema v0.2 声明式样本包——六种模板各一个贡献（metric/list/status/gallery/action-list/simple-form）+ network.fetch 权限 scope + 签名块。`package.integrity` + Ed25519 签名按 v0.2 规则生成。
- `keys/`：**一次性 spike 开发密钥对**（私钥入库仅为此 spike；真实发布密钥永不入库，由阶段 6 CLI keygen 管理）。
- `../scripts/spike/build-package.mjs`：重建 integrity 清单 + 签名（`node scripts/spike/build-package.mjs [pkgDir] [keysDir]`）。
- `../scripts/spike/validate-package.mjs`：结构校验 + 五步验签链（`node scripts/spike/validate-package.mjs [pkgDir]`，通过=exit 0 + VERIFIED）。
- 行为钉扎：`tests/DeskBox.Tests/DeclarativePackageSpikeTests.cs`（验证通过 / 改 payload 文件被检 / 改 manifest 不重建清单被检）。

**SPIKE-GRADE 声明**：Node 工具链零依赖、JCS 为子集实现（整数 only、排序键、无空白；小数/代理对未按 RFC 8785 全覆盖）。阶段 6 CLI（.NET AOT）必须用完整 JCS 实现，本工具只是脚手架不是参考实现。

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
