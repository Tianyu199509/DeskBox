#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""DeskBox AI 创建格子：Agent 工具契约一致性门禁（P3 契约单一真源的第一块）。

检查项：
  1. MCP/CLI 工具清单与设置页文案 key 一一对应；
  2. deskbox_upsert_widget 的输入契约包含 stageId / commit（P2 暂存→提交）；
  3. SKILL.md 工具说明与工具实际行为一致；
  4. 工具说明文案不再宣称「默认直接热重载到桌面」；
  5. MCP 适配器转发 commit / stageId、CLI 提供 --commit/--stage-id、Bridge 暴露四个 stage_* op；
  6. 对外协议文本不再承诺「保存文件即热重载到桌面」；
  7. 重复提交同一 stageId 必须幂等（Bridge 返回 idempotent，SKILL.md 有说明）；
  8. 每个暂存 op 都有工坊侧消费入口，且暂存文案真的被界面引用；
  9. 13 语用户文案不再宣称「桌面自动实时刷新 / 热重载到桌面」；
  10. 远程 Prompt Pack 是否仍为空占位（提示，不阻塞）。

用法：python scripts/check_ai_widget_contract.py
退出码：发现契约漂移为 1，否则为 0。
"""
from __future__ import annotations

import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
CLI = ROOT / "src" / "DeskBox" / "Services" / "DeskBoxAgentCliEntry.cs"
BRIDGE = ROOT / "src" / "DeskBox" / "Services" / "DeskBoxAgentBridgeServer.cs"
TEMPLATES = ROOT / "src" / "DeskBox" / "Services" / "CustomWidgetTemplates.cs"
CONNECTOR = ROOT / "src" / "DeskBox" / "Services" / "CustomWidgetAgentConnectorService.cs"
STRINGS = ROOT / "src" / "DeskBox" / "Strings"
PROMPT_PACK = ROOT / "catalog" / "prompts.v1.json"
APP_STUDIO = ROOT / "src" / "DeskBox" / "App.CustomWidgetStudio.cs"
STUDIO_PAGES = ROOT / "src" / "DeskBox" / "Views" / "CustomWidgetStudioWindow.Pages.cs"
STUDIO_ROOT = ROOT / "src" / "DeskBox" / "Views" / "CustomWidgetStudioWindow.xaml.cs"

# Bridge 暴露的暂存 op ↔ 工坊侧消费入口。协议加了 op 但 UI 没有对应入口，
# 用户就永远看不到「待确认」这件事（上一轮 MCP 适配器丢字段就是同类断点）。
STAGE_OPS = {
    "stage_list": ("App.CustomWidgetStudio.cs", "GetPendingAgentStages"),
    "stage_commit": ("App.CustomWidgetStudio.cs", "CommitAgentStageAsync"),
    "stage_discard": ("App.CustomWidgetStudio.cs", "DiscardAgentStageAsync"),
}

TOOL_KEYS = {
    "deskbox_get_widget_spec": "CustomWidget.AgentHub.Tool.GetSpec",
    "deskbox_get_capability_spec": "CustomWidget.AgentHub.Tool.GetCapabilitySpec",
    "deskbox_get_studio_context": "CustomWidget.AgentHub.Tool.GetStudioContext",
    "deskbox_list_widgets": "CustomWidget.AgentHub.Tool.ListWidgets",
    "deskbox_validate_manifest": "CustomWidget.AgentHub.Tool.Validate",
    "deskbox_upsert_widget": "CustomWidget.AgentHub.Tool.Upsert",
    "deskbox_capture_widget_preview": "CustomWidget.AgentHub.Tool.Capture",
    "deskbox_push_widget_data": "CustomWidget.AgentHub.Tool.PushData",
}

TOOL_NAME = re.compile(r'writer\.WriteString\("name",\s*"(deskbox_[a-z_]+)"\)')
PROP_NAME = re.compile(r'writer\.WriteStartObject\("([A-Za-z]+)"\)')


def fail(problems: list[str], msg: str) -> None:
    problems.append(msg)


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    if hasattr(sys.stderr, "reconfigure"):
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    problems: list[str] = []
    cli = CLI.read_text(encoding="utf-8-sig")
    templates = TEMPLATES.read_text(encoding="utf-8-sig")
    tools = sorted(set(TOOL_NAME.findall(cli)))

    unregistered = [t for t in tools if t not in TOOL_KEYS]
    if unregistered:
        fail(problems, f"新增工具尚未登记文案 key：{', '.join(unregistered)}")

    catalogs = {}
    for path in sorted(STRINGS.glob("*.json")):
        with path.open("r", encoding="utf-8-sig") as f:
            catalogs[path.stem] = json.load(f)

    for tool, key in TOOL_KEYS.items():
        if tool not in tools:
            fail(problems, f"文案 key {key} 对应的工具已不存在：{tool}")
        for lang, data in catalogs.items():
            if key not in data:
                fail(problems, f"{lang} 缺少工具文案 {key}")

    # upsert 工具块内的输入属性
    upsert_start = cli.find('writer.WriteString("name", "deskbox_upsert_widget")')
    upsert_end = cli.find('writer.WriteString("name", "deskbox_capture_widget_preview")', upsert_start) if upsert_start >= 0 else -1
    if upsert_start < 0:
        fail(problems, "找不到 deskbox_upsert_widget 工具定义")
    else:
        block = cli[upsert_start: upsert_end if upsert_end > upsert_start else upsert_start + 3500]
        props = set(PROP_NAME.findall(block))
        for required in ("stageId", "commit", "widgetId", "manifestJson"):
            if required not in props:
                fail(problems, f"upsert 契约缺少属性 {required}")

    # SKILL.md 中 upsert 一行的行为描述
    skill_line = next(
        (line for line in templates.splitlines() if "deskbox_upsert_widget" in line and "`" in line),
        "",
    )
    if not skill_line:
        fail(problems, "SKILL.md 常量中找不到 deskbox_upsert_widget 说明")
    elif "commit" not in skill_line:
        fail(problems, "SKILL.md 的 upsert 说明未同步「暂存→提交」语义")

    # Contract-first 第一步：schema 里的每个输入参数都必须在 SKILL.md 的工具清单里写明。
    # 之前 Agent 能从 schema 看到 locales / resizeToDefault，文档却没提——文档与契约各自演进。
    doc_line = next(
        (line for line in templates.splitlines()
         if line.lstrip().startswith("- `deskbox_upsert_widget`")),
        "",
    )
    if not doc_line:
        fail(problems, "SKILL.md 工具清单缺少 deskbox_upsert_widget 条目")
    else:
        schema_props = set(PROP_NAME.findall(cli[upsert_start: upsert_end if upsert_end > upsert_start else upsert_start + 3500])) - {"properties"}
        undocumented = sorted(p for p in schema_props if p not in doc_line)
        if undocumented:
            fail(problems, "SKILL.md 未记录 upsert 输入参数：" + ", ".join(undocumented))

    upsert_copy = catalogs.get("zh-CN", {}).get("CustomWidget.AgentHub.Tool.Upsert", "")
    if "暂存" not in upsert_copy:
        fail(problems, "工具说明仍宣称默认直接写桌面，未同步 P2 暂存语义")

    # MCP upsert 适配器必须把 commit / stageId 原样转发给 Bridge，
    # 否则「先暂存 → 用户确认 → 再提交」在 MCP 通道上永远无法闭环。
    handler_start = cli.find('case "deskbox_upsert_widget":')
    handler_end = cli.find('case "deskbox_capture_widget_preview":', handler_start)
    handler = cli[handler_start:handler_end] if handler_start >= 0 and handler_end > handler_start else ""
    if not handler:
        fail(problems, "找不到 deskbox_upsert_widget 的 MCP 适配器实现")
    else:
        if 'w.WriteBoolean("commit", true)' not in handler:
            fail(problems, "MCP upsert 适配器未转发 commit=true，外部 Agent 无法提交")
        if 'w.WriteString("stageId", stageId)' not in handler:
            fail(problems, "MCP upsert 适配器未转发 stageId，外部 Agent 无法提交")
        if 'w.WriteBoolean("reveal", true)' in handler:
            fail(problems, "MCP upsert 适配器仍强制 reveal=true")

    # CLI 必须提供显式提交入口，否则 stageId 只能看不能用。
    cli_text = CLI.read_text(encoding="utf-8-sig")
    for flag in ("--commit", "--stage-id"):
        if flag not in cli_text:
            fail(problems, f"CLI 缺少显式提交参数 {flag}")

    bridge_text = BRIDGE.read_text(encoding="utf-8-sig")
    app_text = APP_STUDIO.read_text(encoding="utf-8-sig")
    studio_text = STUDIO_PAGES.read_text(encoding="utf-8-sig") + STUDIO_ROOT.read_text(encoding="utf-8-sig")

    for op, (file_name, entry) in STAGE_OPS.items():
        if f'"{op}"' not in bridge_text:
            fail(problems, f"Bridge 缺少暂存操作 {op}")
        if entry not in app_text:
            fail(problems, f"{file_name} 缺少暂存入口 {entry}（协议有 {op}，界面调不到）")

    for entry in ("TryReadAgentStage", "OpenAgentStagePreview"):
        if entry not in app_text + studio_text:
            fail(problems, f"stage_get 缺少消费入口 {entry}")
    if "GetPendingAgentStages" not in studio_text:
        fail(problems, "stage_list 结果未被界面消费，待确认暂存不会提示给用户")

    # 文案 key 存在不等于用户看得到：必须真的被界面引用。
    for key in (
        "CustomWidget.Workshop.AgentStage.Notice",
        "CustomWidget.Workshop.AgentStage.ViewButton",
        "CustomWidget.Workshop.AgentStage.DiscardButton",
        "CustomWidget.Workshop.AgentStage.ReviewHint",
    ):
        if key not in studio_text:
            fail(problems, f"界面未引用暂存文案 {key}")

    if "_pendingAgentStageId" not in studio_text:
        fail(problems, "CommitAsync 未区分待提交暂存批次，「放到桌面」会绕过 stage_commit")

    # 幂等：stageId 是服务端生成、绑定内容摘要且一次性的，重试提交必须返回成功而不是「不存在」。
    if "TryGetCommitted" not in bridge_text:
        fail(problems, "Bridge 提交路径缺少幂等查询 TryGetCommitted（重试会被误报为失败）")
    if '"idempotent"' not in bridge_text:
        fail(problems, "Bridge 幂等响应未返回 idempotent 标记，Agent 无法区分重复提交")

    # 对外协议文本不得再承诺「保存文件即热重载到桌面」。
    forbidden = (
        "hot-reloads the live Windows 11 desktop widget",
        "hot-reloads the widget on the desktop",
        "hot-reloads on the desktop",
        "instant 200ms hot-reload on the desktop",
        "live-sync",
        "桌面会在 200ms 内实时热重载",
        "热重载到桌面",
    )
    for path in (TEMPLATES, CONNECTOR):
        text = path.read_text(encoding="utf-8-sig")
        for phrase in forbidden:
            if phrase in text:
                fail(problems, f"{path.name} 仍宣称直写桌面：{phrase}")

    templates_text = TEMPLATES.read_text(encoding="utf-8-sig")
    if "STAGE-BY-DEFAULT" not in templates_text:
        fail(problems, "SKILL.md 缺少 STAGE-BY-DEFAULT 写入边界声明")
    if "commit: true" not in templates_text:
        fail(problems, "SKILL.md 未说明显式提交方式")
    if "idempotent: true" not in templates_text:
        fail(problems, "SKILL.md 未说明重复提交的幂等语义，Agent 会把重试当成失败")

    # 协议已改为「先暂存、确认后写桌面」，但用户看到的是 Strings 里的文案。
    # 之前只扫 Templates/Connector，导致 13 语仍在承诺「桌面自动实时刷新」而门禁是绿的。
    string_forbidden = {
        "zh-hans": ("桌面热重载", "桌面会自动实时刷新", "桌面将自动实时更新", "桌面 200ms 内实时刷新",
                    "直接创建、校验、热更新格子"),
        "zh-hant": ("桌面熱重載", "桌面會自動即時重新整理", "桌面將自動即時更新"),
        "en": ("hot-reload on the desktop", "hot-reloads on the desktop", "desktop hot-reload",
               "live-sync", "hot-update the widget", "200ms live hot-reload"),
    }
    for lang, data in catalogs.items():
        if lang == "zh-CN":
            patterns = string_forbidden["zh-hans"]
        elif lang == "zh-TW":
            patterns = string_forbidden["zh-hant"]
        else:
            patterns = string_forbidden["en"]
        for key, value in data.items():
            if not isinstance(value, str):
                continue
            for phrase in patterns:
                if phrase in value:
                    fail(problems, f"{lang} 文案 {key} 仍宣称直写桌面：{phrase}")

    # 这 8 条已按「先进预览、确认后写桌面」重写，回归时不允许再退回旧承诺。
    desktop_claim_keys = (
        "CustomWidget.Workshop.AgentMcp.Step3",
        "CustomWidget.Workshop.AgentMcp.Tour3Subtitle",
        "CustomWidget.Workshop.AgentMcp.SyncedBanner",
        "CustomWidget.Workshop.AgentStage.ReviewHint",
        "CustomWidget.AgentHub.BridgeDescription",
        "CustomWidget.AgentHub.BridgeSubtitleReady",
        "CustomWidget.AiWizard.Agent.Description",
        "CustomWidget.AgentHub.Workspace.Desc",
    )
    zh_catalog = catalogs.get("zh-CN", {})
    for key in desktop_claim_keys:
        zh_text = zh_catalog.get(key)
        if zh_text is None:
            fail(problems, f"zh-CN 缺少暂存边界文案 {key}")
            continue
        for bad in ("热重载", "自动刷新", "自动更新"):
            if bad in zh_text:
                fail(problems, f"zh-CN {key} 仍使用旧承诺措辞：{bad}")
        if "桌面" not in zh_text:
            fail(problems, f"zh-CN {key} 未说明桌面写入时机")

    with PROMPT_PACK.open("r", encoding="utf-8-sig") as f:
        pack = json.load(f)
    placeholder = len(pack) <= 4 and "overrides" not in pack

    print(f"MCP/CLI 工具: {len(tools)}；语言文件: {len(catalogs)}")
    print(f"远程 Prompt Pack: {'占位（OTA 覆盖未启用）' if placeholder else '含覆盖内容'}")
    for p in problems:
        print("  [FAIL]", p)
    if not problems:
        print("  [OK] 工具清单、MCP/CLI 提交转发、幂等提交、Bridge 暂存操作、SKILL.md 与工具文案一致")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
