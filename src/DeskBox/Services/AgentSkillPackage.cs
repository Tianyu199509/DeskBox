using System.Text;

namespace DeskBox.Services;

/// <summary>The installed skill is a small entry point; the running server owns the current specification.</summary>
internal static class AgentSkillPackage
{
    internal static readonly UTF8Encoding Utf8NoBom = new(false, true);

    internal const string McpInstructions = "For DeskBox widget work, call deskbox_get_widget_spec first to read the current runtime contract, and call deskbox_get_studio_context when collaborating with an open Widget Workshop session. A local skill is optional. deskbox_upsert_widget stages by default and leaves the desktop unchanged. Commit only after user confirmation, using the same stageId and staged content.";
    internal const string Markdown = """
---
name: deskbox-widget
description: Create, edit, preview, and update native DeskBox desktop widgets through the DeskBox MCP tools. Use when the user asks to build or change a DeskBox widget or send widget telemetry.
---

# DeskBox widget workflow

1. Discover the `deskbox` MCP tools and call `deskbox_get_widget_spec` before designing or changing a widget. That tool returns the current runtime specification. Use `deskbox_get_capability_spec` for individual capabilities, and call `deskbox_get_studio_context` to inspect any open Widget Workshop draft or selected node. Do not use a cached manifest schema as the authority.
2. Read the existing widget before changing it. Validate the manifest with `deskbox_validate_manifest`.
3. `deskbox_upsert_widget` is STAGE-BY-DEFAULT. Without `commit`, it returns a `stageId` and does not change the desktop. Present the proposed result for review.
4. Only after the user confirms, submit `commit: true` with the same `stageId` and exact staged content. Reusing a committed stageId is idempotent. A file save in a draft workspace updates the preview, not the desktop.
5. Use `deskbox_capture_widget_preview` to inspect a live widget when appropriate. `deskbox_push_widget_data` changes live widget state; use it only when the user asks for those updates.

If tools are missing, ask the user to configure DeskBox in their client and follow its refresh or trust instructions. Do not edit client trust stores, bypass approvals, or scan unrelated repositories. A missing skill does not prevent MCP use: the specification is also available directly from `deskbox_get_widget_spec`.
""";

    internal static bool HasValidFrontmatter(string content)
    {
        if (!content.StartsWith("---\n", StringComparison.Ordinal) && !content.StartsWith("---\r\n", StringComparison.Ordinal)) return false;
        string[] lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int end = Array.IndexOf(lines, "---", 1);
        if (end < 0) return false;
        return lines.Take(end).Contains("name: deskbox-widget", StringComparer.Ordinal) &&
            lines.Take(end).Any(line => line.StartsWith("description: ", StringComparison.Ordinal) && line.Length > 13);
    }
}
