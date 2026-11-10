using DeskBox.Contracts;
using DeskBox.Features.Appearance;
using DeskBox.Features.FileStack;
using DeskBox.Features.Interaction;
using DeskBox.Features.Performance;
using DeskBox.Features.Search;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using DeskBox.Views.Dialogs;

namespace DeskBox.Tests;

public sealed class SystemDefaultsAndSettingsPolishTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "DeskBox.Tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void FreshAndRestoredDefaults_UseUpdatedSystemPreferences()
    {
        var fresh = new AppSettings();
        var restored = new AppSettings
        {
            WidgetCornerPreference = SettingsService.WidgetCornerPreferenceSquare,
            HiddenCacheCleanupScope = PerformanceSettingsPolicy.HiddenCacheCleanupScopeAllRecreatable,
            WidgetAnimationEffect = SettingsService.WidgetAnimationEffectFade,
            WidgetAnimationSpeed = SettingsService.WidgetAnimationSpeedFast,
            WidgetAnimationSlideDirection = SettingsService.WidgetAnimationSlideDirectionLeft,
            DynamicClickRaiseScope = SettingsService.DynamicClickRaiseScopeSingle,
            FileStackOpenMode = SettingsService.FileStackOpenModeInline,
            KeepCapsuleContentWarm = false
        };
        SettingsService.ApplyDefaultPreferences(restored);

        foreach (AppSettings candidate in new[] { fresh, restored })
        {
            // 1. 格子圆角：默认标准圆角
            Assert.Equal(SettingsService.WidgetCornerPreferenceRound, candidate.WidgetCornerPreference);

            // 2. 性能与资源 - 隐藏后保留界面缓存：默认打开 (Warm)
            EffectivePerformanceSettings perf = PerformanceSettingsPolicy.Resolve(candidate);
            Assert.Equal(PerformanceSettingsPolicy.HiddenCacheCleanupScopeWarm, perf.HiddenCacheCleanupScope);
            Assert.True(candidate.Performance.KeepCapsuleContentWarm);

            // 4. 显示与隐藏动画：默认贴边滑出 + 向右 + 舒缓
            WidgetAnimationOptions anim = WidgetAnimationSettings.From(candidate);
            Assert.Equal(SettingsService.WidgetAnimationEffectScreenEdgeSlide, anim.Effect);
            Assert.Equal(SettingsService.WidgetAnimationSlideDirectionRight, anim.SlideDirection);
            Assert.Equal(SettingsService.WidgetAnimationSpeedRelaxed, anim.Speed);

            // 7. 格子层级 - 点击格子时：默认整组一起浮出
            Assert.Equal(SettingsService.DynamicClickRaiseScopeGroup, candidate.DynamicClickRaiseScope);

            // 8. 文件叠放 - 展开方式：默认弹出式展开
            Assert.Equal(SettingsService.FileStackOpenModePopover, candidate.FileStackOpenMode);
        }
    }

    [Fact]
    public void KeepWarmCacheWhenHidden_IsEnabledByDefaultInPerformanceEditor()
    {
        var settings = new SettingsService(_root);
        var editor = new PerformanceSettingsViewModel(
            new PerformanceSettingsCoordinator(settings),
            new InteractionSettingsCoordinator(settings),
            static key => key,
            static () => true);

        Assert.True(editor.KeepWarmCacheWhenHidden);
        Assert.True(editor.KeepCapsuleContentWarm);
        Assert.Equal(
            PerformanceOptionKinds.HiddenCacheCleanupScopeWarm,
            editor.SelectedHiddenCacheCleanupScope);

        editor.KeepCapsuleContentWarm = false;
        Assert.False(settings.Settings.Performance.KeepCapsuleContentWarm);

        var legacyV17 = new AppSettings
        {
            SchemaVersion = 17,
            KeepCapsuleContentWarm = false
        };
        var (migrated, anyApplied) = new SettingsMigrationPipeline().RunMigrationsOnCopy(legacyV17);
        Assert.True(anyApplied);
        Assert.Equal(SettingsMigrationPipeline.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.True(migrated.Performance.KeepCapsuleContentWarm);

        var explicitUserOptOutV18 = new AppSettings
        {
            SchemaVersion = SettingsMigrationPipeline.CurrentSchemaVersion,
            KeepCapsuleContentWarm = false
        };
        var (preserved, preservedApplied) = new SettingsMigrationPipeline().RunMigrationsOnCopy(explicitUserOptOutV18);
        Assert.False(preservedApplied);
        Assert.False(preserved.Performance.KeepCapsuleContentWarm);
    }

    [Fact]
    public void WebAiDefault_UsesDeepSeekForMainlandChina_AndChatGptGlobally()
    {
        string prefsPath = Path.Combine(_root, "studio-prefs.json");
        Directory.CreateDirectory(_root);
        var store = new CustomWidgetStudioPreferencesStore(prefsPath);

        Assert.Equal(
            CustomWidgetStudioPreferencesStore.TargetDeepSeek,
            store.ResolveEffectiveWebAiTarget("zh-CN"));
        Assert.Equal(
            CustomWidgetStudioPreferencesStore.TargetDeepSeek,
            CustomWidgetStudioPreferencesStore.GetOrderedWebAiTargets("zh-CN", includeCopyOnly: false)[0].Id);
        Assert.Equal(
            CustomWidgetStudioPreferencesStore.TargetChatGpt,
            store.ResolveEffectiveWebAiTarget("en-US"));
    }

    [Fact]
    public void AnimationPreset_ExternalHotkeyResets_AndAboutCopyVersionButton_AreRemoved()
    {
        string appearanceXaml = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml"));
        string settingsWindowXaml = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.xaml"));
        string searchXaml = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsSections/SearchSettingsSection.xaml"));
        string clipboardXaml = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsSections/ClipboardSettingsSection.xaml"));

        // 4. 动画预设已移除
        Assert.DoesNotContain("Settings.Animation.Preset.Title", appearanceXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AnimationPreset", appearanceXaml, StringComparison.Ordinal);
        Assert.Null(typeof(AppearanceSettingsViewModel).GetProperty("AnimationPreset"));
        Assert.DoesNotContain(
            SettingsSearchCatalog.Entries,
            entry => string.Equals(entry.HeaderKey, "Settings.Animation.Preset.Title", StringComparison.Ordinal));

        // 5. 所有快捷键行右侧仅保留录入按钮与开关，移除外部重置按钮
        Assert.Contains("x:Name=\"GlobalHotkeyCaptureButton\"", settingsWindowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ResetGlobalHotkeyButton", settingsWindowXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SearchHotkeyCaptureButton\"", searchXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ResetSearchHotkeyButton", searchXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ClipboardHotkeyCaptureButton\"", clipboardXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ResetClipboardHotkeyButton", clipboardXaml, StringComparison.Ordinal);

        // 9. 关于页版本号旁的复制按钮已移除
        Assert.DoesNotContain("CopyAboutVersionButton_Click", settingsWindowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.About.CopyVersion", settingsWindowXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CapsuleOverrides_UseDefaultColoredWidgetIconAssets_AndSettingsCardRows()
    {
        var mappedFile = new WidgetConfig
        {
            WidgetKind = WidgetKind.File,
            FollowsDefaultStoragePath = false
        };
        var managedFile = new WidgetConfig
        {
            WidgetKind = WidgetKind.File,
            FollowsDefaultStoragePath = true
        };
        var pomodoroLiveKey = new WidgetConfig
        {
            WidgetKind = WidgetKind.Custom,
            Metadata = new Dictionary<string, string>
            {
                [CustomWidgetViewModel.TemplateMetadataKey] = "deskbox.pomodoro",
                [WidgetTitleIconCustomization.EmojiMetadataKey] = "🍅"
            }
        };
        var countdownLegacyKey = new WidgetConfig
        {
            WidgetKind = WidgetKind.Custom,
            Metadata = new Dictionary<string, string> { ["CustomTemplateId"] = "countdown" }
        };
        var clipboard = new WidgetConfig { WidgetKind = WidgetKind.Clipboard };

        Assert.True(WidgetTitleIconCustomization.NormalizeOverrides(pomodoroLiveKey));
        Assert.False(pomodoroLiveKey.Metadata.ContainsKey(WidgetTitleIconCustomization.EmojiMetadataKey));

        Assert.Equal(
            "mapped-folder",
            WidgetTitleIconKindNames.GetColorAssetName(
                WidgetTitleIconKindNames.NormalizeKind(WidgetTitleIconKindNames.FromWidgetConfig(mappedFile))));
        Assert.Equal(
            "managed-storage",
            WidgetTitleIconKindNames.GetColorAssetName(
                WidgetTitleIconKindNames.NormalizeKind(WidgetTitleIconKindNames.FromWidgetConfig(managedFile))));
        Assert.Equal(
            "pomodoro",
            WidgetTitleIconKindNames.GetColorAssetName(
                WidgetTitleIconKindNames.NormalizeKind(WidgetTitleIconKindNames.FromWidgetConfig(pomodoroLiveKey))));
        Assert.Equal(
            "countdown",
            WidgetTitleIconKindNames.GetColorAssetName(
                WidgetTitleIconKindNames.NormalizeKind(WidgetTitleIconKindNames.FromWidgetConfig(countdownLegacyKey))));
        Assert.Equal(
            "clipboard",
            WidgetTitleIconKindNames.GetColorAssetName(
                WidgetTitleIconKindNames.NormalizeKind(WidgetTitleIconKindNames.FromWidgetConfig(clipboard))));

        foreach (WidgetTitleIconKind kind in Enum.GetValues<WidgetTitleIconKind>())
        {
            string assetName = WidgetTitleIconKindNames.GetColorAssetName(kind);
            string svgPath = TestPaths.FromRepository($"src/DeskBox/Assets/WidgetTitleIcons/{assetName}.svg");
            Assert.True(File.Exists(svgPath), $"Missing colored widget icon SVG: {svgPath}");
        }

        var item = new CapsuleOverrideSettingsItem("w1", "待办", "收起方式：点击展开", "\uE73E", "todo");
        Assert.Equal("todo", item.ColorIconAssetName);

        string capsuleXaml = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsSections/CapsuleModeSettingsSection.xaml"));
        Assert.Contains("CapsuleOverrideIcon_Loaded", capsuleXaml, StringComparison.Ordinal);
        Assert.Contains("CapsuleOverrideIcon_DataContextChanged", capsuleXaml, StringComparison.Ordinal);
        Assert.Contains("<ImageIcon", capsuleXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Glyph=\"{Binding Glyph}\"", capsuleXaml, StringComparison.Ordinal);

        // Verify Editor ViewModel defaults for Interaction & FileStack too
        var settings = new SettingsService(_root);
        var interactionVm = new InteractionSettingsViewModel(
            new InteractionSettingsCoordinator(settings),
            static key => key);
        var fileStackVm = new FileStackSettingsViewModel(
            new FileStackSettingsCoordinator(settings),
            static key => key,
            static (key, _) => key);
        Assert.Equal(DynamicClickRaiseScopes.Group, interactionVm.ClickRaiseScope);
        Assert.Equal(FileStackOptionKinds.OpenModePopover, fileStackVm.OpenMode);
    }

    [Fact]
    public void HotkeyRecorderReset_AndRestoreDefaults_UseFactoryHotkeyDefaultsAcrossAllScopes()
    {
        Assert.Equal(
            new GlobalHotkeyActivation(
                SettingsService.DefaultGlobalHotkeyActivationKind,
                new GlobalHotkeyGesture(
                    (HotkeyModifierKeys)SettingsService.DefaultGlobalHotkeyModifiers,
                    SettingsService.DefaultGlobalHotkeyKey)),
            HotkeyRecorderDialog.GetDefaultActivation(HotkeyRecorderDialog.Scope.Global));
        Assert.Equal(
            GlobalHotkeyActivation.FromChord(SearchSettingsViewModel.DefaultGesture),
            HotkeyRecorderDialog.GetDefaultActivation(HotkeyRecorderDialog.Scope.Search));
        Assert.Equal(
            GlobalHotkeyActivation.FromChord(ClipboardHotkeyService.DefaultGesture),
            HotkeyRecorderDialog.GetDefaultActivation(HotkeyRecorderDialog.Scope.Clipboard));

        string recorderCode = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/Dialogs/HotkeyRecorderDialog.xaml.cs"));
        Assert.Contains("GetDefaultActivation(RecorderScope)", recorderCode, StringComparison.Ordinal);

        string preferenceCommands = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/ViewModels/SettingsViewModel.PreferenceCommands.cs"));
        Assert.Contains("_hotkeyActions.RefreshRegistration();", preferenceCommands, StringComparison.Ordinal);
        Assert.Contains("_hotkeyActions.RefreshSearchHotkeyRegistration();", preferenceCommands, StringComparison.Ordinal);
        Assert.Contains("_hotkeyActions.RefreshClipboardHotkeyRegistration();", preferenceCommands, StringComparison.Ordinal);
    }

    [Fact]
    public void Win10AndWin11_RoundedSurfaceFallbacks_SearchSynonyms_AndShadowCompaction_AreConsistent()
    {
        string contentWindowCode = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/ContentWidgetWindow.xaml.cs"));
        Assert.Contains(
            "ContentWidgetShell.MaterialSurface.Visibility == Visibility.Collapsed",
            contentWindowCode,
            StringComparison.Ordinal);

        string stackPopoverCode = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Controls/WidgetContents/FileSurfaceContent.StackPopover.cs"));
        Assert.DoesNotContain(
            "BuildLegacyAcrylicSurfaceOverlayColor",
            stackPopoverCode,
            StringComparison.Ordinal);

        string quickPasteCode = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/ClipboardQuickPasteWindow.xaml.cs"));
        Assert.Contains(
            "SurfaceBorder.CornerRadius = WindowsCompatibilityService.SupportsNativeWindowCorners",
            quickPasteCode,
            StringComparison.Ordinal);

        string textShadowCode = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Helpers/WidgetTextShadow.cs"));
        Assert.Contains(
            "PendingTexts.RemoveAll(ShouldPrunePendingReference);",
            textShadowCode,
            StringComparison.Ordinal);

        Assert.True(
            SettingsSearchMatcher.GetScore("过渡效果", "显示与隐藏动画", "外观", string.Empty) <
            SettingsSearchMatcher.NoMatch);
    }

    [Fact]
    public void FirstPartyAdapters_HonorKeepCapsuleContentWarm_AndHiddenWarmCacheScope()
    {
        string clipboardAdapter = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Controls/WidgetContents/ClipboardWidgetContentAdapter.cs"));
        string glanceContent = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Controls/WidgetContents/GlanceWidgetContent.xaml.cs"));
        string todoAdapter = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Controls/WidgetContents/TodoWidgetContentAdapter.cs"));
        string quickCaptureAdapter = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Controls/WidgetContents/QuickCaptureWidgetContentAdapter.cs"));
        string fileAdapter = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Controls/WidgetContents/FileWidgetContentAdapter.cs"));

        Assert.Contains("ViewModel.SetViewAttached(MaterializedView is not null && (!collapsed || keepWarm));", clipboardAdapter, StringComparison.Ordinal);
        Assert.Contains("if (collapsed && !keepWarm)", clipboardAdapter, StringComparison.Ordinal);
        Assert.Contains("ShouldKeepWarmWhileCollapsed", glanceContent, StringComparison.Ordinal);
        Assert.Contains("ShouldKeepWarmWhileHidden", glanceContent, StringComparison.Ordinal);
        Assert.Contains("if (!ShouldKeepCollapsedViewWarm)", todoAdapter, StringComparison.Ordinal);
        Assert.Contains("if (collapsed && !ShouldKeepCollapsedViewWarm)", quickCaptureAdapter, StringComparison.Ordinal);
        Assert.Contains("PerformanceSettingsPolicy.HiddenCacheCleanupScopeAllRecreatable", fileAdapter, StringComparison.Ordinal);
    }
}
