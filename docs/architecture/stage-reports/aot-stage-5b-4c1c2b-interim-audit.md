# DeskBox AOT 阶段 5B-4C1C2B 中间审计

- 日期：2026-08-23
- 状态：部分自动补充证据已完成，真人 Explorer 物理鼠标与视觉验收未完成
- run ID：`771a45a536f84881ae89c04362c7299d`
- 结构化会话状态：`AwaitingManualRound1`
- 证据原则：真实 Explorer 窗口配合注入鼠标只能作为自动补充，不能标记 `PhysicalExplorerMouseVerified=true`

## 1. 已取得的自动补充证据

在受审计 profile 51 / schema 48 AOT 产物上，使用真实 Explorer 窗口和 owned 夹具执行了一轮注入鼠标补充检查：

1. 将文件拖到 `target-folder` 上方时真实目标边框进入高亮；不松开鼠标移出格子并移出 Widget 后，高亮立即清除，日志记录 `Group drag leave cleared tracking`；
2. 小文件移动完成，目标长度 46，SHA-256 为 `C855849CCEF1F3DC1B37A4827F143909D4F925F46516D1CD5AC06EECA9A2D38D`；
3. 文件夹移动完成，嵌套 payload 长度 53，SHA-256 为 `061789043376008D301437CEE263395752B15DDBF57E95394663930839945C17`；
4. 384 MiB 跨卷导入进入 `chunked-cross-volume` 路径，耗时约 249 ms，目标长度 402,653,184，SHA-256 为 `87F509094AD3F0B6BA1E09A28A8B5E6BAED3DB7A3189ED6325669B357CD80C66`；
5. 120 帧、2,108 ms 的裁剪帧序列显示：释放后约 32 ms 仍有一个系统 drag image，约 166 ms 消失，约 183 ms 出现进度卡，首个可见值约为 `134 MB / 384 MB`；本轮没有观察到 drag image 与进度卡重叠；
6. File Widget 向 Explorer 拖出时，Esc 取消保持源文件且目标为空；随后实际 drop 成功移动 `outbound.txt`，长度 50，SHA-256 为 `93659B00BE3CA98C87F38FD5F02FADB92DB88958C329F635778D360AB3176BBE`，日志记录外部拖出已协调；
7. owned 小文件、文件夹和 outbound 基线均已恢复，大文件目标副本保留在 evidence 目录供复核。

会话索引位于 `.artifacts/aot-managed-ui-smoke/win-x64/native-drop-physical-sessions/771a45a536f84881ae89c04362c7299d.json`，帧序列位于该 run 的 `auto-large-progress-frames` 目录。

## 2. 未通过或不能由该方法证明的项目

注入 Ctrl 时，即使 `GetAsyncKeyState` 显示 Ctrl 按下，Explorer/产品仍显示并执行“移动”，没有形成可信的 copy 效果。因此没有重复扩大自动化范围，也没有把该结果写成 Ctrl copy 失败的产品结论；它仍需物理键盘和鼠标组合验证。

以下项目保持 Pending：

- 真人物理鼠标的小文件、文件夹和大文件拖入；
- 物理 Ctrl copy 与无 Ctrl move；
- 人眼确认系统 drag image 只有一层；
- 人眼确认进度卡始终置顶且毛玻璃符合设计；
- 100% 之外至少一个 DPI 缩放，以及可用时的跨显示器路径；
- 录像或关键截图与人工签字式结论。

结构化会话仍记录 `physicalExplorerMouseVerified=false`，各 manual check 仍为 `Pending`。该状态是正确的，不能因自动补充结果而改成完成。

## 3. 清理与风险边界

受审计 AOT PID 和监视进程已停止。owned fixture 的主要基线已恢复；预览根仍保留，因为会话尚待人工验证。一个只含所有权标记的临时 C 盘目录和保存的大文件证据副本也未强制删除，避免在安全校验拒绝后扩大删除权限。

本轮没有修改产品 Rust ABI。拖放 pointer、高亮、系统 drag image 和 WinUI 合成继续属于 C#/COM/UI 生命周期边界，没有显示出迁移 Rust 可降低内存或复杂度的证据。

## 4. 结论

自动补充结果提高了对高亮清理、移动路径、大文件进度时序和外部拖出的信心，但 5B-4C1C2B 仍未完成。它继续作为发布前人工门，与 5B-4C2B 物理热键门并列；两者不阻止其他隔离 AOT 开发，但在声明 x64 AOT 可发布前必须关闭。
