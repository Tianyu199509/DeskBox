# 外部 AI 客户端接入改造

## 范围与原则

本批在现有工作区上修复接入可靠性、收拢设置界面，并统一客户端定义。不提交、不推送、不发布。用户的第三方客户端不由 DeskBox 自动重启。

- MCP 是主路径，安装的 Skill 只负责发现入口和操作边界。完整能力规范仍由运行中的 DeskBox 提供。
- 配置存在、文件有效、客户端加载、实际工具调用是不同证据。界面不会用本地自检代替客户端验证。
- 检测和复制工坊提示词没有配置写入副作用。只有明确配置、更新或导出动作可以写入。
- 用户的禁用、超时、环境变量、工具授权规则必须保留；不生成第三方信任记录或自动授权列表。

## 实现划分

| 所有者 | 职责 |
|---|---|
| AgentClientRegistry | 客户端名称、支持级别、配置候选路径、格式、Skill 位置 |
| AgentCodexConfig | TOML 语法解析、按语法节点范围更新 command/args、保留其余文本和语义 |
| AgentConfigFileStore | 同机 DeskBox 写入互斥、内容冲突检查、备份与原子替换 |
| AgentSkillPackage | 精简 Skill、无 BOM UTF-8、回读验证、内容摘要、本地修改保护与旧副本迁移 |
| AgentConnectionDiagnostics | 只读检查配置、禁用状态、命令路径和 Skill 状态 |
| CustomWidgetAgentConnectorService | 调度指定客户端的配置和回读，不隐式同步其他客户端 |

Codex 用户 Skill 只新建在 `.agents/skills`。已知的旧 `.codex/skills/deskbox-widget/SKILL.md` 会保留为备份后退出扫描；无法识别的副本不删除。Codex 配置尊重 `CODEX_HOME`。

旧 Skill 没有安装摘要，只有与已知官方内容一致时才能直接更新。无法确认来源或存在本地修改时，界面要求用户明确选择备份并替换。新安装使用 `.deskbox-install.json` 保存版本与摘要。

配置文件使用乐观并发检查，并互斥 DeskBox 自己的写入。第三方客户端不共享 DeskBox 锁，因此这不是跨应用事务；检测到内容变化会拒绝替换。`.deskbox.bak` 保存上一次实际修改前的内容，无变化时不重写。

## 界面

设置主界面优先展示检测到或已配置的 Codex、WorkBuddy，每项显示下一步动作。实验性、仅模板以及未检测到的客户端在“其他客户端”中，保留配置入口。

手动配置、Skill 导出、HTTP 与本地服务检查位于高级区域。工作区和编辑器操作沿用工坊入口，设置不再重复放置。状态看板收进使用示例。

“验证”检查本地配置，并提供复制到目标客户端的只读验证提示。它不自动把客户端标成已生效，也不启动、关闭或接管目标客户端。完整客户端验证仍以真实工具调用结果为准。

## 客户端支持范围

| 客户端 | 本批定位 | 真实客户端验收 |
|---|---|---|
| Codex、WorkBuddy | 优先维护的接入路径 | Codex 的真实 skills/list 已确认新 Skill 启用且不再报错；WorkBuddy 旧说明已通过新设置界面备份并更新。完整客户端暂存提交仍需复验 |
| Claude Code、Cursor、VS Code、Trae、Windsurf、Claude Desktop、CodeBuddy | 实验性配置适配 | 未逐个真机验证，不作已验证承诺 |
| Gemini CLI、Antigravity、Cline、Roo Code | 分别检测与配置的实验性适配 | 不共享已配置状态，待逐个真机验证 |
| DeepSeek Harness | 仅配置模板 | 不再自动改写未验证的 YAML 配置 |

## 验收与后续

自动化覆盖编码与回读、TOML 语法变体、Unicode 路径、用户字段保留、禁用状态、冲突拒绝、检测只读、Skill 修改保护、迁移和幂等。

本轮验证结果：

- 全量 x64 测试 6664/6664 通过，失败 0、跳过 0，其中新增接入行为用例 16 项。
- AOT 条件编译检查通过；TOML 编辑器单独完成 Native AOT 发布与运行，含中文、Emoji、单引号路径，没有依赖反射。
- 13 语言键集、MCP/CLI 契约、设置搜索目录检查通过。
- 真实设置页执行 Codex 同步后，独立 Codex app-server 的 `skills/list(forceReload: true)` 从两条 `missing YAML frontmatter delimited by ---` 变为一条 `.agents/skills/deskbox-widget/SKILL.md`，`enabled: true`，没有该 Skill 的解析错误。
- WorkBuddy 原有说明先保存为 `.deskbox.bak`，再更新为与 Codex 相同的无 BOM 入口包。其他未测试客户端没有被批量同步。
- 最终界面收拢后完成 canonical 全量重建，DeskBox.dll 写入时间为 2026-11-06 13:18:55，晚于本轮源码。Debug 主进程 PID 43428，启动窗错误 0；`widget status` 返回 online、同一 PID、7 个工具。
- 证据保存在 `.artifacts/agent-connection-20261106/`。真实设置页已经完成接入操作核对；最终布局确认主列表只有 Codex 和 WorkBuddy，其他 12 个客户端折叠，验证对话框明确区分本地文件检查与真实客户端调用。

文件写入先前触发的模块边界检查已通过归入 `DeskBox.Core.Persistence` 及复用平台文件原语解决，没有放宽测试。新增 SettingsExpander 的本地化使用计数与原生类型契约已同步。

下一批优先在 Codex 和 WorkBuddy 中逐项验收：加载工具、读取最新规范、暂存不改变桌面、用户确认后提交、重复提交幂等、禁用后不被更新重新启用。通过后再扩大 Claude Code 与 Cursor 的支持承诺。

Direct/Store、x64/ARM64 的真实客户端联调不由 Debug 单元测试代替。商店升级后的程序路径变化仍需在实际安装环境中验证。
