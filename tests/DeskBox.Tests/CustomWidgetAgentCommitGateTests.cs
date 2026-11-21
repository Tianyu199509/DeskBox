using System.Text.Json;
using DeskBox.FileSafety;
using DeskBox.Services;
using Xunit;

namespace DeskBox.Tests;

public sealed class CustomWidgetAgentCommitGateTests : IDisposable
{
    private readonly string _tempStageRoot =
        Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "AgentCommitGate", Guid.NewGuid().ToString("N"));
    private readonly string _tempTrustRoot =
        Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "AgentCommitGateTrust", Guid.NewGuid().ToString("N"));

    public CustomWidgetAgentCommitGateTests()
    {
        Directory.CreateDirectory(_tempStageRoot);
        CustomWidgetAgentStageStore.RootOverride = _tempStageRoot;
    }

    public void Dispose()
    {
        CustomWidgetAgentStageStore.RootOverride = string.Empty;
        try { Directory.Delete(_tempStageRoot, recursive: true); } catch { }
        try { Directory.Delete(_tempTrustRoot, recursive: true); } catch { }
    }

    private const string ManifestCompact = "{\"id\":\"deskbox.stage.gate\",\"schemaVersion\":1}";

    private string StageBundle(string? widgetId = "w-gate-1") => CustomWidgetAgentStageStore.Stage(new AgentStageRequest(
        WidgetId: widgetId,
        ManifestJson: ManifestCompact,
        LogicJs: "function onInit(ctx) { ctx.state.ready = '1'; }",
        HasLogicJs: true,
        LocalesJson: null,
        HasLocales: false,
        ResizeToDefault: false));

    [Fact]
    public void CommitLease_RejectsConcurrentReplay_AndCompletesOnlyAfterWorkspaceWrite()
    {
        string stageId = StageBundle();

        Assert.True(CustomWidgetAgentStageStore.TryConsumeBundle(
            stageId, ManifestCompact,
            hasCallerLogicJs: false, callerLogicJs: null,
            hasCallerLocales: false, callerLocalesJson: null,
            out _, out string firstError),
            firstError);

        // A concurrent replay of the same stage is rejected while the commit is in flight
        // instead of creating a duplicate widget.
        Assert.False(CustomWidgetAgentStageStore.TryConsumeBundle(
            stageId, ManifestCompact,
            hasCallerLogicJs: false, callerLogicJs: null,
            hasCallerLocales: false, callerLocalesJson: null,
            out _, out string replayError));
        Assert.False(string.IsNullOrWhiteSpace(replayError));
        Assert.False(CustomWidgetAgentStageStore.TryGetCommitted(stageId, out _, ManifestCompact,
            hasCallerLogicJs: false, callerLogicJs: null,
            hasCallerLocales: false, callerLocalesJson: null));

        // A failed workspace write keeps the bundle retryable.
        CustomWidgetAgentStageStore.AbortCommit(stageId);
        Assert.True(CustomWidgetAgentStageStore.TryConsumeBundle(
            stageId, ManifestCompact,
            hasCallerLogicJs: false, callerLogicJs: null,
            hasCallerLocales: false, callerLocalesJson: null,
            out _, out string retryError),
            retryError);

        // Success consumes the lease: directory removed, idempotency tombstone written.
        CustomWidgetAgentStageStore.CompleteCommit(stageId);
        Assert.True(CustomWidgetAgentStageStore.TryGetCommitted(stageId, out string? committedId, ManifestCompact,
            hasCallerLogicJs: false, callerLogicJs: null,
            hasCallerLocales: false, callerLocalesJson: null));
        Assert.Equal("w-gate-1", committedId);
        Assert.False(Directory.Exists(Path.Combine(_tempStageRoot, "stages", stageId)) &&
                     Directory.Exists(_tempStageRoot));
    }

    [Fact]
    public void ExecutableSurfaceFingerprint_ExternalChangeRevokesPrivilegedOverrides_StudioChangeKeepsThem()
    {
        var store = new CustomWidgetTrustStore(_tempTrustRoot);
        const string widgetId = "custom.fingerprint-w";
        string manifestV1 = "{\"id\":\"custom.fingerprint-w\",\"schemaVersion\":1,\"elements\":{\"root\":{\"type\":\"Text\",\"text\":\"v1\"}}}";
        string manifestV2 = "{\"id\":\"custom.fingerprint-w\",\"schemaVersion\":1,\"elements\":{\"root\":{\"type\":\"Text\",\"text\":\"v2\"}}}";
        const string logicV1 = "export function onInit(ctx) { ctx.computed.banner = 'v1'; }";
        string logicV2 = logicV1.Replace("v1", "v2", StringComparison.Ordinal);

        store.SetPermissionOverride(widgetId, CustomWidgetPermissionKind.CommandExecution, enabled: true);
        store.SetPermissionOverride(widgetId, CustomWidgetPermissionKind.Notifications, enabled: true);
        store.RecordExecutableSurfaceAfterWrite(widgetId, manifestV1, logicV1, trustedLocalAuthoring: true);

        // Baseline hash is stable and fingerprints script + privileged manifest nodes.
        Assert.Equal(
            CustomWidgetTrustStore.ComputeExecutableSurfaceHash(manifestV1, logicV1),
            CustomWidgetTrustStore.ComputeExecutableSurfaceHash(manifestV1, logicV1));
        Assert.NotEqual(
            CustomWidgetTrustStore.ComputeExecutableSurfaceHash(manifestV1, logicV1),
            CustomWidgetTrustStore.ComputeExecutableSurfaceHash(manifestV1, logicV2));
        // CRLF-only script differences do not change the fingerprint.
        Assert.Equal(
            CustomWidgetTrustStore.ComputeExecutableSurfaceHash(manifestV1, logicV1),
            CustomWidgetTrustStore.ComputeExecutableSurfaceHash(manifestV1, logicV1.Replace("\n", "\r\n", StringComparison.Ordinal)));

        // Studio (trusted) save with a changed surface keeps grants.
        store.RecordExecutableSurfaceAfterWrite(widgetId, manifestV2, logicV2, trustedLocalAuthoring: true);
        Assert.True(store.GetProfile(widgetId).IsPermissionGranted(CustomWidgetPermissionKind.CommandExecution));

        // External (Agent/OTA) overwrite with a changed surface revokes only positive privileged overrides.
        store.SetPermissionOverride(widgetId, CustomWidgetPermissionKind.CommandExecution, enabled: true);
        store.RecordExecutableSurfaceAfterWrite(widgetId, manifestV1, logicV1, trustedLocalAuthoring: false);
        Assert.False(store.GetProfile(widgetId).IsPermissionGranted(CustomWidgetPermissionKind.CommandExecution));
        Assert.False(store.GetProfile(widgetId).IsPermissionGranted(CustomWidgetPermissionKind.LocalFileAccess));
        // Non-privileged grants survive.
        Assert.True(store.GetProfile(widgetId).IsPermissionGranted(CustomWidgetPermissionKind.Notifications));

        // External write with an UNCHANGED surface keeps grants (no re-prompt on idempotent writes).
        store.SetPermissionOverride(widgetId, CustomWidgetPermissionKind.CommandExecution, enabled: true);
        store.RecordExecutableSurfaceAfterWrite(widgetId, manifestV1, logicV1, trustedLocalAuthoring: false);
        Assert.True(store.GetProfile(widgetId).IsPermissionGranted(CustomWidgetPermissionKind.CommandExecution));
    }

    [Fact]
    public async Task BridgeUpsert_PrivilegedCommit_DeniedHostApproval_KeepsStageUnconsumed()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", "AgentCommitGateE2E", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            const string privilegedManifest =
                "{\"id\":\"custom.gate-priv\",\"schemaVersion\":1,\"capabilities\":[\"local-files-and-commands\"],\"root\":\"root\",\"elements\":{\"root\":{\"type\":\"Text\",\"text\":\"X\"}}}";
            string stageId = CustomWidgetAgentStageStore.Stage(new AgentStageRequest(
                WidgetId: "w-gate-priv",
                ManifestJson: privilegedManifest,
                LogicJs: null,
                HasLogicJs: false,
                LocalesJson: null,
                HasLocales: false,
                ResizeToDefault: false));

            var settings = new SettingsService(Path.Combine(sandbox, "settings"));
            await settings.LoadAsync();
            var fileService = new FileService();
            var manager = new WidgetManager(
                settings,
                fileService,
                TestOrganizerServices.Create(settings, fileService),
                new ThemeService(settings),
                new QuickCaptureService(new QuickCaptureStore(Path.Combine(sandbox, "notes"))),
                () => Path.Combine(sandbox, "desktop"),
                recycleManagedFolderDeletes: false);

            AgentCommitApprovalRequest? seenRequest = null;
            await using var bridge = new DeskBoxAgentBridgeServer(
                "DeskBox_ApprovalGate_" + Guid.NewGuid().ToString("N"),
                () => manager,
                commitApprovalProvider: request =>
                {
                    seenRequest = request;
                    return Task.FromResult(false);
                });

            string payload =
                "{\"op\":\"upsert\",\"commit\":true,\"stageId\":\"" + stageId + "\"}";
            using JsonDocument response = JsonDocument.Parse(
                await bridge.DispatchRequestJsonAsync(payload));

            Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
            JsonElement result = response.RootElement.GetProperty("result");
            Assert.False(result.GetProperty("committed").GetBoolean());
            Assert.True(result.GetProperty("requiresHostApproval").GetBoolean());
            Assert.NotNull(seenRequest);
            Assert.True(seenRequest!.DeclaresPrivilegedCapabilities);
            Assert.Equal("w-gate-priv", seenRequest.WidgetId);

            // A denied commit leaves the staged bundle unconsumed and retryable.
            Assert.Contains(CustomWidgetAgentStageStore.ListPending(), s => s.StageId == stageId);
        }
        finally
        {
            try { Directory.Delete(sandbox, recursive: true); } catch { }
        }
    }
}