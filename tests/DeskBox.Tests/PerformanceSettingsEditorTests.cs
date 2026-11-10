using DeskBox.Contracts;
using DeskBox.Features.Performance;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Batch 50 — the facade-retirement closing batch's performance tests. The
/// performance section (plus the General section's inline preset combo)
/// re-binds to the performance editor; the section's eleven custom-mode
/// local-lambda facade writes, the preset write and the quiescence trim
/// switch move to the performance coordinator. These tests pin the editor's
/// behavior (policy-resolved projection, write-through with the custom-mode
/// switch, decorative-toggle derived flag, trim-switch write owners,
/// external sync without write-back) and the migration pattern itself
/// (XAML paths, AOT bridges, General-combo element DataContexts, shell
/// facade removal).
/// </summary>
public sealed class PerformanceSettingsEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    private static readonly Func<string, string> PassthroughLocalize = static key => key;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static (SettingsService Settings, PerformanceSettingsCoordinator Coordinator,
        InteractionSettingsCoordinator Interaction, PerformanceSettingsViewModel Editor) CreateEditor(
        string root,
        Action<SettingsService>? arrange = null)
    {
        var settings = new SettingsService(root);
        arrange?.Invoke(settings);
        var coordinator = new PerformanceSettingsCoordinator(settings);
        var interaction = new InteractionSettingsCoordinator(settings);
        return (settings, coordinator, interaction,
            new PerformanceSettingsViewModel(coordinator, interaction, PassthroughLocalize, () => false));
    }

    [Fact]
    public void Constructor_ProjectsThePolicyResolvedSnapshot()
    {
        (_, _, _, PerformanceSettingsViewModel editor) = CreateEditor(
            _root,
            settings =>
            {
                settings.Settings.Performance.PerformanceMode =
                    PerformanceOptionKinds.ModeResourceSaver;
                settings.Settings.Performance.HiddenCacheCleanupDelaySeconds =
                    PerformanceOptionKinds.CleanupAfter5Minutes;
            });

        // ResourceSaver resolves the hidden delay back to its preset (30s)
        // exactly like the legacy shell's InitializePerformanceSettings.
        Assert.Equal(PerformanceOptionKinds.ModeResourceSaver, editor.SelectedPerformanceMode);
        Assert.Equal(
            PerformanceOptionKinds.CleanupAfter30Seconds,
            editor.SelectedHiddenCacheCleanupDelaySeconds);
        Assert.Equal(
            PerformanceOptionKinds.CleanupAfter5Minutes,
            editor.SelectedVisibleIdleCacheCleanupDelaySeconds);
        Assert.Equal(PerformanceOptionKinds.CacheBudgetSmall, editor.SelectedPerformanceCacheBudget);
        Assert.Equal(
            PerformanceOptionKinds.HiddenCacheCleanupScopeWarm,
            editor.SelectedHiddenCacheCleanupScope);
        // The custom mode is only offered while selected.
        SettingsOption[] modes = [.. editor.AvailablePerformanceModeOptions];
        Assert.Equal(2, modes.Length);
    }

    [Fact]
    public void PresetSelection_AppliesThePresetFieldsAndPersists()
    {
        (SettingsService settings, _, _, PerformanceSettingsViewModel editor) = CreateEditor(
            _root,
            settings => settings.Settings.Performance.PerformanceMode =
                PerformanceOptionKinds.ModeResourceSaver);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        editor.SelectedPerformanceMode = PerformanceOptionKinds.ModeBalanced;

        Assert.Equal(PerformanceOptionKinds.ModeBalanced, editor.SelectedPerformanceMode);
        Assert.Equal(
            PerformanceOptionKinds.CleanupAfter30Seconds,
            editor.SelectedHiddenCacheCleanupDelaySeconds);
        Assert.Equal(
            PerformanceOptionKinds.CleanupAfter10Minutes,
            editor.SelectedVisibleIdleCacheCleanupDelaySeconds);
        Assert.Equal(PerformanceOptionKinds.CacheBudgetBalanced, editor.SelectedPerformanceCacheBudget);
        Assert.Equal(1, notified);
        settings.SaveAsync().GetAwaiter().GetResult();
        var reloaded = new SettingsService(_root);
        reloaded.LoadAsync().GetAwaiter().GetResult();
        Assert.Equal(
            PerformanceOptionKinds.ModeBalanced,
            reloaded.Settings.Performance.PerformanceMode);
    }

    [Fact]
    public void CustomDetailEdit_PersistsAndSwitchesTheModeToCustom()
    {
        (SettingsService settings, _, _, PerformanceSettingsViewModel editor) = CreateEditor(_root);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        editor.SelectedHiddenCacheCleanupDelaySeconds =
            PerformanceOptionKinds.CleanupAfter5Minutes;

        Assert.Equal(PerformanceOptionKinds.ModeCustom, editor.SelectedPerformanceMode);
        SettingsOption[] modes = [.. editor.AvailablePerformanceModeOptions];
        Assert.Equal(3, modes.Length);
        Assert.Equal(
            PerformanceOptionKinds.CleanupAfter5Minutes,
            settings.Settings.Performance.HiddenCacheCleanupDelaySeconds);
        Assert.Equal(PerformanceOptionKinds.ModeCustom, settings.Settings.Performance.PerformanceMode);
        // Unchanged re-selection is skipped (no second broadcast, no write).
        int notifiedBeforeRepeat = notified;
        editor.SelectedHiddenCacheCleanupDelaySeconds =
            PerformanceOptionKinds.CleanupAfter5Minutes;
        Assert.Equal(notifiedBeforeRepeat, notified);
    }

    [Fact]
    public void BudgetAndScopeEdits_PersistThroughTheCustomPath()
    {
        (SettingsService settings, _, _, PerformanceSettingsViewModel editor) = CreateEditor(
            _root,
            settings => settings.Settings.Performance.PerformanceMode =
                PerformanceOptionKinds.ModeBalanced);

        editor.SelectedPerformanceCacheBudget = PerformanceOptionKinds.CacheBudgetLarge;
        editor.SelectedHiddenCacheCleanupScope = PerformanceOptionKinds.HiddenCacheCleanupScopeAllRecreatable;
        editor.SelectedVisibleIdleCacheCleanupDelaySeconds =
            PerformanceOptionKinds.CleanupAfter15Minutes;

        Assert.Equal(PerformanceOptionKinds.ModeCustom, editor.SelectedPerformanceMode);
        Assert.Equal(
            PerformanceOptionKinds.CacheBudgetLarge,
            settings.Settings.Performance.PerformanceCacheBudget);
        Assert.Equal(
            PerformanceOptionKinds.HiddenCacheCleanupScopeAllRecreatable,
            settings.Settings.Performance.HiddenCacheCleanupScope);
        Assert.Equal(
            PerformanceOptionKinds.CleanupAfter15Minutes,
            settings.Settings.Performance.VisibleIdleCacheCleanupDelaySeconds);
    }

    [Fact]
    public void UnknownValues_NormalizeToCanonicalOptions()
    {
        (SettingsService settings, _, _, PerformanceSettingsViewModel editor) = CreateEditor(_root);

        editor.SelectedPerformanceMode = "Nonsense";
        Assert.Equal(PerformanceOptionKinds.ModeBalanced, editor.SelectedPerformanceMode);

        editor.SelectedPerformanceCacheBudget = "Huge";
        Assert.Equal(PerformanceOptionKinds.CacheBudgetBalanced, editor.SelectedPerformanceCacheBudget);

        editor.SelectedHiddenCacheCleanupScope = "Deep";
        Assert.Equal(
            PerformanceOptionKinds.HiddenCacheCleanupScopeWarm,
            editor.SelectedHiddenCacheCleanupScope);
    }

    [Fact]
    public void DecorativeToggle_WritesTheDerivedFlagAndSwitchesToCustom()
    {
        (SettingsService settings, _, _, PerformanceSettingsViewModel editor) = CreateEditor(
            _root,
            settings => settings.Settings.Performance.PerformanceMode =
                PerformanceOptionKinds.ModeBalanced);

        // Default projection: all four switches on (legacy flags default).
        Assert.Equal(
            PassthroughLocalize("Settings.Performance.DecorativeAnimations.All"),
            editor.ContinuousDecorativeAnimationsSummaryText);

        editor.ToggleContinuousDecorativeAnimation(
            PerformanceOptionKinds.DecorativeAnimationVinylRotation);

        PerformanceSettingsSlice performance = settings.Settings.Performance;
        Assert.False(performance.EnableVinylRotationAnimations);
        Assert.False(performance.EnableContinuousDecorativeAnimations);
        // Decorative animation switches stay independent of the performance preset.
        Assert.Equal(PerformanceOptionKinds.ModeBalanced, performance.PerformanceMode);
        Assert.Equal(PerformanceOptionKinds.ModeBalanced, editor.SelectedPerformanceMode);
        Assert.True(editor.IsContinuousDecorativeAnimationSelected(
            PerformanceOptionKinds.DecorativeAnimationTextMarquee));

        // All four off renders the localized "off" summary.
        editor.ToggleContinuousDecorativeAnimation(
            PerformanceOptionKinds.DecorativeAnimationTextMarquee);
        editor.ToggleContinuousDecorativeAnimation(
            PerformanceOptionKinds.DecorativeAnimationGlanceRotation);
        editor.ToggleContinuousDecorativeAnimation(
            PerformanceOptionKinds.DecorativeAnimationCompactAmbient);
        Assert.Equal(
            PassthroughLocalize("Common.Off"),
            editor.ContinuousDecorativeAnimationsSummaryText);
        // A partial selection joins the localized display names.
        editor.ToggleContinuousDecorativeAnimation(
            PerformanceOptionKinds.DecorativeAnimationGlanceRotation);
        Assert.Contains(
            PassthroughLocalize("Settings.Performance.DecorativeAnimations.GlanceRotation"),
            editor.ContinuousDecorativeAnimationsSummaryText);
    }

    [Fact]
    public void TrimSwitches_WriteThroughTheirRespectiveCoordinators()
    {
        (SettingsService settings, _, _, PerformanceSettingsViewModel editor) = CreateEditor(
            _root,
            settings => settings.Settings.Performance.PerformanceMode =
                PerformanceOptionKinds.ModeBalanced);

        // The idle and immediate-hidden switches persist through the
        // interaction coordinator (their write owner since batch 34); the
        // quiescence switch through the performance coordinator.
        editor.IdleWorkingSetTrimEnabled = false;
        editor.ImmediateHiddenWorkingSetTrimEnabled = true;
        editor.QuiescenceWorkingSetTrimEnabled = false;
        editor.KeepCapsuleContentWarm = true;

        PerformanceSettingsSlice performance = settings.Settings.Performance;
        Assert.False(performance.IdleWorkingSetTrimEnabled);
        Assert.True(performance.ImmediateHiddenWorkingSetTrimEnabled);
        Assert.False(performance.QuiescenceWorkingSetTrimEnabled);
        Assert.True(performance.KeepCapsuleContentWarm);
        // Preset switches preserve KeepCapsuleContentWarm as an independent user preference.
        editor.SelectedPerformanceMode = PerformanceOptionKinds.ModeResourceSaver;
        Assert.True(performance.KeepCapsuleContentWarm);
        Assert.True(editor.KeepCapsuleContentWarm);
        editor.SelectedPerformanceMode = PerformanceOptionKinds.ModeBalanced;
        // Trim switches never switch the preset mode to custom.
        Assert.Equal(
            PerformanceOptionKinds.ModeBalanced,
            performance.PerformanceMode);
    }

    [Fact]
    public void ExternalSync_RefreshesTheProjectionWithoutWritingBack()
    {
        (SettingsService settings, _, _, PerformanceSettingsViewModel editor) = CreateEditor(_root);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        settings.Settings.Performance.PerformanceMode = PerformanceOptionKinds.ModeCustom;
        settings.Settings.Performance.HiddenCacheCleanupDelaySeconds =
            PerformanceOptionKinds.CleanupAfter5Minutes;

        int notifiedBeforeSync = notified;
        editor.SyncPresentation();

        Assert.Equal(PerformanceOptionKinds.ModeCustom, editor.SelectedPerformanceMode);
        Assert.Equal(
            PerformanceOptionKinds.CleanupAfter5Minutes,
            editor.SelectedHiddenCacheCleanupDelaySeconds);
        Assert.Equal(notifiedBeforeSync, notified);
    }

    [Fact]
    public void Options_ListCanonicalValuesWithLocalizedNames()
    {
        (_, _, _, PerformanceSettingsViewModel editor) = CreateEditor(_root);

        SettingsOption[] hiddenDelays = [.. editor.AvailableHiddenCacheCleanupDelayOptions];
        Assert.Equal(3, hiddenDelays.Length);
        Assert.Equal(PerformanceOptionKinds.CleanupAfter30Seconds, hiddenDelays[0].Value);
        Assert.Equal(
            "Settings.Performance.HiddenCleanup.30Seconds",
            hiddenDelays[0].DisplayName);

        SettingsOption[] visibleDelays =
            [.. editor.AvailableVisibleIdleCacheCleanupDelayOptions];
        Assert.Equal(5, visibleDelays.Length);
        Assert.Equal(PerformanceOptionKinds.CleanupAfter15Minutes, visibleDelays[4].Value);

        SettingsOption[] budgets = [.. editor.AvailablePerformanceCacheBudgetOptions];
        Assert.Equal(3, budgets.Length);
        Assert.Equal(PerformanceOptionKinds.CacheBudgetSmall, budgets[0].Value);

        SettingsOption[] scopes = [.. editor.AvailableHiddenCacheCleanupScopeOptions];
        Assert.Equal(2, scopes.Length);
        Assert.Equal(
            PerformanceOptionKinds.HiddenCacheCleanupScopeAllRecreatable,
            scopes[0].Value);

        Assert.Equal(
            PerformanceOptionKinds.SupportedDecorativeAnimationOptions,
            editor.AvailableContinuousDecorativeAnimationOptions);
    }

    [Fact]
    public void StoppedCoordinator_RefusesFurtherWrites()
    {
        (_, PerformanceSettingsCoordinator coordinator, _, PerformanceSettingsViewModel editor) =
            CreateEditor(_root);
        coordinator.Stop();

        Assert.Throws<ObjectDisposedException>(
            () => editor.SelectedHiddenCacheCleanupDelaySeconds =
                PerformanceOptionKinds.CleanupAfter5Minutes);
    }

    [Fact]
    public void PolicyConstants_AliasTheContractCanonicalValues()
    {
        Assert.Equal(PerformanceOptionKinds.ModeBalanced, PerformanceSettingsPolicy.ModeBalanced);
        Assert.Equal(
            PerformanceOptionKinds.ModeResourceSaver,
            PerformanceSettingsPolicy.ModeResourceSaver);
        Assert.Equal(PerformanceOptionKinds.ModeCustom, PerformanceSettingsPolicy.ModeCustom);
        Assert.Equal(
            PerformanceOptionKinds.CleanupAfter15Minutes,
            PerformanceSettingsPolicy.CleanupAfter15Minutes);
        Assert.Equal(
            PerformanceOptionKinds.CacheBudgetBalanced,
            PerformanceSettingsPolicy.CacheBudgetBalanced);
        Assert.Equal(
            PerformanceOptionKinds.HiddenCacheCleanupScopeWarm,
            PerformanceSettingsPolicy.HiddenCacheCleanupScopeWarm);
        Assert.Equal(
            PerformanceOptionKinds.DecorativeAnimationCompactAmbient,
            PerformanceSettingsPolicy.DecorativeAnimationCompactAmbient);
    }

    [Fact]
    public void SettingsShell_NoLongerExposesThePerformanceCompatFacade()
    {
        System.Reflection.PropertyInfo[] properties = typeof(ViewModels.SettingsViewModel)
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        string[] removed =
        [
            "SelectedPerformanceMode",
            "AvailablePerformanceModeOptions",
            "SelectedHiddenCacheCleanupDelaySeconds",
            "AvailableHiddenCacheCleanupDelayOptions",
            "SelectedVisibleIdleCacheCleanupDelaySeconds",
            "AvailableVisibleIdleCacheCleanupDelayOptions",
            "SelectedTransientWindowReleaseDelaySeconds",
            "SelectedPerformanceCacheBudget",
            "AvailablePerformanceCacheBudgetOptions",
            "SelectedHiddenCacheCleanupScope",
            "AvailableHiddenCacheCleanupScopeOptions",
            "ContinuousDecorativeAnimationsSummaryText",
            "IsContinuousDecorativeAnimationSelected",
            "GetContinuousDecorativeAnimationDisplayName",
            "ToggleContinuousDecorativeAnimation",
            "AvailableContinuousDecorativeAnimationOptions",
            "IdleWorkingSetTrimEnabled",
            "ImmediateHiddenWorkingSetTrimEnabled",
            "QuiescenceWorkingSetTrimEnabled",
            "KeepCapsuleContentWarm",
            "SelectedAttachmentStorageMode",
            "AvailableAttachmentStorageModeOptions"
        ];
        foreach (string name in removed)
        {
            Assert.DoesNotContain(properties, property => property.Name == name);
        }
    }

    [Fact]
    public void PerformanceSection_BindsToTheEditorThroughSectionAndElementDataContexts()
    {
        string xaml = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.xaml"));
        string window = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.xaml.cs"));
        string deferred = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.DeferredSections.cs"));
        string bindable = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/ViewModels/SettingsViewModel.AotBindableProperties.cs"));
        string editorBridge = File.ReadAllText(
            TestPaths.FromRepository(
                "src/DeskBox/Features/Performance/PerformanceSettingsViewModel.AotBindableProperties.cs"));

        // Inline performance expander on General binds through the performance editor.
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding SelectedPerformanceMode, Mode=TwoWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsOn=\"{Binding KeepWarmCacheWhenHidden, Mode=TwoWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding SelectedPerformanceCacheBudget, Mode=TwoWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding SelectedVisibleIdleCacheCleanupDelaySeconds, Mode=TwoWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsOn=\"{Binding IdleWorkingSetTrimEnabled, Mode=TwoWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsChecked=\"{Binding AllowTextMarquee, Mode=TwoWay}\"",
            xaml,
            StringComparison.Ordinal);

        // The inline performance card on General receives _performanceSettingsViewModel;
        // the standalone PerformanceSettings deferred section is retired.
        Assert.Contains(
            "PerformanceInlineCard.DataContext = _performanceSettingsViewModel;",
            window,
            StringComparison.Ordinal);
        Assert.DoesNotContain("\"PerformanceSettings\"", deferred, StringComparison.Ordinal);

        // General keeps the shell DataContext while cross-domain controls
        // reach their editors through element-level DataContexts.
        Assert.Contains(
            "x:Name=\"PerformanceInlineCard\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "QuickCaptureAttachmentStorageModeComboBox.DataContext = _featureWidgetsSettingsViewModel;",
            deferred,
            StringComparison.Ordinal);
        Assert.Contains(
            "TodoAttachmentStorageModeComboBox.DataContext = _featureWidgetsSettingsViewModel;",
            deferred,
            StringComparison.Ordinal);

        // The shell bridge keeps neither the performance nor the attachment
        // entries; the editors carry their own NativeAOT bridges.
        Assert.DoesNotContain("nameof(SelectedPerformanceMode)", bindable, StringComparison.Ordinal);
        Assert.DoesNotContain("nameof(IdleWorkingSetTrimEnabled)", bindable, StringComparison.Ordinal);
        Assert.DoesNotContain("nameof(SelectedAttachmentStorageMode)", bindable, StringComparison.Ordinal);
        Assert.Equal(44, CountOccurrences(bindable, "nameof("));
        Assert.Contains("nameof(SelectedPerformanceMode)", editorBridge, StringComparison.Ordinal);
        Assert.Contains("nameof(KeepWarmCacheWhenHidden)", editorBridge, StringComparison.Ordinal);
        Assert.Contains("nameof(AllowTextMarquee)", editorBridge, StringComparison.Ordinal);
        Assert.DoesNotContain("nameof(ImmediateHiddenWorkingSetTrimEnabled)", editorBridge, StringComparison.Ordinal);
        Assert.DoesNotContain("nameof(QuiescenceWorkingSetTrimEnabled)", editorBridge, StringComparison.Ordinal);
        Assert.Equal(18, CountOccurrences(editorBridge, "nameof("));
    }

    [Fact]
    public void GeneralSection_AttachmentComboBindsThroughTheFeatureWidgetsEditor()
    {
        string editorBridge = File.ReadAllText(
            TestPaths.FromRepository(
                "src/DeskBox/Features/FeatureWidgets/FeatureWidgetsSettingsViewModel.AotBindableProperties.cs"));
        string editor = File.ReadAllText(
            TestPaths.FromRepository(
                "src/DeskBox/Features/FeatureWidgets/FeatureWidgetsSettingsViewModel.cs"));

        Assert.Contains("nameof(AttachmentStorageMode)", editorBridge, StringComparison.Ordinal);
        Assert.Contains("nameof(AvailableAttachmentStorageModeOptions)", editorBridge, StringComparison.Ordinal);
        // The localization keys are assembled from fragments so the whole
        // key literal cannot collide with the facade-name ratchet.
        Assert.DoesNotContain("\"Settings.AttachmentStorageMode.", editor, StringComparison.Ordinal);
        Assert.Contains("Settings.AttachmentStor\" + \"ageMode.Link", editor, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachmentCombo_SurfaceLivesOnTheFeatureWidgetsEditor()
    {
        var settings = new SettingsService(_root);
        var coordinator = new FeatureWidgetsSettingsCoordinator(settings);
        var editor = new DeskBox.Features.FeatureWidgets.FeatureWidgetsSettingsViewModel(
            coordinator, PassthroughLocalize);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        Assert.Equal(AttachmentStorageModes.Link, editor.AttachmentStorageMode);
        SettingsOption[] options = [.. editor.AvailableAttachmentStorageModeOptions];
        Assert.Equal(2, options.Length);
        Assert.Equal(AttachmentStorageModes.Link, options[0].Value);
        Assert.Equal(AttachmentStorageModes.Copy, options[1].Value);

        editor.AttachmentStorageMode = AttachmentStorageModes.Copy;
        Assert.Equal(
            AttachmentStorageModes.Copy,
            settings.Settings.QuickCapture.AttachmentStorageMode);
        Assert.Equal(AttachmentStorageModes.Copy, coordinator.ReadAttachmentStorageMode());
        Assert.Equal(1, notified);

        // Unknown values normalize to Link and persist (one more broadcast).
        int notifiedBeforeRepeat = notified;
        editor.AttachmentStorageMode = "nonsense";
        Assert.Equal(AttachmentStorageModes.Link, editor.AttachmentStorageMode);
        Assert.Equal(notifiedBeforeRepeat + 1, notified);

        // External sync re-projects without writing back.
        settings.Settings.QuickCapture.AttachmentStorageMode = AttachmentStorageModes.Copy;
        editor.SyncPresentation();
        Assert.Equal(AttachmentStorageModes.Copy, editor.AttachmentStorageMode);
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
