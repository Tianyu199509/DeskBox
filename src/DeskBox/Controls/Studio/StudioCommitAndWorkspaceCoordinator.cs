using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.FileSafety;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Services.Studio;
using DeskBox.ViewModels;
using DeskBox.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using ToolkitControls = CommunityToolkit.WinUI.Controls;

namespace DeskBox.Controls.Studio;

internal sealed class StudioCommitAndWorkspaceCoordinator : IDisposable
{
    private readonly CustomWidgetStudioWindow _shell;
    private LocalizationService _loc => _shell.Localization;
    private IStudioHostBridge _hostBridge => _shell.HostBridge;
    private CustomWidgetStudioPreferencesStore _prefs => _shell.Preferences;
    private CustomWidgetStudioSessionStore _sessionStore => _shell.SessionStore;
    private StudioSessionController _session => _shell.Session;

    internal StudioCommitAndWorkspaceCoordinator(CustomWidgetStudioWindow shell)
    {
        _shell = shell;
    }
    private readonly object _codeToolWatcherLock = new();
    private string? _lastSelfCopiedText;
    private FileSystemWatcher? _codeToolWatcher;
    private CancellationTokenSource? _codeToolDebounceCts;

    internal string? LastSelfCopiedText { get => _lastSelfCopiedText; set => _lastSelfCopiedText = value; }

    internal void OnStudioRootDragOver(object sender, DragEventArgs e)
    {
        if (e.Handled || _shell.IsDialogOpen || _shell.IsClosed || _session.IsAnyStudioJobRunning)
        {
            return;
        }

        if (DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            DataPackageOperation negotiated = DeskBoxDragData.GetFileAssociationOperation(
                e.DataView,
                e.AllowedOperations);
            if (negotiated != DataPackageOperation.None)
            {
                e.AcceptedOperation = negotiated;
                if (e.DragUIOverride is not null)
                {
                    e.DragUIOverride.IsCaptionVisible = true;
                    e.DragUIOverride.Caption = _loc.T("CustomWidget.Workshop.ImportPackageButton");
                }
            }
        }
    }

    internal async void OnStudioRootDrop(object sender, DragEventArgs e)
    {
        if (e.Handled || _shell.IsDialogOpen || _shell.IsClosed || _session.IsAnyStudioJobRunning || !DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            return;
        }

        DataPackageOperation allowedOperations = e.AllowedOperations;
        DragOperationDeferral deferral = e.GetDeferral();
        try
        {
            using DroppedFileBatch batch = await DeskBoxDragData.TryGetDroppedFilesAsync(e.DataView);
            foreach (DroppedFilePath file in batch.Files)
            {
                if (!string.IsNullOrWhiteSpace(file.Path))
                {
                    string ext = Path.GetExtension(file.Path);
                    if (CustomWidgetPackageService.SupportedImportExtensions.Any(s =>
                            string.Equals(s, ext, StringComparison.OrdinalIgnoreCase)))
                    {
                        e.Handled = true;
                        e.AcceptedOperation = DeskBoxDragData.GetFileAssociationOperation(
                            e.DataView,
                            allowedOperations);
                        await ImportPackageFilePathInStudioAsync(file.Path);
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _hostBridge.Log($"[CustomWidgetStudio] Drag-drop package import failed: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    internal async Task ImportPackageFromDiskInStudioAsync()
    {
        if (_shell.IsDialogOpen || _shell.IsClosed || _shell.WindowHandle == IntPtr.Zero)
        {
            return;
        }

        if (_session.Page != StudioPage.Start && _session.HasUnsavedWork && !await _shell.ConfirmDiscardCleanlyAsync())
        {
            return;
        }

        try
        {
            string? pickedPath = await FileOpenPickerService.PickSingleFileAsync(
                _shell.WindowHandle,
                CustomWidgetPackageService.SupportedImportExtensions.ToArray(),
                Microsoft.Windows.Storage.Pickers.PickerLocationId.Desktop);
            if (string.IsNullOrWhiteSpace(pickedPath))
            {
                return;
            }

            await ImportPackageFilePathInStudioAsync(pickedPath, alreadyConfirmedDiscard: true);
        }
        catch (CustomWidgetIncompatibleVersionException verEx)
        {
            _shell.ShowPageMessage(InfoBarSeverity.Error, string.Empty, verEx.Message, null);
        }
        catch (Exception ex)
        {
            _hostBridge.Log($"[CustomWidgetStudio] Import package failed: {ex.Message}");
            _shell.ShowPageMessage(
                InfoBarSeverity.Error,
                string.Empty,
                _loc.Format("CustomWidget.Studio.Status.ImportFailed", ex.Message),
                null);
        }
    }

    private async Task ImportPackageFilePathInStudioAsync(string packagePath, bool alreadyConfirmedDiscard = false)
    {
        if (!alreadyConfirmedDiscard && _session.Page != StudioPage.Start && _session.HasUnsavedWork && !await _shell.ConfirmDiscardCleanlyAsync())
        {
            return;
        }

        try
        {
            CustomWidgetImportedBundle imported = await CustomWidgetPackageService.ReadPackageBundleAsync(packagePath);
            string displayLabel = Path.GetFileNameWithoutExtension(packagePath);
            _shell.OpenExternalManifestPreviewCore(
                imported.RawJson,
                displayLabel,
                imported.LogicJs,
                imported.Locales,
                CustomWidgetOriginKinds.Community);
        }
        catch (CustomWidgetIncompatibleVersionException verEx)
        {
            _shell.ShowPageMessage(InfoBarSeverity.Error, string.Empty, verEx.Message, null);
        }
        catch (Exception ex)
        {
            _hostBridge.Log($"[CustomWidgetStudio] Import package failed: {ex.Message}");
            _shell.ShowPageMessage(
                InfoBarSeverity.Error,
                string.Empty,
                _loc.Format("CustomWidget.Studio.Status.ImportFailed", ex.Message),
                null);
        }
    }

    internal async Task ExportWidgetBundleInteractiveAsync(
        string? manifestJson,
        string? logicJs,
        CustomWidgetLocaleCatalog? locales,
        string? fallbackTitle = null)
    {
        if (_shell.IsDialogOpen || _shell.IsClosed || _shell.WindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(manifestJson))
        {
            return;
        }

        try
        {
            string? targetFolder = await FolderPickerService.PickFolderAsync(_shell.WindowHandle);
            if (string.IsNullOrWhiteSpace(targetFolder))
            {
                return;
            }

            CustomWidgetManifest? parsed = CustomWidgetManifestParser.Parse(manifestJson!).Manifest;
            if (parsed is not null && locales is { HasAnyLocale: true })
            {
                parsed = parsed.WithLocales(locales);
            }

            string rawName = !string.IsNullOrWhiteSpace(fallbackTitle)
                ? fallbackTitle!
                : (parsed?.ResolveLocalizedName(_session.EffectivePreviewCultureName) ?? parsed?.Id ?? "widget");
            string safeBaseName = FileService.SanitizeFileSystemName(rawName);
            if (string.IsNullOrWhiteSpace(safeBaseName))
            {
                safeBaseName = parsed?.Id ?? "widget";
            }

            string exportPath = FileService.GetAvailablePath(
                Path.Combine(targetFolder, $"{safeBaseName}{CustomWidgetPackageService.SharePackageExtension}"));
            CustomWidgetExportReport report = await CustomWidgetPackageService.ExportSharePackageFromBundleAsync(
                manifestJson!,
                logicJs,
                locales,
                exportPath);

            string successMsg = _loc.Format("CustomWidget.Workshop.ExportSuccessFormat", Path.GetFileName(exportPath));
            if (report.SanitizedPathsCount > 0 || report.StrippedSecretsCount > 0)
            {
                successMsg = $"{successMsg} {_loc.T("CustomWidget.Package.SanitizedNotice")}";
            }

            _shell.ShowPageMessage(InfoBarSeverity.Success, string.Empty, successMsg, null);
            DeskBox.Services.ShellLaunchService.FireAndForgetRevealInExplorer(exportPath);
        }
        catch (Exception ex)
        {
            _hostBridge.Log($"[CustomWidgetStudio] Export package failed: {ex.Message}");
            _shell.ShowPageMessage(InfoBarSeverity.Error, string.Empty, ex.Message, null);
        }
    }

    private const string DraftSkeletonManifestJson = """
        {
          "schemaVersion": 1,
          "id": "custom.draft",
          "name": "Draft Widget",
          "version": "1.0.0",
          "author": "DeskBox",
          "description": "External Agent draft workspace",
          "icon": "sparkles",
          "origin": "agent",
          "minSize": { "width": 260, "height": 200 },
          "defaultSize": { "width": 320, "height": 260 },
          "state": {},
          "collections": {},
          "root": "main",
          "elements": {
            "main": {
              "type": "Stack",
              "spacing": 8,
              "children": ["ready_text"]
            },
            "ready_text": {
              "type": "Text",
              "text": "Ready for external Agent preview.",
              "style": "bodyStrong"
            }
          }
        }
        """;

    internal async Task CopyAgentKickoffPromptAsync(string? idea)
    {
        string trimmed = idea?.Trim() ?? string.Empty;
        if (trimmed.Length > 0)
        {
            _prefs.SetPreferredStudioChannel(CustomWidgetStudioPreferencesStore.ChannelAgentMcp);
            await _shell.Generation.BeginGenerationAsync(trimmed);
            return;
        }

        try
        {
            string prompt = CustomWidgetAgentConnectorService.BuildStudioAgentMcpPrompt(
                userIdea: string.Empty,
                uiLanguage: _loc.CurrentCultureName,
                targetWidgetId: _session.TargetWidgetId,
                targetWidgetName: _session.TargetWidgetName,
                currentManifestJson: _session.CurrentManifestJson,
                briefMarkdown: _session.BriefMarkdown,
                targetNodeContext: StudioManifestInspector.FormatTargetNodePromptContext(_session.CurrentManifestJson, _session.SelectedTargetNode?.NodePath),
                pinnedCapabilityIds: _session.ResolveEffectivePinnedCapabilitiesForPrompt(null),
                isCapsuleViewport: _session.IsCapsulePreviewMode,
                currentLogicJs: _session.CurrentLogicJs,
                currentLocales: _session.CurrentLocales);
            _shell.SetClipboardText(prompt);
            _shell.ShowPageMessage(
                InfoBarSeverity.Success,
                string.Empty,
                _loc.T("CustomWidget.Workshop.AgentTaskCopied"),
                null);
        }
        catch (Exception ex)
        {
            _shell.ShowFailureMessage(
                "CustomWidget.Workshop.Failure.ClipboardTitle",
                "CustomWidget.Workshop.Failure.ClipboardBody",
                ex);
        }
    }

    internal Task StartAgentMcpFlowAsync(string prompt, string? contextJson)
    {
        _shell.Generation.CancelGeneration();
        _shell.Generation.StopWaitingElapsedTimer();
        DisposeCodeToolWatcher();

        string fullPrompt = CustomWidgetAgentConnectorService.BuildStudioAgentMcpPrompt(
            userIdea: prompt,
            uiLanguage: _loc.CurrentCultureName,
            targetWidgetId: _session.TargetWidgetId,
            targetWidgetName: _session.TargetWidgetName,
            currentManifestJson: contextJson,
            manifestFilePath: null,
            briefMarkdown: _session.BriefMarkdown,
            targetNodeContext: StudioManifestInspector.FormatTargetNodePromptContext(contextJson, _session.SelectedTargetNode?.NodePath),
            pinnedCapabilityIds: _session.ResolveEffectivePinnedCapabilitiesForPrompt(prompt),
            isCapsuleViewport: _session.IsCapsulePreviewMode,
            currentLogicJs: _session.CurrentLogicJs,
            currentLocales: _session.CurrentLocales);

        _shell.SetClipboardText(fullPrompt);

        _shell.ChatPanel.ClearMcpActivities();
        _session.WaitingKind = WaitingKind.AgentMcp;
        _session.IsInPlaceGenerating = true;
        _shell.ChatPanel.ChatStickToBottom = true;

        if (_session.Page != StudioPage.Preview)
        {
            _shell.StageViewport.ShowPreviewPage(justLoaded: false);
        }
        else
        {
            if (_shell.ChatPanel.RefineTextBox is not null)
            {
                _shell.ChatPanel.RefineTextBox.Text = string.Empty;
                _session.RefineDraftIdea = string.Empty;
            }
            if (_shell.PageInfoBar is not null)
            {
                _shell.PageInfoBar.IsOpen = false;
                _shell.PageInfoBar.Visibility = Visibility.Collapsed;
            }
            _shell.ChatPanel.RefreshInPlaceGenerationChrome();
        }

        return Task.CompletedTask;
    }

    internal void OnExternalAgentWidgetUpserted(
        string widgetId,
        string widgetName,
        string manifestJson,
        string? logicJs,
        bool hasLogicJs,
        CustomWidgetLocaleCatalog? locales,
        bool hasLocales)
    {
        if (_shell.IsClosed || string.IsNullOrWhiteSpace(manifestJson))
        {
            return;
        }

        // P2：工坊自己点击「放到桌面」调用 stage_commit 时，Bridge 仍会广播一次「外部已落盘」。
        // 这条通知属于同一次提交，由 CommitPendingAgentStageAsync 自行完成会话落盘，不重复灌回修订历史。
        if (_session.IsCommittingPendingAgentStage)
        {
            return;
        }

        // 若当前预览舞台正打开着这份待确认暂存，而用户在外部 Agent 对话中回复确认触发了 commit=true：
        // 此时无需再叠加一条重复修订，直接把当前修订标记为已提交并持久化共创会话。
        if (!string.IsNullOrWhiteSpace(_session.PendingAgentStageId))
        {
            _session.PendingAgentStageId = null;
            _session.PendingAgentStageManifest = null;

            if (_session.Revisions.Count > 0 &&
                string.Equals(
                    CustomWidgetAgentStageStore.ComputeManifestHash(_session.CurrentManifestJson ?? string.Empty),
                    CustomWidgetAgentStageStore.ComputeManifestHash(manifestJson),
                    StringComparison.Ordinal))
            {
                _shell.Generation.StopWaitingElapsedTimer();

                bool wasCreate = string.IsNullOrWhiteSpace(_session.TargetWidgetId);
                _session.TargetWidgetId = widgetId;
                if (!string.IsNullOrWhiteSpace(widgetName))
                {
                    _session.TargetWidgetName = widgetName;
                }

                int lastIdx = _session.Revisions.Count - 1;
                _session.Revisions[lastIdx] = _session.Revisions[lastIdx] with { IsCommitted = true };
                if (wasCreate)
                {
                    _sessionStore.PromoteCreateSessionToWidget(
                        widgetId,
                        _session.BriefMarkdown,
                        _session.Revisions,
                        _session.ChatMessages);
                }
                else
                {
                    _sessionStore.SaveCommittedWidgetSession(
                        widgetId,
                        _session.BriefMarkdown,
                        _session.Revisions,
                        _session.ChatMessages);
                }

                _session.UncommittedEditCount = 0;
                _session.Committed = true;
                DisposeCodeToolWatcher();
                _ = FinalizeExternalAgentCommitAndCloseAsync(widgetId, manifestJson, wasEdit: !wasCreate);
                return;
            }
        }

        bool isWaitingMcp = _session.IsInPlaceGenerating && _session.WaitingKind == WaitingKind.AgentMcp;
        bool matchesCurrentTarget = !string.IsNullOrWhiteSpace(_session.TargetWidgetId) &&
            string.Equals(_session.TargetWidgetId, widgetId, StringComparison.OrdinalIgnoreCase);
        bool isAgentChannelCreateDraft = string.IsNullOrWhiteSpace(_session.TargetWidgetId) &&
            string.Equals(_prefs.PreferredStudioChannel, CustomWidgetStudioPreferencesStore.ChannelAgentMcp, StringComparison.OrdinalIgnoreCase);

        if (!isWaitingMcp && !matchesCurrentTarget && !isAgentChannelCreateDraft)
        {
            return;
        }

        _shell.Generation.StopWaitingElapsedTimer();

        bool wasCreateMode = string.IsNullOrWhiteSpace(_session.TargetWidgetId);
        _session.TargetWidgetId = widgetId;
        if (!string.IsNullOrWhiteSpace(widgetName))
        {
            _session.TargetWidgetName = widgetName;
        }

        string promptForHistory = _session.PendingPrompt;
        bool applied = _shell.Generation.HandleManifestCandidate(
            manifestJson,
            promptForHistory,
            _loc.T("CustomWidget.Workshop.History.AgentMcp"),
            source: "ai",
            fencedLogicJs: hasLogicJs ? logicJs : null,
            externalLocales: hasLocales ? locales : null,
            hasExplicitExternalFiles: hasLogicJs && hasLocales,
            hasExplicitLogicJsOverride: hasLogicJs,
            hasExplicitLocalesOverride: hasLocales);

        if (!applied)
        {
            return;
        }

        if (_session.Revisions.Count > 0)
        {
            int lastIdx = _session.Revisions.Count - 1;
            _session.Revisions[lastIdx] = _session.Revisions[lastIdx] with { IsCommitted = true };
        }

        if (wasCreateMode)
        {
            _sessionStore.PromoteCreateSessionToWidget(
                widgetId,
                _session.BriefMarkdown,
                _session.Revisions,
                _session.ChatMessages);
        }
        else
        {
            _sessionStore.SaveCommittedWidgetSession(
                widgetId,
                _session.BriefMarkdown,
                _session.Revisions,
                _session.ChatMessages);
        }

        _session.UncommittedEditCount = 0;
        _session.Committed = true;
        DisposeCodeToolWatcher();
        _ = FinalizeExternalAgentCommitAndCloseAsync(widgetId, manifestJson, wasEdit: !wasCreateMode);
    }

    private async Task FinalizeExternalAgentCommitAndCloseAsync(string widgetId, string committedManifestJson, bool wasEdit)
    {
        try
        {
            await _session.PersistPreviewSettingOverridesToWidgetAsync(widgetId, committedManifestJson, _shell);
            if (_prefs.LocateOnDesktopAfterCommit && _hostBridge.HasWidgetManager)
            {
                await _hostBridge.LocateCustomWidgetAsync(widgetId);
            }
        }
        catch (Exception ex)
        {
            _hostBridge.Log($"[CustomWidgetStudio] Finalize external Agent commit failed: {ex.Message}");
        }

        if (_shell.IsClosed)
        {
            return;
        }

        _hostBridge.ShowNotification(
            _loc.T(wasEdit ? "CustomWidget.Workshop.Saved.Title" : "CustomWidget.Workshop.Placed.Title"),
            _loc.T("CustomWidget.Workshop.Placed.Body"));

        if (_session.Page == StudioPage.Preview)
        {
            _session.Committed = false;
            if (!string.IsNullOrWhiteSpace(widgetId))
            {
                _shell.BeginHydrateEditBaselineStorePayload(widgetId);
            }
            _shell.UpdateWindowTitle();
            _shell.StageViewport.SyncHeaderCommitActionsForCurrentMode();
            _shell.ShowPageMessage(
                InfoBarSeverity.Success,
                string.Empty,
                _loc.T(wasEdit ? "CustomWidget.Workshop.Saved.Title" : "CustomWidget.Workshop.Placed.Title"),
                null);
            _shell.ChatPanel.RefreshInPlaceGenerationChrome(refreshStream: false);
            _shell.ChatPanel.RefreshRevisionChrome();
            return;
        }

        _shell.AllowClose = true;
        _shell.Close();
    }

    internal async Task OpenInCodeToolAsync(string? idea)
    {
        bool useLiveWidgetWorkspace = _session.IsEditMode &&
                                      !string.IsNullOrWhiteSpace(_session.TargetWidgetId) &&
                                      !_session.HasUnsavedWork;
        string workspace = useLiveWidgetWorkspace
            ? CustomWidgetDataStore.ForWidget(_session.TargetWidgetId!).WidgetDirectory
            : _sessionStore.GetCreateDraftDirectory();
        string seedJson = !string.IsNullOrWhiteSpace(_session.CurrentManifestJson)
            ? _session.CurrentManifestJson!
            : DraftSkeletonManifestJson;

        try
        {
            Directory.CreateDirectory(workspace);
            string manifestPath = Path.Combine(workspace, "manifest.json");
            if (!useLiveWidgetWorkspace)
            {
                _session.TouchDraftWriteTick();
                CustomWidgetStorageFileOps.ReplaceTextFileAtomically(manifestPath, seedJson);

                string logicPath = Path.Combine(workspace, CustomWidgetDataStore.LogicScriptFileName);
                if (!string.IsNullOrWhiteSpace(_session.CurrentLogicJs))
                {
                    CustomWidgetStorageFileOps.ReplaceTextFileAtomically(logicPath, _session.CurrentLogicJs!);
                }
                else
                {
                    CustomWidgetStorageFileOps.DeleteFileIfExists(logicPath);
                }

                string localesDir = Path.Combine(workspace, CustomWidgetDataStore.LocalesDirectoryName);
                if (_session.CurrentLocales is { HasAnyLocale: true } draftLocales)
                {
                    draftLocales.WriteToDirectoryAtomically(localesDir);
                }
                else if (Directory.Exists(localesDir))
                {
                    CustomWidgetStorageFileOps.DeleteDirectoryRecursive(localesDir);
                }
                _session.TouchDraftWriteTick();
            }

            await SyncWorkspaceBriefAsync(workspace);
            await CustomWidgetTemplates.EnsureAgentWorkspacePreparedAsync(
                workspace,
                _session.IsEditMode ? _session.TargetWidgetId : null,
                idea);
            _shell.SetClipboardText(CustomWidgetTemplates.BuildLocalAgentPrompt(
                idea ?? string.Empty,
                Path.Combine(workspace, "SKILL.md"),
                manifestPath,
                workspace,
                _session.SelectedEmoji,
                _loc.CurrentCultureName,
                _session.ResolveEffectivePinnedCapabilitiesForPrompt(idea)));
            CustomWidgetAgentConnectorService.TryOpenWorkspaceInEditor(workspace, _prefs.PreferredCodeEditor);
            StartCodeToolWatcher(workspace);
            _shell.ShowPageMessage(InfoBarSeverity.Informational, string.Empty, _loc.T("CustomWidget.Workshop.CodeTool.Hint"), null);
        }
        catch (Exception ex)
        {
            _shell.ShowFailureMessage(
                "CustomWidget.Workshop.Failure.OpenToolTitle",
                "CustomWidget.Workshop.Failure.OpenToolBody",
                ex);
        }
    }

    private async Task SyncWorkspaceBriefAsync(string workspaceDirectory)
    {
        if (string.IsNullOrWhiteSpace(_session.BriefMarkdown))
        {
            return;
        }

        string briefPath = Path.Combine(workspaceDirectory, CustomWidgetStudioSessionStore.BriefFileName);
        await File.WriteAllTextAsync(briefPath, _session.BriefMarkdown.Trim() + Environment.NewLine, Encoding.UTF8);
    }

    private void StartCodeToolWatcher(string workspace)
    {
        DisposeCodeToolWatcher();
        try
        {
            var watcher = new FileSystemWatcher(workspace)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
            };
            static bool IsWatchedWorkspaceFile(string? name)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    return false;
                }

                if (string.Equals(name, "manifest.json", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, CustomWidgetDataStore.LogicScriptFileName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, CustomWidgetStudioSessionStore.BriefFileName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                string normalized = name.Replace('\\', '/');
                return normalized.StartsWith("locales/", StringComparison.OrdinalIgnoreCase) &&
                       normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                       !normalized.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
            }

            watcher.Changed += (_, e) =>
            {
                if (IsWatchedWorkspaceFile(e.Name)) ScheduleCodeToolReload(workspace);
            };
            watcher.Created += (_, e) =>
            {
                if (IsWatchedWorkspaceFile(e.Name)) ScheduleCodeToolReload(workspace);
            };
            watcher.Deleted += (_, e) =>
            {
                if (IsWatchedWorkspaceFile(e.Name)) ScheduleCodeToolReload(workspace);
            };
            watcher.Renamed += (_, e) =>
            {
                if (IsWatchedWorkspaceFile(e.Name) || IsWatchedWorkspaceFile(e.OldName))
                {
                    ScheduleCodeToolReload(workspace);
                }
            };
            watcher.EnableRaisingEvents = true;
            _codeToolWatcher = watcher;
        }
        catch (Exception ex)
        {
            _hostBridge.Log($"[CustomWidgetStudio] Code tool watcher failed: {ex.Message}");
        }
    }

    private void ScheduleCodeToolReload(string workspace)
    {
        long elapsed = _session.ElapsedSinceLastDraftWriteMs();
        if (elapsed is >= 0 and < 450)
        {
            return;
        }

        CancellationTokenSource cts;
        lock (_codeToolWatcherLock)
        {
            _codeToolDebounceCts?.Cancel();
            _codeToolDebounceCts?.Dispose();
            cts = new CancellationTokenSource();
            _codeToolDebounceCts = cts;
        }

        CancellationToken token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(220, token);
                string manifestPath = Path.Combine(workspace, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    return;
                }

                string text = await File.ReadAllTextAsync(manifestPath, Encoding.UTF8, token);
                string logicPath = Path.Combine(workspace, CustomWidgetDataStore.LogicScriptFileName);
                string? diskLogicJs = File.Exists(logicPath)
                    ? await File.ReadAllTextAsync(logicPath, Encoding.UTF8, token)
                    : null;
                if (string.IsNullOrWhiteSpace(diskLogicJs))
                {
                    diskLogicJs = null;
                }

                string localesDir = Path.Combine(workspace, CustomWidgetDataStore.LocalesDirectoryName);
                CustomWidgetLocaleCatalog loadedCatalog = CustomWidgetLocaleCatalog.LoadFromDirectory(localesDir);
                CustomWidgetLocaleCatalog? diskLocales = loadedCatalog.HasAnyLocale ? loadedCatalog : null;

                string briefPath = Path.Combine(workspace, CustomWidgetStudioSessionStore.BriefFileName);
                string? diskBrief = File.Exists(briefPath)
                    ? await File.ReadAllTextAsync(briefPath, Encoding.UTF8, token)
                    : null;
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                _shell.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_shell.IsClosed && !token.IsCancellationRequested)
                    {
                        if (!string.IsNullOrWhiteSpace(diskBrief))
                        {
                            _session.BriefMarkdown = diskBrief!.Trim();
                        }

                        bool bundleChanged =
                            !string.Equals(text, _session.CurrentManifestJson, StringComparison.Ordinal) ||
                            !string.Equals(diskLogicJs ?? string.Empty, _session.CurrentLogicJs ?? string.Empty, StringComparison.Ordinal) ||
                            !string.Equals(
                                diskLocales?.ComputeSha256() ?? string.Empty,
                                _session.CurrentLocales?.ComputeSha256() ?? string.Empty,
                                StringComparison.Ordinal);
                        if (bundleChanged)
                        {
                            if (_session.Page != StudioPage.Waiting)
                            {
                                _session.PageBeforeWaiting = _session.Page;
                            }

                            _shell.Generation.HandleManifestCandidate(
                                text,
                                null,
                                _loc.T("CustomWidget.Workshop.History.CodeTool"),
                                source: "external",
                                fencedLogicJs: diskLogicJs,
                                externalLocales: diskLocales,
                                hasExplicitExternalFiles: true);
                        }
                    }
                });
            }
            catch
            {
            }
        }, token);
    }

    internal void DisposeCodeToolWatcher()
    {
        lock (_codeToolWatcherLock)
        {
            try
            {
                _codeToolDebounceCts?.Cancel();
                _codeToolDebounceCts?.Dispose();
            }
            catch
            {
            }

            _codeToolDebounceCts = null;
        }

        if (_codeToolWatcher is not null)
        {
            try
            {
                _codeToolWatcher.EnableRaisingEvents = false;
                _codeToolWatcher.Dispose();
            }
            catch
            {
            }

            _codeToolWatcher = null;
        }
    }

    internal static bool ShouldRevealOnEditCommit(bool wasParkedOrHidden, bool locateOnDesktopAfterCommit) =>
        wasParkedOrHidden || locateOnDesktopAfterCommit;

    internal async Task CommitAsync(Button commitButton, bool closeAfterCommit = false)
    {
        string? json = _session.CurrentManifestJson;
        if (string.IsNullOrWhiteSpace(json) || !_hostBridge.HasWidgetManager)
        {
            return;
        }

        // P2：外部 Agent 的待确认暂存必须经 Bridge 的 stage_commit 落盘，
        // 这样校验、stageId 一次性消费与审计都留在同一条写入路径上。
        string? stagedId = _session.PendingAgentStageId;
        string? abandonedStageId = null;
        if (!string.IsNullOrWhiteSpace(stagedId))
        {
            if (string.Equals(json, _session.PendingAgentStageManifest, StringComparison.Ordinal))
            {
                await CommitPendingAgentStageAsync(commitButton, stagedId);
                return;
            }

            // 用户在预览里又改了内容 = 已经转成一次本地创作。此时改走常规提交路径，
            // 丢弃旧暂存目录并在落盘后登记 committed 幂等记录，防止用户随后在 Agent 聊天里
            // 再次回复「确认提交」时报错或用未微调版本覆盖桌面。
            abandonedStageId = stagedId;
            try
            {
                await _hostBridge.DiscardAgentStageAsync(stagedId);
            }
            catch (Exception ex)
            {
                _hostBridge.Log("[AgentStage] discard after local edit failed: " + ex.Message);
            }

            _session.PendingAgentStageId = null;
            _session.PendingAgentStageManifest = null;
        }

        commitButton.IsEnabled = false;
        if (_shell.ChatPanel.CommitButton is not null)
        {
            _shell.ChatPanel.CommitButton.IsEnabled = false;
        }
        if (_shell.ChatPanel.ApplyInPlaceButton is not null)
        {
            _shell.ChatPanel.ApplyInPlaceButton.IsEnabled = false;
        }
        try
        {
            bool wasEdit = _session.IsEditMode;
            if (!wasEdit && _session.IsExternalImportSession && !_session.HasPromptedFirstRunSetupInSession)
            {
                CustomWidgetManifest? preCommitManifest = CustomWidgetManifestParser.Parse(json).Manifest;
                if (preCommitManifest is not null)
                {
                    if (_session.CurrentLocales is { HasAnyLocale: true })
                    {
                        preCommitManifest = preCommitManifest.WithLocales(_session.CurrentLocales);
                    }

                    CustomWidgetSetupInspection setupCheck = CustomWidgetOnboardingHelper.InspectFirstRunSetupRequirements(
                        preCommitManifest,
                        _session.EffectivePreviewCultureName,
                        _session.SessionSettingOverrides,
                        _session.SessionSecretOverrides);
                    if (setupCheck.RequiresAttention)
                    {
                        await _shell.PromptAndApplyFirstRunSetupInStudioAsync();
                    }
                }
            }

            if (wasEdit && !string.IsNullOrWhiteSpace(_session.TargetWidgetId))
            {
                bool proceed = await VerifyOrMigrateStoreDataBeforeCommitAsync(_session.TargetWidgetId!, json);
                if (!proceed)
                {
                    commitButton.IsEnabled = true;
                    _shell.ChatPanel.RefreshInPlaceGenerationChrome(refreshStream: false);
                    return;
                }
            }

            CustomWidgetViewModel? previewVm = !wasEdit ? _shell.StageViewport.PreviewVm : null;
            CustomWidgetStorePayload? userSeedPayload = previewVm?.BuildUserEnteredStoreSeedPayload();
            string? sampleDataJsonForCreate = !wasEdit && !_session.IsSampleDataMutedInPreview
                ? _session.PreviewSampleDataJson
                : null;
            Dictionary<string, List<CustomWidgetRecord>>? previewSampleCollectionsSnapshot = null;
            if (!wasEdit &&
                !_session.IsSampleDataMutedInPreview &&
                previewVm is not null &&
                (previewVm.HasPreviewSampleData || !string.IsNullOrWhiteSpace(sampleDataJsonForCreate)) &&
                previewVm.Collections.Count > 0)
            {
                previewSampleCollectionsSnapshot = new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal);
                foreach ((string colKey, List<CustomWidgetRecord> colRecs) in previewVm.Collections)
                {
                    if (colRecs.Count > 0)
                    {
                        previewSampleCollectionsSnapshot[colKey] = new List<CustomWidgetRecord>(colRecs);
                    }
                }
            }
            string? logicJsToCommit = _session.CurrentLogicJs;
            CustomWidgetLocaleCatalog? localesToCommit = _session.CurrentLocales;
            string jsonToCommit = CustomWidgetTemplates.SyncManifestScriptPropertyJson(json, logicJsToCommit);
            jsonToCommit = _session.StampManifestProvenanceForCommit(jsonToCommit);

            CustomWidgetManifest? prevManifestForMigration = null;
            CustomWidgetManifest? nextManifestForMigration = CustomWidgetManifestParser.Parse(jsonToCommit).Manifest;
            if (wasEdit && !string.IsNullOrWhiteSpace(_session.TargetWidgetId))
            {
                CustomWidgetDataStore targetStore = CustomWidgetDataStore.ForWidget(_session.TargetWidgetId!);
                string? prevDiskManifestJson = StudioManifestInspector.TryReadWidgetManifest(_session.TargetWidgetId!, _hostBridge.Log);
                prevManifestForMigration = !string.IsNullOrWhiteSpace(prevDiskManifestJson)
                    ? CustomWidgetManifestParser.Parse(prevDiskManifestJson!).Manifest
                    : null;
                if (nextManifestForMigration is not null)
                {
                    await CustomWidgetHealthAnalyzer.MigratePromotedStateKeysToSettingsAsync(
                        targetStore,
                        prevManifestForMigration,
                        nextManifestForMigration);
                }
            }

            (Dictionary<string, string>? initialSettings, Dictionary<string, string>? initialSecrets) =
                _session.ExtractValidPreviewSettingOverrides(jsonToCommit);

            GlanceWidgetInstanceInfo info;
            bool wasParkedOrHidden = wasEdit && !_shell.StageViewport.IsTargetWidgetVisibleOnDesktop();
            if (wasEdit && !string.IsNullOrWhiteSpace(_session.TargetWidgetId))
            {
                bool sizeChanged = prevManifestForMigration is not null &&
                    nextManifestForMigration is not null &&
                    (prevManifestForMigration.DefaultSize.Width != nextManifestForMigration.DefaultSize.Width ||
                     prevManifestForMigration.DefaultSize.Height != nextManifestForMigration.DefaultSize.Height);
                bool shouldReveal = ShouldRevealOnEditCommit(wasParkedOrHidden, _prefs.LocateOnDesktopAfterCommit);
                info = (await _hostBridge.UpdateCustomWidgetManifestAsync(
                    _session.TargetWidgetId!,
                    jsonToCommit,
                    resizeToDefault: sizeChanged,
                    reveal: shouldReveal,
                    logicJs: logicJsToCommit,
                    updateLogicJs: true,
                    locales: localesToCommit,
                    updateLocales: true))!;
                if (initialSettings is not null || initialSecrets is not null || _session.HasDirtyChromeModeOverride)
                {
                    await _hostBridge.ApplyCustomWidgetMutationAsync(
                        new DeskBox.Contracts.CustomWidgetSettingsMutation(
                            _session.TargetWidgetId!,
                            Settings: initialSettings,
                            Secrets: initialSecrets,
                            ChromeModeOverride: _session.HasDirtyChromeModeOverride
                                ? WidgetChromeModeNames.ToSettingValue(_session.StageChromeMode)
                                : null,
                            UpdateChromeMode: _session.HasDirtyChromeModeOverride));
                }
            }
            else
            {
                info = (await _hostBridge.CreateCustomWidgetAsync(
                    $"json:{jsonToCommit}",
                    logicJsOverride: logicJsToCommit,
                    hasLogicJsOverride: true,
                    localesOverride: localesToCommit,
                    hasLocalesOverride: true,
                    initialSettings: initialSettings,
                    initialSecrets: initialSecrets))!;
                if (_session.StageChromeMode != WidgetChromeMode.System && !string.IsNullOrWhiteSpace(info.Id))
                {
                    await _hostBridge.ApplyCustomWidgetMutationAsync(
                        new DeskBox.Contracts.CustomWidgetSettingsMutation(
                            info.Id,
                            ChromeModeOverride: WidgetChromeModeNames.ToSettingValue(_session.StageChromeMode),
                            UpdateChromeMode: true));
                }
            }

            if (!wasEdit &&
                !string.IsNullOrWhiteSpace(info.Id) &&
                (userSeedPayload is not null ||
                 previewSampleCollectionsSnapshot is { Count: > 0 } ||
                 !string.IsNullOrWhiteSpace(sampleDataJsonForCreate)))
            {
                try
                {
                    await SeedNewlyCreatedWidgetStoreAsync(
                        info.Id,
                        nextManifestForMigration,
                        userSeedPayload,
                        previewSampleCollectionsSnapshot,
                        sampleDataJsonForCreate);
                }
                catch (Exception seedEx)
                {
                    _hostBridge.Log($"[CustomWidgetStudio] Carry user preview seed data on create failed: {seedEx.Message}");
                }
            }

            if (!string.IsNullOrWhiteSpace(info.Id))
            {
                if (!string.IsNullOrWhiteSpace(abandonedStageId))
                {
                    CustomWidgetAgentStageStore.RecordCommitted(abandonedStageId, info.Id);
                }

                IReadOnlyList<StudioPersistedRevision> committedRevisions =
                    CustomWidgetStudioSessionStore.MarkCommittedRevision(
                        _session.Revisions.Take(_session.RevisionIndex + 1).ToList(),
                        jsonToCommit,
                        source: _session.AiRevisionCount > 0 ? "ai" : "manual",
                        committedLogicJs: logicJsToCommit,
                        committedLocales: localesToCommit);
                if (wasEdit)
                {
                    _sessionStore.SaveCommittedWidgetSession(info.Id, _session.BriefMarkdown, committedRevisions, _session.ChatMessages);
                }
                else
                {
                    _sessionStore.PromoteCreateSessionToWidget(info.Id, _session.BriefMarkdown, committedRevisions, _session.ChatMessages);
                }

                if (!closeAfterCommit && _session.RevisionIndex >= 0 && _session.RevisionIndex < _session.Revisions.Count && committedRevisions.Count > 0)
                {
                    _session.Revisions[_session.RevisionIndex] = _session.Revisions[_session.RevisionIndex] with
                    {
                        IsCommitted = true,
                        ManifestJson = jsonToCommit,
                        LogicJs = logicJsToCommit,
                        Locales = localesToCommit
                    };
                }

                if (!wasEdit && !closeAfterCommit)
                {
                    _session.TargetWidgetId = info.Id;
                    _session.TargetWidgetName = info.Name;
                    _session.IsExternalImportSession = false;
                    _shell.UpdateWindowTitle();
                    _shell.StageViewport.SyncHeaderCommitActionsForCurrentMode();
                }

                _shell.RecordStudioCommitDiagnostic(
                    commitSource: _session.AiRevisionCount > 0 ? "ai" : "manual",
                    widgetId: info.Id,
                    wasEdit: wasEdit,
                    manifestJson: jsonToCommit,
                    logicJs: logicJsToCommit);
            }

            if ((wasParkedOrHidden || _prefs.LocateOnDesktopAfterCommit) && !string.IsNullOrWhiteSpace(info.Id))
            {
                await _hostBridge.LocateCustomWidgetAsync(info.Id);
            }

            string commitSuccessTitleKey = (!wasEdit || wasParkedOrHidden)
                ? "CustomWidget.Workshop.Placed.Title"
                : "CustomWidget.Workshop.Saved.Title";

            _session.UncommittedEditCount = 0;
            _session.HasDirtySettingOverrides = false;
            _session.HasDirtyChromeModeOverride = false;
            if (!closeAfterCommit)
            {
                _session.Committed = false;
                if (!string.IsNullOrWhiteSpace(_session.TargetWidgetId))
                {
                    _shell.BeginHydrateEditBaselineStorePayload(_session.TargetWidgetId!);
                }
                _shell.StageViewport.SyncHeaderCommitActionsForCurrentMode();
                _shell.ShowPageMessage(
                    InfoBarSeverity.Success,
                    string.Empty,
                    _loc.T(commitSuccessTitleKey),
                    null);
                _shell.ChatPanel.RefreshInPlaceGenerationChrome(refreshStream: false);
                _shell.ChatPanel.RefreshRevisionChrome();
                commitButton.IsEnabled = !_session.IsAnyStudioJobRunning && !string.IsNullOrWhiteSpace(_session.CurrentManifestJson);
                return;
            }

            _session.Committed = true;
            _hostBridge.ShowNotification(
                _loc.T(commitSuccessTitleKey),
                _loc.T("CustomWidget.Workshop.Placed.Body"));
            _shell.AllowClose = true;
            _shell.Close();
        }
        catch (Exception ex)
        {
            commitButton.IsEnabled = true;
            _shell.ChatPanel.RefreshInPlaceGenerationChrome(refreshStream: false);
            _shell.ShowFailureMessage(
                "CustomWidget.Workshop.Failure.SaveTitle",
                "CustomWidget.Workshop.Failure.SaveBody",
                ex);
        }
    }

    /// <summary>
    /// 用户确认一份外部暂存：Bridge 会重新校验、消费 stageId 再写入桌面。
    /// 成功后持久化共创修订历史并回到首屏让下一批待确认内容继续出现。
    /// </summary>
    private async Task CommitPendingAgentStageAsync(Button commitButton, string stageId)
    {
        commitButton.IsEnabled = false;
        try
        {
            bool wasEdit = _session.IsEditMode;
            string? candidateJson = _session.CurrentManifestJson;
            if (wasEdit && !string.IsNullOrWhiteSpace(_session.TargetWidgetId) && !string.IsNullOrWhiteSpace(candidateJson))
            {
                bool proceed = await VerifyOrMigrateStoreDataBeforeCommitAsync(_session.TargetWidgetId!, candidateJson);
                if (!proceed)
                {
                    commitButton.IsEnabled = true;
                    return;
                }
            }

            string response;
            _session.IsCommittingPendingAgentStage = true;
            try
            {
                response = await _hostBridge.CommitAgentStageAsync(stageId);
            }
            finally
            {
                _session.IsCommittingPendingAgentStage = false;
            }

            (bool ok, bool committed, string? committedWidgetId, string error) = StudioManifestInspector.ParseAgentStageResponse(response);
            if (!ok || !committed)
            {
                commitButton.IsEnabled = true;
                _shell.ShowPageMessage(
                    InfoBarSeverity.Error,
                    string.Empty,
                    string.IsNullOrWhiteSpace(error)
                        ? _loc.T("CustomWidget.Workshop.AgentStage.CommitFailed")
                        : error,
                    null);
                return;
            }

            string resolvedWidgetId = !string.IsNullOrWhiteSpace(committedWidgetId)
                ? committedWidgetId!
                : (_session.TargetWidgetId ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(resolvedWidgetId) && !string.IsNullOrWhiteSpace(candidateJson))
            {
                await _session.PersistPreviewSettingOverridesToWidgetAsync(resolvedWidgetId, candidateJson, _shell);

                IReadOnlyList<StudioPersistedRevision> committedRevisions =
                    CustomWidgetStudioSessionStore.MarkCommittedRevision(
                        _session.Revisions.Take(_session.RevisionIndex + 1).ToList(),
                        candidateJson,
                        source: "ai",
                        committedLogicJs: _session.CurrentLogicJs,
                        committedLocales: _session.CurrentLocales);
                if (wasEdit)
                {
                    _sessionStore.SaveCommittedWidgetSession(resolvedWidgetId, _session.BriefMarkdown, committedRevisions, _session.ChatMessages);
                }
                else
                {
                    _sessionStore.PromoteCreateSessionToWidget(resolvedWidgetId, _session.BriefMarkdown, committedRevisions, _session.ChatMessages);
                }

                _shell.RecordStudioCommitDiagnostic(
                    commitSource: "agent-stage",
                    widgetId: resolvedWidgetId,
                    wasEdit: wasEdit,
                    manifestJson: candidateJson,
                    logicJs: _session.CurrentLogicJs);

                if (_prefs.LocateOnDesktopAfterCommit && _hostBridge.HasWidgetManager)
                {
                    await _hostBridge.LocateCustomWidgetAsync(resolvedWidgetId);
                }
            }

            _session.PendingAgentStageId = null;
            _session.PendingAgentStageManifest = null;
            _session.UncommittedEditCount = 0;
            DisposeCodeToolWatcher();

            string stageSuccessKey = wasEdit ? "CustomWidget.Workshop.Saved.Title" : "CustomWidget.Workshop.Placed.Title";
            if (_session.Page == StudioPage.Preview)
            {
                _session.Committed = false;
                if (!wasEdit && !string.IsNullOrWhiteSpace(resolvedWidgetId))
                {
                    _session.TargetWidgetId = resolvedWidgetId;
                    _session.IsExternalImportSession = false;
                    _shell.UpdateWindowTitle();
                }
                if (!string.IsNullOrWhiteSpace(resolvedWidgetId))
                {
                    _shell.BeginHydrateEditBaselineStorePayload(resolvedWidgetId);
                }
                _shell.StageViewport.SyncHeaderCommitActionsForCurrentMode();
                _shell.ShowPageMessage(
                    InfoBarSeverity.Success,
                    string.Empty,
                    _loc.T(stageSuccessKey),
                    null);
                _shell.ChatPanel.RefreshInPlaceGenerationChrome(refreshStream: false);
                _shell.ChatPanel.RefreshRevisionChrome();
                commitButton.IsEnabled = !_session.IsAnyStudioJobRunning && !string.IsNullOrWhiteSpace(_session.CurrentManifestJson);
                return;
            }

            _session.Committed = true;
            _hostBridge.ShowNotification(
                _loc.T(stageSuccessKey),
                _loc.T("CustomWidget.Workshop.Placed.Body"));
            _shell.AllowClose = true;
            _shell.Close();
        }
        catch (Exception ex)
        {
            commitButton.IsEnabled = true;
            _shell.ShowFailureMessage(
                "CustomWidget.Workshop.Failure.SaveTitle",
                "CustomWidget.Workshop.Failure.SaveBody",
                ex);
        }
    }

    internal async Task PlaceTemplateDirectlyAsync(
        string manifestJson,
        string? emoji,
        Button triggerButton,
        CustomWidgetLocaleCatalog? locales = null,
        string? logicJs = null)
    {
        if (string.IsNullOrWhiteSpace(manifestJson) || !_hostBridge.HasWidgetManager)
        {
            return;
        }

        triggerButton.IsEnabled = false;
        try
        {
            CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(manifestJson);
            CustomWidgetManifest? effectiveManifest = parsed.Manifest;
            CustomWidgetLocaleCatalog? normalizedLocales = locales is { HasAnyLocale: true } ? locales : null;
            if (effectiveManifest is not null && normalizedLocales is not null)
            {
                effectiveManifest = effectiveManifest.WithLocales(normalizedLocales);
            }

            Dictionary<string, string>? initialSettings = null;
            Dictionary<string, string>? initialSecrets = null;
            if (effectiveManifest is not null)
            {
                CustomWidgetFirstRunSetupOutcome? setupOutcome =
                    await _shell.PromptFirstRunSetupForManifestAsync(effectiveManifest, _loc.CurrentCultureName);
                if (setupOutcome is { Applied: true })
                {
                    if (setupOutcome.Settings.Count > 0)
                    {
                        initialSettings = new Dictionary<string, string>(
                            setupOutcome.Settings,
                            StringComparer.OrdinalIgnoreCase);
                    }

                    if (setupOutcome.Secrets.Count > 0)
                    {
                        initialSecrets = new Dictionary<string, string>(
                            setupOutcome.Secrets,
                            StringComparer.OrdinalIgnoreCase);
                    }
                }
            }

            string jsonToCommit = manifestJson;
            if (CustomWidgetTemplates.IsEmojiIcon(emoji) &&
                CustomWidgetTemplates.MatchPresetOrNull(effectiveManifest?.Id) is null &&
                (effectiveManifest is null || !CustomWidgetTemplates.IsEmojiIcon(effectiveManifest.Icon)))
            {
                try
                {
                    jsonToCommit = CustomWidgetTemplates.WithUpdatedIconJson(jsonToCommit, emoji!.Trim());
                }
                catch
                {
                }
            }

            jsonToCommit = _session.StampManifestProvenanceForCommit(jsonToCommit);

            GlanceWidgetInstanceInfo? info = await _hostBridge.CreateCustomWidgetAsync(
                $"json:{jsonToCommit}",
                logicJsOverride: logicJs,
                hasLogicJsOverride: true,
                localesOverride: normalizedLocales,
                hasLocalesOverride: true,
                initialSettings: initialSettings,
                initialSecrets: initialSecrets);
            if (info is null)
            {
                return;
            }
            if (!string.IsNullOrWhiteSpace(info.Id))
            {
                string title = effectiveManifest?.ResolveLocalizedName(_loc.CurrentCultureName) ?? _loc.T("CustomWidget.Title");
                var initialRevisions = new List<StudioPersistedRevision>
                {
                    new()
                    {
                        RevisionId = "rev-1",
                        ManifestJson = jsonToCommit,
                        ManifestSha256 = CustomWidgetStudioSessionStore.ComputeManifestSha256(
                            jsonToCommit,
                            logicJs,
                            normalizedLocales),
                        LogicJs = logicJs,
                        Locales = normalizedLocales,
                        ChangeSummary = _loc.Format("CustomWidget.Workshop.History.FromTemplate", title),
                        Source = "template",
                        IsCommitted = true,
                        CreatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks
                    }
                };
                _sessionStore.SaveCommittedWidgetSession(info.Id, string.Empty, initialRevisions, []);
            }

            if (_prefs.LocateOnDesktopAfterCommit && !string.IsNullOrWhiteSpace(info.Id))
            {
                await _hostBridge.LocateCustomWidgetAsync(info.Id);
            }

            _session.Committed = true;
            _session.UncommittedEditCount = 0;
            _hostBridge.ShowNotification(
                _loc.T("CustomWidget.Workshop.Placed.Title"),
                _loc.T("CustomWidget.Workshop.Placed.Body"));
            _shell.AllowClose = true;
            _shell.Close();
        }
        catch (Exception ex)
        {
            triggerButton.IsEnabled = true;
            _shell.ShowFailureMessage(
                "CustomWidget.Workshop.Failure.SaveTitle",
                "CustomWidget.Workshop.Failure.SaveBody",
                ex);
        }
    }

    private async Task<bool> VerifyOrMigrateStoreDataBeforeCommitAsync(string widgetId, string candidateJson)
    {
        if (_shell.IsDialogOpen || _shell.ShellRootGrid.XamlRoot is null)
        {
            return true;
        }

        CustomWidgetParseResult candidateParsed = CustomWidgetManifestParser.Parse(candidateJson);
        if (candidateParsed.Manifest is null)
        {
            return true;
        }

        CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(widgetId);
        CustomWidgetStorePayload livePayload = await store.LoadAsync();
        string? diskManifestJson = StudioManifestInspector.TryReadWidgetManifest(widgetId, _hostBridge.Log);
        CustomWidgetManifest? prevManifest = !string.IsNullOrWhiteSpace(diskManifestJson)
            ? CustomWidgetManifestParser.Parse(diskManifestJson!).Manifest
            : null;

        CustomWidgetDataSafetyReport safety = CustomWidgetHealthAnalyzer.EvaluateStoreDataSafety(
            prevManifest,
            livePayload,
            candidateParsed.Manifest);
        if (!safety.HasDataLossRisk)
        {
            return true;
        }

        var bodySb = new StringBuilder();
        var missingNames = new List<string>();
        bool canAutoMigrateAll = safety.DroppedCollections.Count > 0 &&
                                 safety.DroppedNonEmptyStateKeys.Count == 0 &&
                                 safety.DroppedCollections.All(static c => !string.IsNullOrWhiteSpace(c.SuggestedNewCollectionKey));

        foreach (CustomWidgetDroppedCollectionRisk col in safety.DroppedCollections)
        {
            missingNames.Add(col.OldCollectionKey);
            bodySb.Append(_loc.Format("CustomWidget.Workshop.CoCreate.DataSafetyCollectionLine", col.OldCollectionKey, col.LiveRecordCount));
            if (!string.IsNullOrWhiteSpace(col.SuggestedNewCollectionKey))
            {
                bodySb.Append(_loc.Format("CustomWidget.Workshop.CoCreate.DataSafetyRenameHint", col.SuggestedNewCollectionKey));
            }
            bodySb.AppendLine();
        }

        if (safety.DroppedNonEmptyStateKeys.Count > 0)
        {
            missingNames.AddRange(safety.DroppedNonEmptyStateKeys);
            bodySb.AppendLine(_loc.Format(
                "CustomWidget.Workshop.CoCreate.DataSafetyStateLine",
                string.Join(", ", safety.DroppedNonEmptyStateKeys)));
        }

        _shell.IsDialogOpen = true;
        ContentDialogResult choice;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = _shell.ShellRootGrid.XamlRoot,
                RequestedTheme = StudioUiPrimitives.GetStudioExplicitTheme(_shell),
                Title = _loc.T("CustomWidget.Workshop.CoCreate.DataSafetyTitle"),
                Content = new TextBlock
                {
                    Text = bodySb.ToString().Trim(),
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = canAutoMigrateAll
                    ? _loc.T("CustomWidget.Workshop.CoCreate.DataSafetyMigrateAndSave")
                    : _loc.T("CustomWidget.Workshop.CoCreate.DataSafetyAskAiKeep"),
                SecondaryButtonText = canAutoMigrateAll
                    ? _loc.T("CustomWidget.Workshop.CoCreate.DataSafetyAskAiKeep")
                    : _loc.T("CustomWidget.Workshop.CoCreate.DataSafetySaveAnyway"),
                CloseButtonText = _loc.T("Common.Cancel"),
                DefaultButton = ContentDialogButton.Primary
            };
            if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out object? style) &&
                style is Style dialogStyle)
            {
                dialog.Style = dialogStyle;
            }

            choice = await dialog.ShowAsync();
        }
        finally
        {
            _shell.IsDialogOpen = false;
        }

        if (choice == ContentDialogResult.None)
        {
            return false;
        }

        if (canAutoMigrateAll)
        {
            if (choice == ContentDialogResult.Primary)
            {
                var renames = safety.DroppedCollections
                    .Select(static c => (c.OldCollectionKey, c.SuggestedNewCollectionKey!))
                    .ToList();
                await CustomWidgetHealthAnalyzer.MigrateRenamedCollectionsAsync(store, renames);
                return true;
            }

            // Secondary = Ask AI to preserve original keys
            string preservePrompt = _loc.Format(
                "CustomWidget.Workshop.CoCreate.DataSafetyRepairPrompt",
                string.Join(", ", missingNames));
            _ = _shell.Generation.BeginGenerationAsync(preservePrompt);
            return false;
        }

        if (choice == ContentDialogResult.Primary)
        {
            string preservePrompt = _loc.Format(
                "CustomWidget.Workshop.CoCreate.DataSafetyRepairPrompt",
                string.Join(", ", missingNames));
            _ = _shell.Generation.BeginGenerationAsync(preservePrompt);
            return false;
        }

        // Secondary = Save Anyway
        return true;
    }

    private async Task SeedNewlyCreatedWidgetStoreAsync(
        string widgetId,
        CustomWidgetManifest? manifest,
        CustomWidgetStorePayload? userSeedPayload,
        IReadOnlyDictionary<string, List<CustomWidgetRecord>>? previewSampleCollections,
        string? sampleDataJson)
    {
        Dictionary<string, string>? userState = null;
        Dictionary<string, JsonElement>? userStateJson = null;
        Dictionary<string, (List<Dictionary<string, string>> Items, List<Dictionary<string, JsonElement>> JsonItems)>? userCols = null;
        if (userSeedPayload is not null)
        {
            userState = new Dictionary<string, string>(StringComparer.Ordinal);
            userStateJson = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach ((string k, string v) in userSeedPayload.State)
            {
                if (!WidgetManager.IsTransientRuntimeStateKey(k))
                {
                    userState[k] = v;
                }
            }
            foreach ((string k, JsonElement v) in userSeedPayload.StateJson)
            {
                if (!WidgetManager.IsTransientRuntimeStateKey(k) && v.ValueKind != JsonValueKind.Undefined)
                {
                    userStateJson[k] = v.Clone();
                }
            }
            if (userSeedPayload.Collections.Count > 0)
            {
                userCols = ConvertRecordsDictionary(userSeedPayload.Collections);
            }
        }

        var sampleState = new Dictionary<string, string>(StringComparer.Ordinal);
        var sampleStateJson = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        Dictionary<string, (List<Dictionary<string, string>> Items, List<Dictionary<string, JsonElement>> JsonItems)> sampleCols =
            previewSampleCollections is { Count: > 0 }
                ? ConvertRecordsDictionary(previewSampleCollections)
                : new Dictionary<string, (List<Dictionary<string, string>> Items, List<Dictionary<string, JsonElement>> JsonItems)>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(sampleDataJson))
        {
            ParseSampleSeedJson(sampleDataJson, sampleState, sampleStateJson, sampleCols);
        }

        if (DeskBoxAgentBridgeServer.TryGetActiveWidgetManager() is { } activeManager &&
            await activeManager.TrySeedLiveCreatedWidgetAsync(
                widgetId,
                userState,
                userStateJson,
                userCols,
                sampleState,
                sampleStateJson,
                sampleCols))
        {
            return;
        }

        CustomWidgetDataStore createdStore = CustomWidgetDataStore.ForWidget(widgetId);
        CustomWidgetStorePayload latest = await createdStore.ReloadFromDiskAsync();
        bool mutated = false;

        foreach ((string k, string v) in sampleState)
        {
            if (!WidgetManager.IsTransientRuntimeStateKey(k) &&
                (!latest.State.TryGetValue(k, out string? existing) || string.IsNullOrWhiteSpace(existing)))
            {
                latest.State[k] = v;
                if (sampleStateJson.TryGetValue(k, out JsonElement jv))
                {
                    latest.StateJson[k] = jv.Clone();
                }
                mutated = true;
            }
        }

        if (userState is not null)
        {
            foreach ((string k, string v) in userState)
            {
                if (!string.IsNullOrWhiteSpace(v) || !latest.State.TryGetValue(k, out string? existing) || string.IsNullOrWhiteSpace(existing))
                {
                    latest.State[k] = v;
                    if (userStateJson is not null && userStateJson.TryGetValue(k, out JsonElement jv))
                    {
                        latest.StateJson[k] = jv.Clone();
                    }
                    mutated = true;
                }
            }
        }

        foreach ((string colName, var tuple) in sampleCols)
        {
            if (tuple.Items.Count > 0 &&
                (!latest.Collections.TryGetValue(colName, out List<CustomWidgetRecord>? existing) || existing.Count == 0))
            {
                mutated |= CustomWidgetStoreMutator.ApplyExternalDataPushToPayload(
                    latest,
                    manifest,
                    null,
                    colName,
                    "replace",
                    tuple.Items,
                    null,
                    tuple.JsonItems);
            }
        }

        if (userSeedPayload is not null)
        {
            foreach ((string colName, List<CustomWidgetRecord> records) in userSeedPayload.Collections)
            {
                if (records.Count > 0 ||
                    !latest.Collections.TryGetValue(colName, out List<CustomWidgetRecord>? existing) ||
                    existing.Count == 0)
                {
                    latest.Collections[colName] = records;
                    mutated = true;
                }
            }
        }

        if (mutated)
        {
            await createdStore.SaveAsync(latest, origin: _shell);
        }
    }

    private static Dictionary<string, (List<Dictionary<string, string>> Items, List<Dictionary<string, JsonElement>> JsonItems)> ConvertRecordsDictionary(
        IReadOnlyDictionary<string, List<CustomWidgetRecord>> source)
    {
        var result = new Dictionary<string, (List<Dictionary<string, string>> Items, List<Dictionary<string, JsonElement>> JsonItems)>(StringComparer.Ordinal);
        foreach ((string colName, List<CustomWidgetRecord> records) in source)
        {
            var strList = new List<Dictionary<string, string>>(records.Count);
            var jsonList = new List<Dictionary<string, JsonElement>>(records.Count);
            foreach (CustomWidgetRecord rec in records)
            {
                var strRec = new Dictionary<string, string>(rec.Fields, StringComparer.Ordinal);
                if (!string.IsNullOrWhiteSpace(rec.Id))
                {
                    strRec["id"] = rec.Id;
                }
                var jsonRec = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                if (!string.IsNullOrWhiteSpace(rec.Id))
                {
                    jsonRec["id"] = CustomWidgetScriptEngine.CreateStringJsonElement(rec.Id);
                }
                foreach ((string fk, JsonElement fv) in rec.FieldsJson)
                {
                    if (fv.ValueKind != JsonValueKind.Undefined)
                    {
                        jsonRec[fk] = fv.Clone();
                    }
                }
                strList.Add(strRec);
                jsonList.Add(jsonRec);
            }
            result[colName] = (strList, jsonList);
        }
        return result;
    }

    private static void ParseSampleSeedJson(
        string sampleDataJson,
        Dictionary<string, string> sampleState,
        Dictionary<string, JsonElement> sampleStateJson,
        Dictionary<string, (List<Dictionary<string, string>> Items, List<Dictionary<string, JsonElement>> JsonItems)> sampleCols)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(sampleDataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (doc.RootElement.TryGetProperty("state", out JsonElement stateEl) && stateEl.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in stateEl.EnumerateObject())
                {
                    if (WidgetManager.IsTransientRuntimeStateKey(prop.Name))
                    {
                        continue;
                    }
                    sampleStateJson[prop.Name] = prop.Value.Clone();
                    sampleState[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString() ?? string.Empty
                        : prop.Value.GetRawText();
                }
            }

            JsonElement collectionsRoot = doc.RootElement.TryGetProperty("collections", out JsonElement colsEl) && colsEl.ValueKind == JsonValueKind.Object
                ? colsEl
                : doc.RootElement;
            foreach (JsonProperty colProp in collectionsRoot.EnumerateObject())
            {
                if (string.Equals(colProp.Name, "state", StringComparison.Ordinal) ||
                    string.Equals(colProp.Name, "collections", StringComparison.Ordinal) ||
                    colProp.Value.ValueKind != JsonValueKind.Array ||
                    sampleCols.ContainsKey(colProp.Name))
                {
                    continue;
                }

                var items = new List<Dictionary<string, string>>();
                var jsonItems = new List<Dictionary<string, JsonElement>>();
                foreach (JsonElement recEl in colProp.Value.EnumerateArray())
                {
                    if (recEl.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    var dict = new Dictionary<string, string>(StringComparer.Ordinal);
                    var jsonDict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    foreach (JsonProperty fProp in recEl.EnumerateObject())
                    {
                        jsonDict[fProp.Name] = fProp.Value.Clone();
                        dict[fProp.Name] = fProp.Value.ValueKind == JsonValueKind.String
                            ? fProp.Value.GetString() ?? string.Empty
                            : fProp.Value.GetRawText();
                    }
                    if (dict.Count > 0)
                    {
                        items.Add(dict);
                        jsonItems.Add(jsonDict);
                    }
                }

                if (items.Count > 0)
                {
                    sampleCols[colProp.Name] = (items, jsonItems);
                }
            }
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        DisposeCodeToolWatcher();
    }
}
