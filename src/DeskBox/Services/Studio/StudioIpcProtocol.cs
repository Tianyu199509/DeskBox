using System.IO;
using System.Text;
using System.Text.Json;
using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Services.Studio;

/// <summary>
/// Wire method names and AOT source-generated DTO payloads for the out-of-process
/// Custom Widget Studio companion host (<c>DeskBox.exe --studio &lt;mainPid&gt;</c>).
/// Intentionally free of global application or service-locator references.
/// </summary>
internal static class StudioIpcMethods
{
    // ── Studio host -> Main process requests ────────────────────────────
    public const string Bootstrap = "studio.bootstrap";
    public const string HostBootReady = "studio.boot-ready";
    public const string HostFarewell = "studio.farewell";
    public const string ReportWindowState = "studio.window-state";
    public const string ReportStudioContext = "studio.report-context";
    public const string ReportStagePng = "studio.report-stage-png";
    public const string BringToFront = "studio.bring-to-front";
    public const string ReleaseBand = "studio.release-band";
    public const string CreateWidget = "studio.create-widget";
    public const string UpdateWidgetManifest = "studio.update-manifest";
    public const string ApplyMutation = "studio.apply-mutation";
    public const string ApplyStoreData = "studio.apply-store-data";
    public const string SetWidgetEnabled = "studio.set-enabled";
    public const string LocateWidget = "studio.locate";
    public const string RemoveWidget = "studio.remove";
    public const string ShowNotification = "studio.notify";
    public const string CommitAgentStage = "studio.stage-commit";
    public const string DiscardAgentStage = "studio.stage-discard";

    // ── Main process -> Studio host pushed events ───────────────────────
    public const string HostShow = "studio.host.show";
    public const string HostShowPreview = "studio.host.show-preview";
    public const string HostShutdown = "studio.host.shutdown";
    public const string SettingsReloaded = "settings-reloaded";
    public const string LayoutChanged = "layout-changed";
    public const string DisplayTopologyChanged = "app.display-topology-changed";
    public const string AgentWidgetUpserted = "studio.evt.agent-upserted";
    public const string McpClientConnected = "studio.evt.mcp-connected";
    public const string McpActivityRecorded = "studio.evt.mcp-activity";
    public const string AgentStageChanged = "studio.evt.stage-changed";
    public const string RequestStudioContext = "studio.evt.request-context";
    public const string CaptureStudioStagePng = "studio.evt.capture-stage-png";
}

internal sealed record StudioShowIntentPayload(
    string? WidgetId,
    string? InitialIdea,
    bool AutoStartGeneration = true);

internal sealed record StudioShowPreviewIntentPayload(
    string ManifestJson,
    string? SourceLabel = null,
    string? LogicJs = null,
    string? LocalesJson = null,
    bool HasLocales = false,
    string? OriginKind = null);

internal sealed record StudioBootstrapResponse(
    string SettingsSnapshotJson,
    ConnectedMcpClientInfo? LastMcpClient = null,
    StudioShowIntentPayload? InitialShowIntent = null,
    StudioShowPreviewIntentPayload? InitialPreviewIntent = null);

internal sealed record StudioWindowStatePayload(
    long WindowHandle,
    string? TargetWidgetId,
    long StateSequence = 0);

internal sealed record StudioWindowBandPayload(
    long WindowHandle,
    string Reason);

internal sealed record StudioCreateWidgetRequest(
    string? TemplateOrPackageSpec,
    string? LogicJsOverride = null,
    bool HasLogicJsOverride = false,
    string? LocalesJsonOverride = null,
    bool HasLocalesOverride = false,
    Dictionary<string, string>? InitialSettings = null,
    Dictionary<string, string>? InitialSecrets = null);

internal sealed record StudioUpdateManifestRequest(
    string WidgetId,
    string ManifestJson,
    bool ResizeToDefault = false,
    bool Reveal = false,
    string? LogicJs = null,
    bool UpdateLogicJs = false,
    string? LocalesJson = null,
    bool HasLocales = false,
    bool UpdateLocales = false);

internal sealed record StudioSetWidgetEnabledRequest(
    string WidgetId,
    bool Enabled);

internal sealed record StudioNotificationPayload(
    string Title,
    string Body);

internal sealed record StudioWidgetMutationResponse(
    GlanceWidgetInstanceInfo? Instance,
    string LayoutSliceJson);

internal sealed record StudioStageCommitResponse(
    string ResponseJson,
    string LayoutSliceJson);

internal sealed record StudioAgentUpsertEventPayload(
    string WidgetId,
    string WidgetName,
    string ManifestJson,
    string? LogicJs,
    bool HasLogicJs,
    string? LocalesJson,
    bool HasLocales);

internal sealed record StudioMcpActivityPayload(
    string Op,
    string Detail);

internal sealed record StudioContextRequestPayload(
    string RequestId,
    bool IncludeManifest = true);

internal sealed record StudioContextReportPayload(
    string RequestId,
    string ContextJson,
    string? TargetWidgetId);

internal sealed record StudioStagePngCaptureRequest(
    string RequestId,
    bool CapsuleMode = false);

internal sealed record StudioStagePngCaptureReport(
    string RequestId,
    bool Ok,
    byte[]? PngBytes,
    int Width,
    int Height,
    int OverflowPx,
    string? Error = null);

internal static class StudioStagePngWireHelper
{
    public static string EncodeRequest(StudioStagePngCaptureRequest request)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("requestId", request.RequestId);
            writer.WriteBoolean("capsuleMode", request.CapsuleMode);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static bool TryDecodeRequest(string? json, out StudioStagePngCaptureRequest? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("requestId", out JsonElement idEl) ||
                idEl.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(idEl.GetString()))
            {
                return false;
            }

            bool capsule = root.TryGetProperty("capsuleMode", out JsonElement capEl) &&
                           capEl.ValueKind == JsonValueKind.True;
            request = new StudioStagePngCaptureRequest(idEl.GetString()!, capsule);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string EncodeReport(StudioStagePngCaptureReport report)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("requestId", report.RequestId);
            writer.WriteBoolean("ok", report.Ok);
            if (report.PngBytes is { Length: > 0 })
            {
                writer.WriteString("pngBase64", Convert.ToBase64String(report.PngBytes));
            }
            writer.WriteNumber("width", report.Width);
            writer.WriteNumber("height", report.Height);
            writer.WriteNumber("overflowPx", report.OverflowPx);
            if (!string.IsNullOrWhiteSpace(report.Error))
            {
                writer.WriteString("error", report.Error);
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static bool TryDecodeReport(string? json, out StudioStagePngCaptureReport? report)
    {
        report = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("requestId", out JsonElement idEl) ||
                idEl.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(idEl.GetString()))
            {
                return false;
            }

            bool ok = root.TryGetProperty("ok", out JsonElement okEl) && okEl.ValueKind == JsonValueKind.True;
            byte[]? pngBytes = null;
            if (root.TryGetProperty("pngBase64", out JsonElement b64El) &&
                b64El.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(b64El.GetString()))
            {
                pngBytes = Convert.FromBase64String(b64El.GetString()!);
            }

            int width = root.TryGetProperty("width", out JsonElement wEl) && wEl.TryGetInt32(out int w) ? w : 0;
            int height = root.TryGetProperty("height", out JsonElement hEl) && hEl.TryGetInt32(out int h) ? h : 0;
            int overflowPx = root.TryGetProperty("overflowPx", out JsonElement oEl) && oEl.TryGetInt32(out int o) ? o : 0;
            string? error = root.TryGetProperty("error", out JsonElement errEl) && errEl.ValueKind == JsonValueKind.String
                ? errEl.GetString()
                : null;
            report = new StudioStagePngCaptureReport(idEl.GetString()!, ok, pngBytes, width, height, overflowPx, error);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

internal static class StudioLocaleWireHelper
{
    public static string? Serialize(CustomWidgetLocaleCatalog? locales)
    {
        if (locales is null || locales.IsEmpty)
        {
            return null;
        }

        return locales.SerializeToCanonicalJson(indented: false);
    }

    public static CustomWidgetLocaleCatalog? Deserialize(string? localesJson, bool hasLocales)
    {
        if (!hasLocales || string.IsNullOrWhiteSpace(localesJson))
        {
            return null;
        }

        CustomWidgetLocaleCatalog parsed = CustomWidgetLocaleCatalog.ParseCatalogJson(localesJson);
        return parsed.IsEmpty ? null : parsed;
    }
}
