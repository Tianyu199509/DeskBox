using DeskBox.Platform;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Views;

public sealed partial class OnboardingWindow
{
    private const string StatusInfoGlyph = "\uE946";
    private const string StatusCompleteGlyph = "\uE73E";
    private const string StatusHiddenGlyph = "\uE890";
    private const string StatusVisibleGlyph = "\uE8A7";

    private int _summonToggleCount;
    private bool _isSynchronizingDropMode;
    private bool _isSynchronizingDoubleClick;
    private readonly Dictionary<WidgetKind, bool> _featureTileStates = new();

    // ════════════════════════════════════════════════════════════
    //  Step 2: moving changes the path. The drop behavior is edited
    //  inline (FileWidget slice, same normalization as the settings
    //  page); the storage root itself only changes in Settings, which
    //  owns the verified copy-migration flow.
    // ════════════════════════════════════════════════════════════

    private void SetupDropStep()
    {
        // Slice reads (never the AppSettings facade passthroughs — the
        // facade-access contract test pins this file's budget).
        string root = CurrentManagedStorageRoot();
        string subfolder = _localizationService.T("Widget.ManagedFolderBaseName");
        string fullPath = System.IO.Path.Combine(root, subfolder);
        Step2PathValue.Text = fullPath;
        ToolTipService.SetToolTip(Step2PathValue, fullPath);

        string fileName = _localizationService.T("Onboarding.Scene.FileName1");
        string rootTail = PathTail(root);
        string desktopTail = PathTail(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        ScenePathOldText.Text = $"…\\{(string.IsNullOrWhiteSpace(desktopTail) ? "Desktop" : desktopTail)}\\{fileName}";
        ScenePathChipText.Text = string.IsNullOrWhiteSpace(rootTail)
            ? System.IO.Path.Combine(fullPath, fileName)
            : $"…\\{rootTail}\\{subfolder}\\{fileName}";

        bool copy = SynchronizeDropModeControls();
        _dropSceneShowsCopy = copy;
    }

    private string CurrentManagedStorageRoot() =>
        SettingsService.NormalizeManagedStorageRootPath(
            _settingsService.Settings.FileWidget.DefaultManagedStorageRootPath);

    private static string PathTail(string path) =>
        System.IO.Path.GetFileName(path.TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar));

    /// <summary>
    /// Mirrors the persisted drop action into the radio pair and the
    /// explanation line. "Follow Windows" leaves both radios clear (the
    /// scene then tells the Move story, the same-drive Windows default).
    /// Returns whether the current action is Copy.
    /// </summary>
    private bool SynchronizeDropModeControls()
    {
        string action = _settingsService.Settings.FileWidget.ManagedDropAction;
        bool copy = string.Equals(action, SettingsService.ManagedDropActionCopy, StringComparison.Ordinal);
        bool move = string.Equals(action, SettingsService.ManagedDropActionMove, StringComparison.Ordinal);
        _isSynchronizingDropMode = true;
        try
        {
            Step2MoveRadio.IsChecked = move;
            Step2CopyRadio.IsChecked = copy;
        }
        finally
        {
            _isSynchronizingDropMode = false;
        }

        Step2ModeExplainText.Text = copy
            ? _localizationService.T("Onboarding.Step2.CopyExplain")
            : _localizationService.T("Onboarding.Step2.MoveExplain");
        return copy;
    }

    private void Step2DropMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_isSynchronizingDropMode || sender is not RadioButton radio)
        {
            return;
        }

        bool copy = ReferenceEquals(radio, Step2CopyRadio);
        string value = copy
            ? SettingsService.ManagedDropActionCopy
            : SettingsService.ManagedDropActionMove;

        Step2ModeExplainText.Text = copy
            ? _localizationService.T("Onboarding.Step2.CopyExplain")
            : _localizationService.T("Onboarding.Step2.MoveExplain");

        // The stage answers the choice at once: replay the story in the
        // newly selected mode (before persisting, so the settings echo
        // finds the scene already in sync).
        SetDropSceneMode(copy, replay: true);

        FileWidgetSettingsSlice fileWidget = _settingsService.Settings.FileWidget;
        if (!string.Equals(fileWidget.ManagedDropAction, value, StringComparison.Ordinal))
        {
            fileWidget.ManagedDropAction = value;
            _settingsService.SaveDebounced();
        }
    }

    private void Step2OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string root = CurrentManagedStorageRoot();
            System.IO.Directory.CreateDirectory(root);
            Win32Helper.OpenFile(root);
        }
        catch (Exception ex)
        {
            App.Log($"[Onboarding] Failed to open managed storage root: {ex.Message}");
        }
    }

    private void Step2PathChange_Click(object sender, RoutedEventArgs e)
    {
        App.Current.ShowSettings("AppearanceDetail");
    }

    // ════════════════════════════════════════════════════════════
    //  Step 3: summon / dismiss (live, wired to the real toggle path)
    // ════════════════════════════════════════════════════════════

    private void SetupSummonStep()
    {
        string hotkeyText = GlobalHotkeyService.FormatActivation(
            GlobalHotkeyService.NormalizeActivation(
                _settingsService.Settings.GlobalHotkeyActivationKind,
                _settingsService.Settings.GlobalHotkeyModifiers,
                _settingsService.Settings.GlobalHotkeyKey),
            _localizationService);
        Step3BodyText.Text = _localizationService.Format(
            "Onboarding.Step3.Body",
            hotkeyText);
        SceneKeycapText.Text = hotkeyText;
        RefreshSummonStatus();
        SynchronizeDoubleClickToggle();
    }

    private void SynchronizeDoubleClickToggle()
    {
        _isSynchronizingDoubleClick = true;
        try
        {
            Step3DoubleClickToggle.IsOn = _settingsService.Settings.Core.DesktopDoubleClickEnabled;
        }
        finally
        {
            _isSynchronizingDoubleClick = false;
        }
    }

    /// <summary>
    /// Same path as the settings page: the live hook service starts/stops
    /// the listener and persists; a refused start leaves the switch off.
    /// </summary>
    private void Step3DoubleClickToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isSynchronizingDoubleClick)
        {
            return;
        }

        bool requested = Step3DoubleClickToggle.IsOn;
        bool applied;
        if (App.Current.DesktopDoubleClickActivationService is { } service)
        {
            applied = service.TrySetEnabled(requested, out int errorCode);
            if (!applied)
            {
                App.Log($"[Onboarding] Desktop double-click toggle refused enabled={requested} error={errorCode}");
            }
        }
        else
        {
            _settingsService.Settings.Core.DesktopDoubleClickEnabled = requested;
            _settingsService.SaveDebounced();
            applied = true;
        }

        if (!applied)
        {
            SynchronizeDoubleClickToggle();
        }
    }

    private void Step3ChangeHotkey_Click(object sender, RoutedEventArgs e)
    {
        App.Current.ShowSettings("InteractionWindowSettings");
    }

    private void RefreshSummonStatus()
    {
        if (Step3StatusText is null)
        {
            return;
        }

        bool hasVisible = App.Current.HasVisibleWidgetsForOnboarding;
        string key;
        string glyph;
        if (_hasCompletedSummonPractice)
        {
            key = "Onboarding.Step3.StatusDone";
            glyph = StatusCompleteGlyph;
        }
        else if (_hasToggledDuringSummonStep)
        {
            key = hasVisible
                ? "Onboarding.Step3.StatusShown"
                : "Onboarding.Step3.StatusHidden";
            glyph = hasVisible ? StatusVisibleGlyph : StatusHiddenGlyph;
        }
        else
        {
            key = "Onboarding.Step3.StatusReady";
            glyph = StatusInfoGlyph;
        }

        Step3StatusIcon.Glyph = glyph;
        Step3StatusText.Text = _localizationService.T(key);
    }

    private async void Step3Try_Click(object sender, RoutedEventArgs e)
    {
        Step3TryButton.IsEnabled = false;
        try
        {
            await App.Current.ToggleWidgetsForOnboardingAsync();
        }
        finally
        {
            Step3TryButton.IsEnabled = true;
        }
    }

    private void OnOnboardingWidgetsVisibilityChanged(bool hasVisibleWidgets)
    {
        if (_isClosed)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosed || _stepIndex != 2)
            {
                return;
            }

            _hasToggledDuringSummonStep = true;
            _summonToggleCount++;
            if (_summonToggleCount >= 2)
            {
                _hasCompletedSummonPractice = true;
            }

            RefreshSummonStatus();

            // The stage stops demonstrating and mirrors the real desktop:
            // the mini widgets follow the actual state, the keycap answers.
            MirrorSummonScene(hasVisibleWidgets);
        });
    }

    // ════════════════════════════════════════════════════════════
    //  Step 4: the tray menu, opened for real, plus the settings
    //  people reach for first (current value + deep link)
    // ════════════════════════════════════════════════════════════

    private void Step4OpenMenu_Click(object sender, RoutedEventArgs e)
    {
        App.Current.ShowTrayContextMenuForOnboarding();
    }

    private void SetupTrayStep()
    {
        Step4CapsuleValue.Text = _settingsService.Settings.WidgetShell.WidgetCollapseBehavior switch
        {
            "Click" => _localizationService.T("Onboarding.Quick.Collapse.Click"),
            "Smart" => _localizationService.T("Onboarding.Quick.Collapse.Smart"),
            _ => _localizationService.T("Onboarding.Quick.Collapse.Expanded"),
        };
        Step4AppearanceValue.Text = _settingsService.Settings.WidgetShell.WidgetMaterialType switch
        {
            "MicaAlt" => _localizationService.T("Onboarding.Quick.Material.MicaAlt"),
            "Acrylic" => _localizationService.T("Onboarding.Quick.Material.Acrylic"),
            "AcrylicBase" => _localizationService.T("Onboarding.Quick.Material.AcrylicBase"),
            "Solid" => _localizationService.T("Onboarding.Quick.Material.Solid"),
            _ => _localizationService.T("Onboarding.Quick.Material.Mica"),
        };
        Step4AutoStartValue.Text = _settingsService.Settings.Core.AutoStart
            ? _localizationService.T("Common.On")
            : _localizationService.T("Common.Off");
    }

    private void Step4QuickSetting_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string sectionTag } &&
            !string.IsNullOrWhiteSpace(sectionTag))
        {
            App.Current.ShowSettings(sectionTag);
        }
    }

    // ════════════════════════════════════════════════════════════
    //  Step 5: optional feature widgets (persist immediately); the
    //  stage tiles mirror every switch
    // ════════════════════════════════════════════════════════════

    private void SetupFeatureStep()
    {
        if (_hasInitializedFeatureToggles)
        {
            return;
        }

        SynchronizeFeatureTogglesFromSettings();
    }

    private bool IsFeatureEnabled(WidgetKind kind) =>
        FeatureWidgetSettings.IsEnabled(_settingsService.Settings, kind);

    private void PlayFeatureStepEntrance()
    {
        foreach (WidgetKind kind in SceneFeatureKinds)
        {
            _featureTileStates[kind] = IsFeatureEnabled(kind);
        }

        PlayFeatureTilesEntrance(kind => _featureTileStates[kind]);
    }

    /// <summary>
    /// Brings the stage tiles in line with the persisted feature switches.
    /// Animated passes only touch tiles whose state actually changed, so a
    /// settings echo never replays a pop the user already saw.
    /// </summary>
    private void ApplyAllFeatureTileStates(bool animate)
    {
        foreach (WidgetKind kind in SceneFeatureKinds)
        {
            bool enabled = IsFeatureEnabled(kind);
            if (animate &&
                _featureTileStates.TryGetValue(kind, out bool previous) &&
                previous == enabled)
            {
                continue;
            }

            _featureTileStates[kind] = enabled;
            ApplyFeatureTileState(kind, enabled, animate);
        }
    }

    private void SetFeatureTogglesEnabled(bool enabled)
    {
        Step5TodoToggle.IsEnabled = enabled;
        Step5QuickCaptureToggle.IsEnabled = enabled;
        Step5ClipboardToggle.IsEnabled = enabled;
        Step5WeatherToggle.IsEnabled = enabled;
        Step5MusicToggle.IsEnabled = enabled;
        Step5GlanceToggle.IsEnabled = enabled;
    }

    private void Step5FeatureToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_hasInitializedFeatureToggles ||
            _isSynchronizingFeatureToggles ||
            sender is not ToggleSwitch { Tag: string kindName } toggle ||
            !Enum.TryParse(kindName, ignoreCase: false, out WidgetKind kind) ||
            !FeatureWidgetSettings.IsFeatureWidget(kind))
        {
            return;
        }

        if (_stepIndex == 4)
        {
            _featureTileStates[kind] = toggle.IsOn;
            ApplyFeatureTileState(kind, toggle.IsOn, animate: true);
        }

        _featureWidgetSelectionUpdateTask = PersistFeatureWidgetSelectionAfterAsync(
            _featureWidgetSelectionUpdateTask,
            kind,
            toggle.IsOn);
    }

    private async Task PersistFeatureWidgetSelectionAfterAsync(
        Task previousUpdate,
        WidgetKind kind,
        bool enabled)
    {
        try
        {
            await previousUpdate;

            if (App.Current.WidgetManager is { } widgetManager)
            {
                await widgetManager.SetFeatureWidgetEnabledAsync(
                    kind,
                    enabled,
                    reveal: enabled);
                return;
            }

            FeatureWidgetSettings.SetEnabled(_settingsService.Settings, kind, enabled);
            await _settingsService.SaveAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[Onboarding] Failed to persist feature selection kind={kind} enabled={enabled}: {ex}");
            FeatureWidgetSettings.SetEnabled(_settingsService.Settings, kind, enabled);
            await _settingsService.SaveAsync();
        }
    }

    private void OnFeatureWidgetSettingsChanged()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(OnFeatureWidgetSettingsChanged);
            return;
        }

        if (_isClosed || !_hasLoaded)
        {
            return;
        }

        if (_hasInitializedFeatureToggles)
        {
            SynchronizeFeatureTogglesFromSettings();
        }

        // Keep the inline controls honest when the same values change in
        // Settings (or another process) while the guide is open.
        bool copy = SynchronizeDropModeControls();
        if (copy != _dropSceneShowsCopy)
        {
            SetDropSceneMode(copy, replay: true);
        }

        SynchronizeDoubleClickToggle();
        if (_stepIndex == 3)
        {
            SetupTrayStep();
        }

        if (_stepIndex == 4)
        {
            ApplyAllFeatureTileStates(animate: true);
        }
    }

    private void SynchronizeFeatureTogglesFromSettings()
    {
        _isSynchronizingFeatureToggles = true;
        try
        {
            Step5TodoToggle.IsOn = FeatureWidgetSettings.IsEnabled(
                _settingsService.Settings,
                WidgetKind.Todo);
            Step5QuickCaptureToggle.IsOn = FeatureWidgetSettings.IsEnabled(
                _settingsService.Settings,
                WidgetKind.QuickCapture);
            Step5ClipboardToggle.IsOn = FeatureWidgetSettings.IsEnabled(
                _settingsService.Settings,
                WidgetKind.Clipboard);
            Step5WeatherToggle.IsOn = FeatureWidgetSettings.IsEnabled(
                _settingsService.Settings,
                WidgetKind.Weather);
            Step5MusicToggle.IsOn = FeatureWidgetSettings.IsEnabled(
                _settingsService.Settings,
                WidgetKind.Music);
            Step5GlanceToggle.IsOn = FeatureWidgetSettings.IsEnabled(
                _settingsService.Settings,
                WidgetKind.Glance);
            _hasInitializedFeatureToggles = true;
        }
        finally
        {
            _isSynchronizingFeatureToggles = false;
        }
    }
}
