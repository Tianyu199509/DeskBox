# DeskBox AOT 阶段 5B-4C2A 完成报告

- 日期：2026-08-23
- 状态：5B-4C2A 已完成到 x64 Native AOT 自动化实际运行边界
- 审计配置：profile 52 / schema 49
- 本阶段范围：主热键与搜索热键的系统注册、合成按键分发、冲突回滚、禁用/重启释放，以及 Win+Space 保留钩子的启停生命周期
- 明确保留：物理键盘、物理 Win+Space、设置/引导录制器与内部屏蔽键验证，归入 5B-4C2B

## 1. 结论

5B-4C2A 已闭环 NativeAOT 下可安全自动化的热键矩阵。两个使用同一受审计 AOT 产物和同一隔离数据根的全新进程，依次证明主热键与搜索热键可以注册、接收一次系统 `WM_HOTKEY`、调度一次产品动作、处理系统冲突、禁用注销、重新启用，并在前一进程正常退出后由后一进程重新取得相同手势。

本阶段同时修正了一个产品一致性问题。`SearchHotkeyService.TryApplyGesture` 原先先写入设置再尝试 `RegisterHotKey`，系统冲突时设置中的新手势可能与实际仍生效的手势不一致。现在与主热键采用同一事务边界：真实系统注册成功后才提交；失败则恢复旧设置并重新注册旧手势。

Win+Space 只验证低级键盘钩子的创建、独立消息线程、错误码、切回标准手势后的停止与线程归零。本阶段没有向保留钩子注入按键，也没有把 `SendInput` 结果写成物理键盘结论。

## 2. 实现边界

产品代码的变化限于：

1. 搜索热键增加接收、调用和 UI 调度失败三个单调计数器，与主热键形成相同的可观察契约；
2. 搜索热键更换改为真实 `RegisterHotKey` 成功后提交，冲突时恢复旧设置与旧注册；
3. NativeAOT-only 辅助方法按下并按相反顺序释放修饰键，部分 `SendInput` 时进行尽力释放，避免测试键滞留；
4. NativeAOT-only 场景只接受 `RegistrationLifecycle`、`Primary/Release`、32 位 `Guid N` run ID 和显式隔离 preview 根；
5. 场景结果使用独立 source-generated JSON context，完成或失败后都进入应用正常关闭路径。

普通 JIT 不包含 AOT 烟测和合成输入辅助文件。主热键、搜索热键和 Win+Space 的日常产品路径仍为现有 C#/Win32 实现。

## 3. 双进程实际证据

成功 run ID 为 `3f8971b8d3bc40cebca69f0c425c55fc`：

| 阶段 | PID | 启动时主/搜索已注册 | 主热键接收/调用/失败 | 搜索热键接收/调用/失败 | Win+Space hook thread |
| --- | ---: | --- | --- | --- | ---: |
| Primary | 22420 | 否 / 否 | 1 / 1 / 0 | 1 / 1 / 0 | 43416 |
| Release | 4032 | 是 / 是 | 1 / 1 / 0 | 1 / 1 / 0 | 33952 |

两阶段各完成 21 个显式步骤。两类冲突都由真实系统注册返回 `ERROR_HOTKEY_ALREADY_REGISTERED`（1409），随后旧手势重新注册；Win+Space 钩子切回标准手势后线程 ID 均归零。最终主热键固定为 `Ctrl+Shift+F23`，搜索热键固定为 `Ctrl+Alt+F24`，两个进程都经正常应用关闭路径自然退出。

两个进程使用的 EXE SHA-256 相同：

`92D56CA740601C1893A8EC1EBE87B8BB2F7C25ED62222959FAC513EEF53B15CE`

正式数据运行前后均为 122 个文件、305,875,736 bytes，指纹保持：

`CDCBF00CDADCA48047B5911BAD976AB9225E4B271A1A390487F6D52AA5CC6AA1`

成功证据归档在 `.artifacts/aot-hotkey-smoke/win-x64/hotkey-runs/3f8971b8d3bc40cebca69f0c425c55fc/`；运行器会把最近一次成功矩阵的索引写入 `.artifacts/aot-hotkey-smoke/win-x64/hotkey-session.json`。预览根在所有权标记复核后清理，没有残留受审计 AOT 进程。

## 4. 证据边界

标准手势的输入来源明确记录为 `SyntheticSendInputForRegisterHotKeyOnly`。它证明 Windows 接收组合键并通过 `RegisterHotKey` 回送 `WM_HOTKEY`，不能证明真实键盘驱动、远程桌面、键盘布局或用户按键时序。

结构化结果固定记录：

- `physicalStandardKeyboardVerified=false`；
- `physicalWinSpaceVerified=false`；
- `physicalRecorderVerified=false`；
- `reservedHookSyntheticTriggerAttempted=false`。

此前 Win+Space 修复中的内部屏蔽键 `0xE8`、设置录制器和引导录制器仍必须由物理键盘核对。低级钩子本身会忽略 `LLKHF_INJECTED`，因此使用合成输入触发它既不能提供物理证据，也会破坏测试结论。

## 5. AOT 审计与回归

profile 52 / schema 49 的标准 x64 发布审计通过：

- 发布目录 39 个文件、90,387,925 bytes；符号目录 3 个文件、202,756,096 bytes；
- 审计用时 281,999 ms，审计期间源码稳定；
- WMC1506 为 0，WMC1510 精确保持 1211；
- 完整 `always-throw` 为 0，5B-4C2A 缺失模式、禁止范围和目标源警告均为 0；
- Rust ABI 2、能力 511、十个导出和 staging/publish 哈希门禁通过；
- 5B-4C2A 新增 6 条阶段契约，搜索热键增加 2 条产品安全契约；全部 AOT 相关测试 423/423；
- 规范 x64 全量测试 2420/2420；Rust workspace 57/57；
- `cargo fmt --check`、Clippy `-D warnings` 和四个相关 PowerShell 脚本解析通过；
- JSON 固定清单更新为 24 个文件、59/59 处 source-generated 调用和 22 个 context 所有者。

项目仍保留已冻结的普通 C# 编译器警告和 WMC1510 基线，本阶段没有把它们描述为新增问题或全项目零警告。

## 6. Rust 决策

本阶段不扩展 Rust。热键链路的核心是 HWND subclass、`WM_HOTKEY`、DispatcherQueue 和低级键盘 hook 的反向回调与线程生命周期，状态量很小，没有可量化的托管内存热点。改成 Rust 需要增加跨语言回调、线程关闭和 UI 投递 ABI，复杂度高于现有直接 Win32 边界，也不能证明会降低常驻内存。

这符合当前选择原则：完整 Rust 只用于确实能缩小复杂 COM 边界或有明确内存/计算收益的模块；简单 P/Invoke、窗口消息和 UI 生命周期继续保留 C#。

## 7. 下一步建议

5B-4C2B 仍是发布前必须完成的人工门：物理标准热键、物理 Win+Space、设置与引导录制器、内部 `0xE8` 屏蔽键、禁用/重启和一次触发语义。它不需要先修改产品代码，只有实测发现缺陷时才做窄修复并重跑 5B-4C2A。

在等待人工输入门时，下一项独立代码开发建议为 **5B-4C3A：Todo recurrence/reminder 的确定性 AOT 状态矩阵**。现有服务已经具备可注入 clock、owned store 和较完整单元测试，可先在隔离根验证 due date、recurrence 生成、提醒候选、snooze、complete、重启和恢复，不弹真实系统通知。该批复杂度为中等，外部状态和恢复边界明显低于 GSMTC 媒体会话、真实天气网络/定位和全局剪贴板图片。

系统通知展示与 activation 可拆到 5B-4C3B；受控 GSMTC 媒体 UI/播放控制、真实 Weather 网络/定位、Quick Capture 全局剪贴板图片和安装升级继续单独分批。5B-4C1C2B 与 5B-4C2B 的人工门在发布前必须关闭，但不阻塞 5B-4C3A 的隔离开发。
