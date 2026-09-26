using System.Text.RegularExpressions;
using System.Reflection;
using System.Reflection.Emit;
using DeskBox.Models;

namespace DeskBox.Tests;

/// <summary>
/// Module-boundary ratchet tests — the "legislation step" of
/// docs/architecture/module-boundary-roadmap-20260918.md. Laws are declared
/// before any physical code moves: exact violation manifests pin today's
/// offenders file-by-file and may only shrink, never grow — so a cleanup in
/// one file cannot launder a regression in another. Hard-zero laws stay
/// dormant while their target namespaces do not exist and start enforcing
/// the day they appear.
/// </summary>
public sealed class ModuleBoundaryContractTests
{
    [Fact]
    public void TodoSettingsPage_DoesNotWriteTodoFieldsDirectly()
    {
        string fields = string.Join("|", typeof(TodoSettingsSlice)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => Regex.Escape(property.Name)));
        Regex directWrite = new(
            $@"\b(?:_settingsService\s*\.\s*Settings|settings)\s*\.\s*(?:Todo\s*\.\s*)?(?:{fields})\s*=(?!=)");
        string[] violations = ProductionSource()
            .Where(item => item.Path.StartsWith(
                    "src/DeskBox/ViewModels/SettingsViewModel", StringComparison.Ordinal) &&
                item.Path.EndsWith(".cs", StringComparison.Ordinal))
            .SelectMany(item => directWrite.Matches(item.Source)
                .Cast<Match>()
                .Select(match => $"{item.Path}: {match.Value}"))
            .ToArray();

        Assert.True(violations.Length == 0,
            "Todo settings writes belong to TodoSettingsCoordinator:\n" +
            string.Join('\n', violations));
    }

    [Fact]
    public void QuickCaptureSettingsPage_DoesNotWriteTabFieldsDirectly()
    {
        Regex directWrite = new(
            @"\b(?:_settingsService\s*\.\s*Settings|settings)\s*\.\s*(?:QuickCapture\s*\.\s*)?QuickCapture(?:DefaultView|ShowTabBar|ShowRecordsTab|ShowPinnedTab|ShowRecentTab)\s*=(?!=)");
        string[] violations = ProductionSource()
            .Where(item => item.Path.StartsWith(
                    "src/DeskBox/ViewModels/SettingsViewModel", StringComparison.Ordinal) &&
                item.Path.EndsWith(".cs", StringComparison.Ordinal))
            .SelectMany(item => directWrite.Matches(item.Source)
                .Cast<Match>()
                .Select(match => $"{item.Path}: {match.Value}"))
            .ToArray();

        Assert.True(violations.Length == 0,
            "QuickCapture tab writes belong to QuickCaptureSettingsCoordinator:\n" +
            string.Join('\n', violations));
    }

    [Fact]
    public void QuickCaptureSettingsPage_DoesNotWritePresentationFieldsDirectly()
    {
        Regex directWrite = new(
            @"\b(?:_settingsService\s*\.\s*Settings|settings)\s*\.\s*(?:QuickCapture\s*\.\s*)?QuickCapture(?:TabStyle|ShowCreatedTime|ItemPreviewLineCount)\s*=(?!=)");
        string[] violations = ProductionSource()
            .Where(item => item.Path.StartsWith(
                    "src/DeskBox/ViewModels/SettingsViewModel", StringComparison.Ordinal) &&
                item.Path.EndsWith(".cs", StringComparison.Ordinal))
            .SelectMany(item => directWrite.Matches(item.Source)
                .Cast<Match>()
                .Select(match => $"{item.Path}: {match.Value}"))
            .ToArray();

        Assert.True(violations.Length == 0,
            "QuickCapture presentation writes belong to QuickCaptureSettingsCoordinator:\n" +
            string.Join('\n', violations));
    }

    [Fact]
    public void QuickCaptureSettingsPage_DoesNotOwnRecentLimitTrim()
    {
        Regex directWrite = new(
            @"\b(?:_settingsService\s*\.\s*Settings|settings)\s*\.\s*(?:QuickCapture\s*\.\s*)?QuickCaptureRecentLimit\s*=(?!=)");
        (string Path, string Source)[] settingsPages = ProductionSource()
            .Where(item => item.Path.StartsWith(
                    "src/DeskBox/ViewModels/SettingsViewModel", StringComparison.Ordinal) &&
                item.Path.EndsWith(".cs", StringComparison.Ordinal))
            .ToArray();
        string[] violations = settingsPages
            .SelectMany(item => directWrite.Matches(item.Source)
                .Cast<Match>()
                .Select(match => $"{item.Path}: {match.Value}"))
            .ToArray();
        Assert.Empty(violations);
        Assert.DoesNotContain("App.Current.QuickCaptureService.TrimRecentItemsAsync",
            string.Join('\n', settingsPages.Select(item => item.Source)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void QuickCaptureSettingsPage_DoesNotWriteTextSizeOverridesDirectly()
    {
        Regex directWrite = new(
            @"\b(?:_settingsService\s*\.\s*Settings|settings)\s*\.\s*(?:QuickCapture\s*\.\s*)?QuickCapture(?:ListTextSize|ContentTextSize)\s*=(?!=)");
        string[] violations = ProductionSource()
            .Where(item => item.Path.StartsWith(
                    "src/DeskBox/ViewModels/SettingsViewModel", StringComparison.Ordinal) &&
                item.Path.EndsWith(".cs", StringComparison.Ordinal))
            .SelectMany(item => directWrite.Matches(item.Source)
                .Cast<Match>()
                .Select(match => $"{item.Path}: {match.Value}"))
            .ToArray();

        Assert.Empty(violations);
    }

    private static readonly IReadOnlyDictionary<string, int> BusinessGlobalAccessExpectedViolations =
        new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["src/DeskBox/Models/WidgetItem.cs"] = 1,
        ["src/DeskBox/Models/OrganizationHistoryEntry.cs"] = 1,
        ["src/DeskBox/ViewModels/WidgetViewModel.Operations.cs"] = 3,
        ["src/DeskBox/ViewModels/SettingsViewModel.SettingsSync.cs"] = 1,
        ["src/DeskBox/ViewModels/SettingsViewModel.AppearanceOptions.cs"] = 2,
        ["src/DeskBox/ViewModels/QuickCaptureWidgetViewModel.Operations.cs"] = 1,
        ["src/DeskBox/ViewModels/MusicWidgetViewModel.MediaInfo.cs"] = 3,
        ["src/DeskBox/ViewModels/SettingsViewModel.FeatureOptions.cs"] = 4,
        ["src/DeskBox/ViewModels/SettingsViewModel.HotkeyAndStorage.cs"] = 1,
        ["src/DeskBox/ViewModels/SettingsViewModel.GroupNavigation.cs"] = 2,
        ["src/DeskBox/ViewModels/SettingsViewModel.PreferenceCallbacks.cs"] = 3,
        ["src/DeskBox/ViewModels/SettingsViewModel.PreferenceCommands.cs"] = 3,
        ["src/DeskBox/ViewModels/SettingsViewModel.QuickCaptureDiagnostics.cs"] = 2,
        ["src/DeskBox/ViewModels/SettingsViewModel.RuntimeDiagnostics.cs"] = 2,
        ["src/DeskBox/Services/DesktopDoubleClickActivationService.cs"] = 2,
        ["src/DeskBox/Services/FileMetaService.cs"] = 1,
        ["src/DeskBox/Services/GlobalHotkeyService.cs"] = 2,
        ["src/DeskBox/Services/QuickCaptureClipboardService.cs"] = 4,
        ["src/DeskBox/Services/PerformanceLogger.cs"] = 1,
        ["src/DeskBox/Services/Localized.cs"] = 1,
        ["src/DeskBox/Services/ResizeGuideOverlayService.cs"] = 4,
        ["src/DeskBox/Services/ThemeService.cs"] = 3,
        ["src/DeskBox/Services/StoreAppUpdateService.cs"] = 1,
        ["src/DeskBox/Services/SearchHotkeyService.cs"] = 3,
        ["src/DeskBox/Services/WeatherService.cs"] = 1,
        ["src/DeskBox/Services/JumpListService.cs"] = 1,
        ["src/DeskBox/Services/WidgetChromeMenuBuilder.cs"] = 1,
        ["src/DeskBox/Services/WidgetSettingsMenuHelper.cs"] = 2,
        ["src/DeskBox/Services/WidgetManager.ZOrder.cs"] = 14,
        ["src/DeskBox/Services/WidgetManager.Surfaces.cs"] = 1,
        ["src/DeskBox/Services/WidgetManager.Storage.cs"] = 1,
        ["src/DeskBox/Services/WidgetManager.FeatureWidgets.cs"] = 1,
        ["src/DeskBox/Services/WidgetManager.cs"] = 12,
        ["src/DeskBox/Services/WidgetManager.CapsuleArrangement.cs"] = 1,
        ["src/DeskBox/Services/WidgetLayerService.cs"] = 8,
    };

    [Fact]
    public void LegacyBusinessGlobalAccess_DoesNotGrow()
    {
        Regex access = new(@"\bApp\.(?:Current|UiDispatcherQueue)\b|\bIServiceProvider\b");
        var offenders = ProductionSource()
            .Where(item => (item.Path.StartsWith("src/DeskBox/Services/", StringComparison.Ordinal) ||
                            item.Path.StartsWith("src/DeskBox/ViewModels/", StringComparison.Ordinal) ||
                            item.Path.StartsWith("src/DeskBox/Models/", StringComparison.Ordinal)) &&
                !item.Path.Contains(".Aot", StringComparison.Ordinal) &&
                !Path.GetFileName(item.Path).StartsWith("Aot", StringComparison.Ordinal))
            .Select(item => (item.Path, Count: access.Matches(item.Source).Count))
            .Where(item => item.Count > 0)
            .ToArray();
        AssertViolationManifest(offenders, BusinessGlobalAccessExpectedViolations,
            "Inject new business dependencies; existing global-access exceptions may only shrink:");
    }

    [Fact]
    public void FeatureBusinessCode_DoesNotResolveGlobalApplicationServices()
    {
        // Inspect compiled references so aliases, fully qualified names and
        // generated async state machines cannot bypass a source-text check.
        Type[] types = typeof(DeskBox.App).Assembly.GetTypes()
            .Where(type => type.Namespace?.StartsWith("DeskBox.Features.", StringComparison.Ordinal) == true ||
                type.FullName?.StartsWith("DeskBox.Services.TodoSettingsCoordinator", StringComparison.Ordinal) == true ||
                type.FullName?.StartsWith("DeskBox.Services.SearchSettingsCoordinator", StringComparison.Ordinal) == true ||
                type.FullName?.StartsWith("DeskBox.Services.BackupSettingsCoordinator", StringComparison.Ordinal) == true ||
                type.FullName?.StartsWith("DeskBox.Services.BackupRestoreActions", StringComparison.Ordinal) == true ||
                type.FullName?.StartsWith("DeskBox.Services.QuickCaptureSettingsCoordinator", StringComparison.Ordinal) == true ||
                type.FullName?.StartsWith("DeskBox.Services.BackupBackend", StringComparison.Ordinal) == true ||
                type.FullName?.StartsWith("DeskBox.Services.ShutdownSequence", StringComparison.Ordinal) == true ||
                type.FullName?.StartsWith("DeskBox.Views.SettingsSections.SearchSettingsSection", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.NotEmpty(types);
        var violations = new List<string>();
        foreach (Type type in types)
        {
            bool isSearchView = type.FullName?.StartsWith("DeskBox.Views.SettingsSections.SearchSettingsSection", StringComparison.Ordinal) == true;
            foreach (Type reference in ReferencedTypes(type).SelectMany(ExpandType).Distinct())
            {
                string name = reference.FullName ?? string.Empty;
                bool globalServiceAccess = name is "DeskBox.App" or "System.IServiceProvider" or
                    "CommunityToolkit.Mvvm.DependencyInjection.Ioc" ||
                    (name == "Microsoft.UI.Xaml.Application" && !isSearchView) ||
                    name.StartsWith("Microsoft.Extensions.DependencyInjection.", StringComparison.Ordinal);
                bool featureUsesAdapter = type.Namespace?.StartsWith("DeskBox.Features.", StringComparison.Ordinal) == true &&
                    (name.StartsWith("DeskBox.Services.", StringComparison.Ordinal) ||
                     name.StartsWith("DeskBox.Platform.", StringComparison.Ordinal) || name == "DeskBox.Models.AppSettings");
                bool searchViewUsesRuntime = isSearchView &&
                    name is "DeskBox.Services.SettingsService" or "DeskBox.Services.SearchHotkeyService" or "DeskBox.Services.EverythingSearchService";
                if (globalServiceAccess || featureUsesAdapter || searchViewUsesRuntime)
                    violations.Add($"{type.FullName} -> {name}");
            }
        }
        Assert.True(violations.Count == 0,
            "Inject feature contracts instead of application/container/adapter dependencies:\n" + string.Join('\n', violations));
    }

    [Fact]
    public void BackupSettingsPages_DoNotResolveTheGlobalApp()
    {
        string[] paths =
        [
            "src/DeskBox/ViewModels/SettingsViewModel.DataBackupOptions.cs",
            "src/DeskBox/ViewModels/SettingsViewModel.CloudBackupOptions.cs",
            "src/DeskBox/Views/SettingsWindow.CloudBackup.cs",
            "src/DeskBox/Features/Backup/BackupSettingsViewModel.cs",
            "src/DeskBox/Services/BackupSettingsCoordinator.cs",
            "src/DeskBox/Services/BackupRestoreActions.cs"
        ];
        foreach (string path in paths)
        {
            string source = ProductionSource().Single(item => item.Path == path).Source;
            Assert.DoesNotContain("App.Current", source, StringComparison.Ordinal);
            Assert.DoesNotContain("IServiceProvider", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void QuickCaptureEnablement_DoesNotResolveTheGlobalApp()
    {
        foreach (string path in new[]
        {
            "src/DeskBox/Features/QuickCapture/QuickCaptureClipboardRuntime.cs",
            "src/DeskBox/Services/QuickCaptureSettingsCoordinator.cs",
            "src/DeskBox/ViewModels/SettingsViewModel.QuickCaptureSettings.cs"
        })
        {
            string source = ProductionSource().Single(item => item.Path == path).Source;
            Assert.DoesNotContain("App.Current", source, StringComparison.Ordinal);
            Assert.DoesNotContain("IServiceProvider", source, StringComparison.Ordinal);
        }

        string callbacks = ProductionSource().Single(item =>
            item.Path == "src/DeskBox/ViewModels/SettingsViewModel.FeatureCallbacks.cs").Source;
        string enablement = callbacks[callbacks.IndexOf("partial void OnQuickCaptureEnabledChanged", StringComparison.Ordinal)..
            callbacks.IndexOf("partial void OnQuickCaptureShowTabBarChanged", StringComparison.Ordinal)];
        string recording = callbacks[callbacks.IndexOf("partial void OnQuickCaptureClipboardEnabledChanged", StringComparison.Ordinal)..
            callbacks.IndexOf("partial void OnQuickCaptureRecentLimitChanged", StringComparison.Ordinal)];
        Assert.DoesNotContain("App.Current", enablement, StringComparison.Ordinal);
        Assert.DoesNotContain("App.Current", recording, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchMasterSwitch_UsesTheCoordinatorInsteadOfWidgetManagerGlobalAccess()
    {
        string manager = ProductionSource().Single(item =>
            item.Path == "src/DeskBox/Services/WidgetManager.FeatureWidgets.cs").Source;
        string settings = ProductionSource().Single(item =>
            item.Path == "src/DeskBox/ViewModels/SettingsViewModel.FeatureOptions.cs").Source;
        Assert.DoesNotContain("App.Current.SetSearchFeatureEnabled", manager, StringComparison.Ordinal);
        Assert.Contains("await _searchFeatureSettings.SetEnabledAsync(enabled, reveal)",
            manager, StringComparison.Ordinal);
        Assert.Contains("_searchFeatureSettings.CommitEnabledStateAsync(enabled)",
            manager, StringComparison.Ordinal);
        Assert.Contains("TrackSearchFeatureAction(_searchFeatureSettings.SetEnabledAsync(enabled, reveal: enabled))",
            settings, StringComparison.Ordinal);
    }

    [Fact]
    public void WidgetManagerContentRegistrations_HaveOneMutationBoundary()
    {
        Regex scatteredMutation = new(
            @"_contentWidgets\s*(?:\[[^\]]+\]\s*=|\.\s*(?:Remove|Clear)\s*\()|" +
            @"_widgetWindowHandles\s*\.\s*(?:Add|Remove|Clear)\s*\(");
        foreach ((string path, string source) in ProductionSource().Where(item =>
                     item.Path.StartsWith("src/DeskBox/Services/WidgetManager", StringComparison.Ordinal) &&
                     item.Path.EndsWith(".cs", StringComparison.Ordinal)))
        {
            Assert.True(!scatteredMutation.IsMatch(source),
                $"Content window ID/HWND mutations must use the shared registration boundary: {path}");
        }

        string creation = ProductionSource().Single(item =>
            item.Path == "src/DeskBox/Services/WidgetManager.cs").Source;
        int factory = creation.IndexOf("var window = factory.CreateContentWindow(plan);", StringComparison.Ordinal);
        int guarded = creation.IndexOf("try", factory + 1, StringComparison.Ordinal);
        int registered = creation.IndexOf("_contentWindowRegistration.Register(config.Id, window)",
            factory + 1, StringComparison.Ordinal);
        Assert.True(factory >= 0 && guarded > factory && registered > guarded,
            "Window tracking and registration must be inside creation's failure-cleanup scope.");
        Assert.Contains("if (registeredIds.Count == 0) return;", creation,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WidgetManagerFileSessions_HaveOneMutationBoundary()
    {
        Regex scatteredMutation = new(
            @"_fileWidgets\s*(?:\[[^\]]+\]\s*=|\.\s*(?:Remove|Clear)\s*\()");
        foreach ((string path, string source) in ProductionSource().Where(item =>
                     item.Path.StartsWith("src/DeskBox/Services/WidgetManager", StringComparison.Ordinal) &&
                     item.Path.EndsWith(".cs", StringComparison.Ordinal)))
        {
            Assert.True(!scatteredMutation.IsMatch(source),
                $"Standalone file-session mutations must use the shared identity boundary: {path}");
        }

        string manager = ProductionSource().Single(item =>
            item.Path == "src/DeskBox/Services/WidgetManager.cs").Source;
        Assert.Contains("_fileSessionRegistration.RegisterOrReplace(config.Id, session)",
            manager, StringComparison.Ordinal);
        Assert.Contains("_fileSessionRegistration.UnregisterHost(host)",
            manager, StringComparison.Ordinal);
    }

    private static IEnumerable<Type> ExpandType(Type type)
    {
        yield return type;
        if (type.HasElementType)
            foreach (Type element in ExpandType(type.GetElementType()!)) yield return element;
        if (type.IsGenericType)
            foreach (Type argument in type.GetGenericArguments().SelectMany(ExpandType)) yield return argument;
    }

    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        if (type.BaseType is { } parent) yield return parent;
        foreach (Type contract in type.GetInterfaces()) yield return contract;
        foreach (FieldInfo field in type.GetFields(flags)) yield return field.FieldType;
        IEnumerable<MethodBase> methods = type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags));
        foreach (MethodBase method in methods)
        {
            if (method is MethodInfo methodInfo) yield return methodInfo.ReturnType;
            foreach (ParameterInfo parameter in method.GetParameters()) yield return parameter.ParameterType;
            MethodBody? body = method.GetMethodBody();
            if (body is null) continue;
            foreach (LocalVariableInfo local in body.LocalVariables) yield return local.LocalType;
            byte[] il = body.GetILAsByteArray()!;
            for (int offset = 0; offset < il.Length;)
            {
                short code = il[offset++];
                if (code == 0xfe) code = unchecked((short)(0xfe00 | il[offset++]));
                OpCode op = IlOpCodes[code];
                if (op.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok)
                {
                    MemberInfo? member = method.Module.ResolveMember(BitConverter.ToInt32(il, offset),
                        type.IsGenericType ? type.GetGenericArguments() : null,
                        method.IsGenericMethod ? method.GetGenericArguments() : null);
                    if (member is Type target) yield return target;
                    else if (member?.DeclaringType is { } declaring) yield return declaring;
                    if (member is MethodInfo called && called.IsGenericMethod)
                        foreach (Type argument in called.GetGenericArguments()) yield return argument;
                }
                offset += op.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, offset),
                    _ => 4
                };
            }
        }
    }

    private static readonly IReadOnlyDictionary<short, OpCode> IlOpCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

    // Exact violation manifests measured on 2026-09-18: file → call-site count.
    // Entries may only shrink or disappear — a file that has to grow, or a new
    // file that has to appear, means new boundary violations were added, which
    // is exactly what these tests exist to reject. A bare total budget would
    // let one file's cleanup pay for another file's regression; the manifest
    // closes that substitution gap. Tighten an entry in the same commit that
    // removes its violations — the manifest is the ratchet's memory.
    //
    // Batch 31 (2026-09-26) completed the proactive Platform P/Invoke
    // migration: every DllImport/LibraryImport outside DeskBox.Platform was
    // moved or extracted, so this manifest is now empty and the test below is
    // a hard-zero law — new P/Invoke must land in Platform from today.
    private static readonly IReadOnlyDictionary<string, int> PlatformInteropExpectedViolations =
        new Dictionary<string, int>(StringComparer.Ordinal)
    {
    };

    private static readonly IReadOnlyDictionary<string, int> DestructiveFileOpExpectedViolations =
        new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["src/DeskBox/App.AotHotkeySmoke.cs"] = 1,
        ["src/DeskBox/App.AotLocalFilePersistenceSmoke.cs"] = 3,
        ["src/DeskBox/App.AotManagedUiSmoke.cs"] = 1,
        ["src/DeskBox/App.AotMusicVolumeMutationSmoke.cs"] = 3,
        ["src/DeskBox/App.AotMusicVolumeReadSmoke.cs"] = 2,
        ["src/DeskBox/App.AotMusicVolumeSessionMutationSmoke.cs"] = 3,
        // +1: the 5B-4C1C2A managed-card probe deletes the destination file
        // it just created inside the owned native-drop fixture widget root.
        ["src/DeskBox/App.AotNativeDropSmoke.cs"] = 5,
        ["src/DeskBox/App.AotQuickAccessMutationSmoke.cs"] = 2,
        ["src/DeskBox/App.AotShellMoveSmoke.cs"] = 2,
        ["src/DeskBox/App.AotShellSmoke.cs"] = 2,
        ["src/DeskBox/App.AotShortcutSmoke.cs"] = 5,
        ["src/DeskBox/App.AotTodoNotificationActivationSmoke.cs"] = 1,
        ["src/DeskBox/App.AotTodoNotificationForwardingSmoke.cs"] = 1,
        ["src/DeskBox/App.AotTodoNotificationLifecycleSmoke.cs"] = 1,
        ["src/DeskBox/App.AotTodoRecurrenceReminderSmoke.cs"] = 1,
        ["src/DeskBox/App.xaml.cs"] = 3,
        ["src/DeskBox/Helpers/NativeDropTarget.cs"] = 5,
        ["src/DeskBox/Services/AotShellMoveFixture.cs"] = 1,
        ["src/DeskBox/Services/AppUpdateService.cs"] = 5,
        ["src/DeskBox/Services/AttachmentStorageService.cs"] = 1,
        // Cloud backup orchestrator cleans up its own %TEMP% upload staging
        // directory — it never touches the data root (that stays inside
        // DeskBoxDataBackupService's owned surface).
        ["src/DeskBox/Services/CloudBackupService.cs"] = 1,
        // +2: scoped cloud restore deletes+copies domain files inside the
        // data directory it already owns (ApplyScopedRestoreCoreAsync).
        // +1: Directory.Move inside the scoped-restore staging dir remaps an
        // orphaned todo store onto a live widget id — confined to staging.
        ["src/DeskBox/Services/DeskBoxDataBackupService.cs"] = 12,
        ["src/DeskBox/Services/DeskBoxDiagnosticsBundleService.cs"] = 2,
        ["src/DeskBox/Services/DeskBoxDragData.cs"] = 3,
        ["src/DeskBox/Services/DesktopOrganizationCoordinator.cs"] = 1,
        ["src/DeskBox/Services/DesktopOrganizationRecoveryStore.cs"] = 2,
        ["src/DeskBox/Services/DesktopOrganizationTransaction.cs"] = 1,
        ["src/DeskBox/Services/DirectStartupTaskBackend.cs"] = 2,
        ["src/DeskBox/Services/FeedbackService.cs"] = 1,
        ["src/DeskBox/Services/FileService.CaseOnlyRename.cs"] = 2,
        ["src/DeskBox/Services/FileService.TransferProgress.cs"] = 4,
        ["src/DeskBox/Services/FileService.cs"] = 9,
        ["src/DeskBox/Services/GlanceImageService.cs"] = 3,
        ["src/DeskBox/Services/GlanceWidgetStore.cs"] = 1,
        ["src/DeskBox/Services/LegacySearchIndexCleanupService.cs"] = 1,
        ["src/DeskBox/Services/ManagedStorageDesktopShortcutService.cs"] = 1,
        ["src/DeskBox/Services/NativeNotificationActivationEnvelopeStore.cs"] = 6,
        ["src/DeskBox/Services/QuickCaptureService.cs"] = 8,
        ["src/DeskBox/Services/ReleaseNotesService.cs"] = 1,
        // +2: RevertLastCommit restores its own .bak (File.Copy) or deletes
        // a primary the same commit created — composite-save rollback inside
        // the store's owned surface.
        ["src/DeskBox/Services/ResilientJsonStore.cs"] = 9,
        ["src/DeskBox/Services/TodoWidgetStore.cs"] = 5,
        ["src/DeskBox/Services/VirtualDropFileNameResolver.cs"] = 1,
        ["src/DeskBox/Services/WidgetManager.FeatureWidgets.cs"] = 1,
        // +1: orphan managed-storage restore deletes the emptied source
        // folder after moving its contents back to the desktop (#112
        // migration rollback work in progress).
        ["src/DeskBox/Services/WidgetManager.Storage.cs"] = 5,
        ["src/DeskBox/ViewModels/TodoWidgetViewModel.DetailAndAttachments.cs"] = 1,
        ["src/DeskBox/Views/ContentWidgetWindow.NativeDragDrop.cs"] = 1,
        ["src/DeskBox/Views/SearchPopupWindow.xaml.cs"] = 2,
        ["src/DeskBox/Views/SettingsWindow.Feedback.cs"] = 1,
    };

    private static readonly string[] LegacyModelsUiExpectedFiles =
    {
        "src/DeskBox/Models/GlanceWidgetData.cs",
        "src/DeskBox/Models/SearchModels.cs",
        "src/DeskBox/Models/SettingsOption.cs",
        "src/DeskBox/Models/WeatherData.cs",
        "src/DeskBox/Models/WidgetItem.AotBindableProperties.cs",
        "src/DeskBox/Models/WidgetItem.cs",
    };

    private static readonly Regex PlatformInteropAttribute = new(
        @"\b(?:DllImport|LibraryImport)\s*\(", RegexOptions.Compiled);

    private static readonly Regex DestructiveFileOperation = new(
        @"(?<![A-Za-z_])(?:File|Directory)\.(?:Move|Delete|Copy|Replace)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex UiFrameworkDependency = new(
        @"Microsoft\.UI|Windows\.Foundation|WinRT\.|Microsoft\.Graphics",
        RegexOptions.Compiled);

    private static readonly Regex NamespaceDeclaration = new(
        @"namespace\s+([A-Za-z0-9_.]+)", RegexOptions.Compiled);

    private static readonly Regex FeatureNamespace = new(
        @"^DeskBox\.Features\.([A-Za-z0-9_]+)", RegexOptions.Compiled);

    private static readonly Regex FeatureUsing = new(
        @"using\s+DeskBox\.Features\.([A-Za-z0-9_]+)", RegexOptions.Compiled);

    [Fact]
    public void PlatformInterop_StaysInsideThePlatformDomain()
    {
        // DllImport/LibraryImport may only appear under DeskBox.Platform.*.
        // That namespace does not exist yet, so every current call site counts
        // against the budget and the count may only shrink as P/Invoke migrates
        // in (new P/Invoke must land in Platform from today).
        var offenders = CountMatchesOutsideNamespaces(
            PlatformInteropAttribute,
            namespacePrefix => namespacePrefix.StartsWith("DeskBox.Platform", StringComparison.Ordinal))
            .ToArray();

        AssertViolationManifest(
            offenders,
            PlatformInteropExpectedViolations,
            "New P/Invoke belongs in Platform:");
    }

    [Fact]
    public void DestructiveFileOperations_StayInsideOwnedDomains()
    {
        // File/Directory Move/Delete/Copy/Replace may only appear in the domains
        // that own file mutation: DeskBox.FileSafety (user-file policy), the
        // future DeskBox.Core.Persistence (local persistence machinery), and
        // DeskBox.Platform (mechanism wrappers). None exist yet, so all current
        // call sites sit in the budget and any new scatter fails the ratchet.
        var offenders = CountMatchesOutsideNamespaces(
            DestructiveFileOperation,
            namespacePrefix =>
                namespacePrefix.StartsWith("DeskBox.FileSafety", StringComparison.Ordinal) ||
                namespacePrefix.StartsWith("DeskBox.Core.Persistence", StringComparison.Ordinal) ||
                namespacePrefix.StartsWith("DeskBox.Platform", StringComparison.Ordinal))
            .ToArray();

        AssertViolationManifest(
            offenders,
            DestructiveFileOpExpectedViolations,
            "Route new file mutation through the file-safety kernel:");
    }

    [Fact]
    public void LegacyModelsNamespace_UiDependenciesDoNotGrow()
    {
        // Semantic law: Core.Models / FileSafety.Models / Sync.Contracts are
        // UI-free; UI.Models / ViewModels may hold WinUI types. The legacy
        // DeskBox.Models namespace is unsorted, so its UI-dependent files are
        // capped at today's count until the namespace is split by semantics.
        string[] offenders = FilesMatching(
                source => UiFrameworkDependency.IsMatch(source),
                namespacePrefix => namespacePrefix.StartsWith("DeskBox.Models", StringComparison.Ordinal))
            .ToArray();

        string[] unexpected = offenders
            .Where(path => !LegacyModelsUiExpectedFiles.Contains(path, StringComparer.Ordinal))
            .ToArray();
        Assert.True(
            unexpected.Length == 0,
            "New UI-bound models belong in a UI namespace:\n" +
            string.Join('\n', unexpected.Select(path => $"  NEW {path}")));
    }

    [Fact]
    public void RestrictedNamespaces_StayUiFree()
    {
        // Hard-zero law, dormant until these namespaces exist: the domain,
        // file-safety and sync model layers must never take WinUI/WASDK types.
        string[] restrictedPrefixes =
        {
            "DeskBox.Core.Models",
            "DeskBox.Core.Persistence",
            "DeskBox.FileSafety.Models",
            "DeskBox.Sync"
        };

        string[] offenders = FilesMatching(
                source => UiFrameworkDependency.IsMatch(source),
                namespacePrefix => restrictedPrefixes.Any(prefix =>
                    namespacePrefix.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Restricted namespaces must stay free of UI-framework dependencies:\n" +
            string.Join('\n', offenders.Select(path => $"  {path}")));
    }

    [Fact]
    public void FeatureNamespaces_DoNotCrossReference()
    {
        // Hard-zero law, dormant until DeskBox.Features.* exists: a feature may
        // use its own namespace and DeskBox.Contracts, never a sibling feature.
        List<string> violations = new();
        foreach ((string path, string source) in ProductionSource())
        {
            string[] declaredRoots = DeclaredNamespaces(source)
                .Select(prefix => FeatureNamespace.Match(prefix))
                .Where(match => match.Success)
                .Select(match => match.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (declaredRoots.Length == 0)
            {
                continue;
            }

            foreach (Match match in FeatureUsing.Matches(source))
            {
                string referencedRoot = match.Groups[1].Value;
                if (!declaredRoots.Contains(referencedRoot, StringComparer.Ordinal))
                {
                    violations.Add($"{path} uses DeskBox.Features.{referencedRoot}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Features must reach sibling features only through Contracts:\n" +
            string.Join('\n', violations.Select(violation => $"  {violation}")));
    }

    [Fact]
    public void SyncNamespace_ReachesOtherDomainsOnlyThroughContracts()
    {
        // Hard-zero law, dormant until DeskBox.Sync exists: the sync engine may
        // not touch FileSafety (local transactional data), concrete Features
        // implementations, or Platform — it reads the sync domain via Contracts.
        string[] forbiddenPrefixes = { "DeskBox.FileSafety", "DeskBox.Features", "DeskBox.Platform" };

        List<string> violations = new();
        foreach ((string path, string source) in ProductionSource())
        {
            bool isSync = DeclaredNamespaces(source).Any(prefix =>
                prefix.StartsWith("DeskBox.Sync", StringComparison.Ordinal));
            if (!isSync)
            {
                continue;
            }

            foreach (string forbidden in forbiddenPrefixes.Where(source.Contains))
            {
                violations.Add($"{path} references {forbidden}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "DeskBox.Sync must reach other domains only through Contracts:\n" +
            string.Join('\n', violations.Select(violation => $"  {violation}")));
    }

    [Fact]
    public void DomainNamespaces_AreNotGloballyImported()
    {
        // Boundary checks above are source-string laws: they only see a
        // forbidden reference when the source text names the namespace. A
        // `global using` makes the same reference invisible (bare type names
        // resolve without spelling the domain), silently defeating every
        // ratchet that relies on the string. Domain namespaces must be
        // imported explicitly, per file, where the dependency is visible.
        Regex globalDomainUsing = new(
            @"global\s+using\s+(?:static\s+)?(?:[\w.]+\s*=\s*)?DeskBox\.(FileSafety|Features|Platform|Sync)\b",
            RegexOptions.Compiled);

        List<string> violations = new();
        foreach ((string path, string source) in ProductionSource())
        {
            foreach (Match match in globalDomainUsing.Matches(source))
            {
                violations.Add($"{path} globally imports DeskBox.{match.Groups[1].Value}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Domain namespaces must be imported explicitly per file, not via global using:\n" +
            string.Join('\n', violations.Select(violation => $"  {violation}")));
    }

    [Fact]
    public void FileSafetyNamespace_UsesContractsNotNativeMechanism()
    {
        // Hard-zero law, dormant until DeskBox.FileSafety exists: the policy
        // layer (identity, done-is-done, WAL) talks to mechanism only through
        // contracts such as IFileSystemPrimitives — never P/Invoke directly.
        string[] offenders = FilesMatching(
                source => PlatformInteropAttribute.IsMatch(source),
                namespacePrefix => namespacePrefix.StartsWith("DeskBox.FileSafety", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "DeskBox.FileSafety must not carry P/Invoke; route mechanism through contracts:\n" +
            string.Join('\n', offenders.Select(path => $"  {path}")));
    }

    private static void AssertViolationManifest(
        (string Path, int Count)[] actual,
        IReadOnlyDictionary<string, int> expected,
        string guidance)
    {
        List<string> violations = new();
        foreach ((string path, int count) in actual)
        {
            if (!expected.TryGetValue(path, out int budget))
            {
                violations.Add($"  NEW {path}: {count}");
            }
            else if (count > budget)
            {
                violations.Add($"  GREW {path}: {count} (manifest {budget})");
            }
        }

        Assert.True(
            violations.Count == 0,
            guidance + "\n" + string.Join('\n', violations));
    }

    private static IEnumerable<(string Path, int Count)> CountMatchesOutsideNamespaces(
        Regex pattern,
        Func<string, bool> isExemptNamespace)
    {
        foreach ((string path, string source) in ProductionSource())
        {
            if (DeclaredNamespaces(source).Any(isExemptNamespace))
            {
                continue;
            }

            int count = pattern.Matches(source).Count;
            if (count > 0)
            {
                yield return (path, count);
            }
        }
    }

    private static IEnumerable<string> FilesMatching(
        Func<string, bool> sourceMatches,
        Func<string, bool> namespaceMatches)
    {
        foreach ((string path, string source) in ProductionSource())
        {
            if (DeclaredNamespaces(source).Any(namespaceMatches) && sourceMatches(source))
            {
                yield return path;
            }
        }
    }

    private static string[] DeclaredNamespaces(string source) =>
        NamespaceDeclaration.Matches(source)
            .Select(match => match.Groups[1].Value)
            .ToArray();

    private static IEnumerable<(string Path, string Source)> ProductionSource()
    {
        string projectDirectory = TestPaths.FromRepository("src/DeskBox");
        return Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                string relative = Path.GetRelativePath(projectDirectory, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                return !relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) &&
                       !relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) &&
                       !relative.StartsWith("AppPackages/", StringComparison.OrdinalIgnoreCase);
            })
            .Select(path => (RepositoryRelativePath(path), File.ReadAllText(path)));
    }

    private static string RepositoryRelativePath(string path) =>
        Path.GetRelativePath(TestPaths.FromRepository("."), path)
            .Replace(Path.DirectorySeparatorChar, '/');
}
