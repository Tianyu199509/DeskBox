using DeskBox.Contracts;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Views.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.System;

namespace DeskBox.Views.SettingsSections;

/// <summary>
/// Settings section for the first-party Clipboard feature widget: capture types,
/// quick-paste shortcut, click/paste behavior, privacy &amp; secret protection,
/// excluded apps, and cache upkeep.
/// </summary>
public sealed partial class ClipboardSettingsSection : UserControl
{
    private const string CardStyleMinimalist = "Minimalist";
    private const string CardStyleCompact = "Compact";
    private const string CardStyleStandard = "Standard";
    private const string CardStyleSpacious = "Spacious";

    private SettingsService? _settingsService;
    private LocalizationService? _localization;
    private ISettingsWidgetActions? _widgetActions;
    private ISettingsDataActions? _dataActions;
    private ISettingsHotkeyActions? _hotkeyActions;
    private nint _ownerWindow;
    private bool _isLoading;
    private bool _isSavingSlice;
    private bool _observingHotkey;
    private bool _observingSettings;
    private bool _clipboardHotkeyRegistered;
    private string? _clipboardHotkeyError;
    private int _statsRefreshGeneration;
    private int _hotkeyRefreshGeneration;
    private IReadOnlyList<ClipboardObservedApp> _observedApps = Array.Empty<ClipboardObservedApp>();

    public ClipboardSettingsSection()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) =>
        {
            if (IsLoaded)
            {
                RebuildExcludedAppLists();
            }
        };
    }

    public event EventHandler? ResetClipboardRequested;

    public void Configure(
        SettingsService settingsService,
        LocalizationService localizationService,
        ISettingsWidgetActions widgetActions,
        ISettingsDataActions dataActions,
        nint ownerWindow,
        ISettingsHotkeyActions? hotkeyActions = null)
    {
        UnsubscribeHotkeyEvents();
        UnsubscribeSettingsEvents();
        _settingsService = settingsService;
        _localization = localizationService;
        _widgetActions = widgetActions;
        _dataActions = dataActions;
        _hotkeyActions = hotkeyActions;
        _ownerWindow = ownerWindow;
        if (IsLoaded)
        {
            SubscribeHotkeyEvents();
            SubscribeSettingsEvents();
        }

        RefreshFromSettings();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SettingsWindow.ApplyNoticeMessageTypography(ClipboardHotkeyReservedWarning);
        SubscribeHotkeyEvents();
        SubscribeSettingsEvents();
        RefreshFromSettings();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeHotkeyEvents();
        UnsubscribeSettingsEvents();
        _statsRefreshGeneration++;
        _hotkeyRefreshGeneration++;
    }

    private void SubscribeSettingsEvents()
    {
        if (_observingSettings || _settingsService is null)
        {
            return;
        }

        _settingsService.SettingsChanged += OnExternalSettingsChanged;
        _observingSettings = true;
    }

    private void UnsubscribeSettingsEvents()
    {
        if (!_observingSettings || _settingsService is null)
        {
            return;
        }

        _settingsService.SettingsChanged -= OnExternalSettingsChanged;
        _observingSettings = false;
    }

    private void OnExternalSettingsChanged()
    {
        if (_isSavingSlice)
        {
            return;
        }

        if (DispatcherQueue is { HasThreadAccess: false } dq)
        {
            dq.TryEnqueue(() =>
            {
                if (IsLoaded && !_isLoading && !_isSavingSlice)
                {
                    RefreshFromSettings();
                }
            });
            return;
        }

        if (IsLoaded && !_isLoading)
        {
            RefreshFromSettings();
        }
    }

    private void SubscribeHotkeyEvents()
    {
        if (_observingHotkey || _hotkeyActions is null)
        {
            return;
        }

        _hotkeyActions.RegistrationChanged += OnHotkeyRegistrationChanged;
        _observingHotkey = true;
    }

    private void UnsubscribeHotkeyEvents()
    {
        if (!_observingHotkey || _hotkeyActions is null)
        {
            return;
        }

        _hotkeyActions.RegistrationChanged -= OnHotkeyRegistrationChanged;
        _observingHotkey = false;
    }

    private void OnHotkeyRegistrationChanged()
    {
        if (DispatcherQueue is { HasThreadAccess: false } dq)
        {
            dq.TryEnqueue(() => _ = RefreshClipboardHotkeyRegistrationStateAsync());
            return;
        }

        _ = RefreshClipboardHotkeyRegistrationStateAsync();
    }

    public void RefreshFromSettings()
    {
        if (_settingsService is null || _localization is null || _widgetActions is null)
        {
            return;
        }

        _isLoading = true;
        try
        {
            ClipboardSettingsSlice slice = _settingsService.Settings.Clipboard;
            EnableClipboardToggle.IsOn = _widgetActions.IsFeatureWidgetEnabled(WidgetKind.Clipboard);
            CaptureTextCheckBox.IsChecked = slice.ClipboardCaptureText;
            CaptureImagesCheckBox.IsChecked = slice.ClipboardCaptureImages;
            CaptureFilesCheckBox.IsChecked = slice.ClipboardCaptureFiles;
            SelectByTag(MaxImageSizeComboBox, slice.ClipboardMaxImageSizeMb.ToString());
            SelectByTag(PopupPlacementComboBox, slice.ClipboardPopupPlacementMode);
            SelectByTag(DoubleCopyComboBox, slice.ClipboardDoubleCopyAction);
            SelectByTag(ClickBehaviorComboBox, slice.ClipboardClickBehavior);
            DefaultPlainTextToggle.IsOn = slice.ClipboardDefaultPastePlainText;
            MoveToTopToggle.IsOn = slice.ClipboardMoveToTopOnReuse;
            SelectByTag(CardStyleComboBox, ResolveCardStyleTag(slice));
            SelectByTag(TabDisplayModeComboBox, ClipboardSettingsSlice.NormalizeTabDisplayMode(slice.ClipboardTabDisplayMode));
            ShowSourceAppToggle.IsOn = slice.ClipboardShowSourceApp;
            IgnorePrivacyFormatsToggle.IsOn = slice.ClipboardIgnoreSystemPrivacyFormats;
            SelectByTag(SecretProtectionComboBox, slice.ClipboardSensitiveProtectionMode);
            SelectByTag(MaxItemsComboBox, slice.ClipboardMaxItems.ToString());
            SelectByTag(RetentionDaysComboBox, slice.ClipboardRetentionDays.ToString());
            ApplyFeatureEnableGating(EnableClipboardToggle.IsOn);
            RebuildExcludedAppLists();
        }
        finally
        {
            _isLoading = false;
        }

        _ = RefreshClipboardHotkeyRegistrationStateAsync();
        _ = RefreshRuntimeStatsAndObservedAppsAsync();
    }

    private static string ResolveCardStyleTag(ClipboardSettingsSlice slice)
    {
        if (string.Equals(
                slice.ClipboardViewMode,
                ClipboardSettingsSlice.ViewModeCompact,
                StringComparison.OrdinalIgnoreCase))
        {
            return CardStyleMinimalist;
        }

        if (string.Equals(
                slice.ClipboardCardDensity,
                ClipboardSettingsSlice.DensityCompact,
                StringComparison.OrdinalIgnoreCase))
        {
            return CardStyleCompact;
        }

        if (string.Equals(
                slice.ClipboardCardDensity,
                ClipboardSettingsSlice.DensityRelaxed,
                StringComparison.OrdinalIgnoreCase))
        {
            return CardStyleSpacious;
        }

        return CardStyleStandard;
    }

    private async Task RefreshRuntimeStatsAndObservedAppsAsync()
    {
        if (_dataActions is null || _localization is null)
        {
            return;
        }

        int generation = ++_statsRefreshGeneration;
        try
        {
            ClipboardStorageStats? stats = await _dataActions.GetClipboardStorageStatsAsync();
            if (generation != _statsRefreshGeneration)
            {
                return;
            }

            if (stats is null)
            {
                StorageSummaryCard.Description = _localization.T("Settings.Clipboard.StorageSummary.Empty");
                ClearUnpinnedButton.IsEnabled = false;
                _observedApps = Array.Empty<ClipboardObservedApp>();
                RebuildExcludedAppLists();
                return;
            }

            int unpinnedCount = Math.Max(0, stats.ActiveItemCount - stats.PinnedItemCount);
            ClearUnpinnedButton.IsEnabled = EnableClipboardToggle.IsOn && unpinnedCount > 0;
            StorageSummaryCard.Description = stats.ActiveItemCount == 0 && stats.TotalCacheBytes == 0
                ? _localization.T("Settings.Clipboard.StorageSummary.Empty")
                : _localization.Format(
                    "Settings.Clipboard.StorageSummary.Format",
                    stats.ActiveItemCount,
                    stats.PinnedItemCount,
                    FormatBytes(stats.TotalCacheBytes));

            _observedApps = stats.ObservedApps ?? [];
            RebuildExcludedAppLists();
        }
        catch
        {
            if (generation == _statsRefreshGeneration)
            {
                StorageSummaryCard.Description = _localization.T("Settings.Clipboard.StorageSummary.Empty");
            }
        }
    }

    private void RebuildExcludedAppLists()
    {
        if (_settingsService is null || _localization is null)
        {
            return;
        }

        List<string> excluded = _settingsService.Settings.Clipboard.ClipboardExcludedApps
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .ToList();

        ExcludedAppsListPanel.Children.Clear();
        ExcludedAppsEmptyText.Visibility = excluded.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ExcludedAppsListPanel.Visibility = excluded.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        Microsoft.UI.Xaml.Media.Brush secondaryBrush =
            NeutralInteractionBrush.ResolveThemedResource("TextFillColorSecondaryBrush", this) ??
            new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x9E, 0x9E, 0x9E));

        foreach (string entry in excluded)
        {
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string fileName = Path.GetFileName(entry);
            var textStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(fileName) ? entry : fileName,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            if (!string.IsNullOrWhiteSpace(fileName) &&
                !string.Equals(fileName, entry, StringComparison.OrdinalIgnoreCase))
            {
                textStack.Children.Add(new TextBlock
                {
                    Text = entry,
                    FontSize = 11,
                    Foreground = secondaryBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }

            var removeButton = new Button
            {
                Tag = entry,
                Style = (Style)Resources["IconActionButtonStyle"],
                Content = new FontIcon { Glyph = "\uE711", FontSize = 12 }
            };
            ToolTipService.SetToolTip(removeButton, _localization.T("Common.Delete"));
            AutomationProperties.SetName(
                removeButton,
                _localization.T("Common.Delete"));
            removeButton.Click += RemoveExcludedAppButton_Click;

            Grid.SetColumn(textStack, 0);
            Grid.SetColumn(removeButton, 1);
            row.Children.Add(textStack);
            row.Children.Add(removeButton);
            ExcludedAppsListPanel.Children.Add(row);
        }

        ObservedAppsListPanel.Children.Clear();
        ObservedAppsEmptyText.Visibility = _observedApps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ObservedAppsListPanel.Visibility = _observedApps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (ClipboardObservedApp observed in _observedApps)
        {
            string exeName = Path.GetFileName(observed.AppPath);
            bool isChecked = excluded.Any(item =>
                string.Equals(item, observed.AppPath, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(exeName) &&
                 string.Equals(Path.GetFileName(item), exeName, StringComparison.OrdinalIgnoreCase)));

            string displayLabel = string.IsNullOrWhiteSpace(observed.AppName)
                ? (string.IsNullOrWhiteSpace(exeName) ? observed.AppPath : exeName)
                : (string.IsNullOrWhiteSpace(exeName) ||
                   string.Equals(observed.AppName, exeName, StringComparison.OrdinalIgnoreCase)
                    ? observed.AppName
                    : $"{observed.AppName} ({exeName})");

            var checkBox = new CheckBox
            {
                Content = displayLabel,
                Tag = observed.AppPath,
                IsChecked = isChecked,
                MinWidth = 0
            };
            ToolTipService.SetToolTip(checkBox, observed.AppPath);
            checkBox.Click += ObservedAppCheckBox_Click;
            ObservedAppsListPanel.Children.Add(checkBox);
        }
    }

    private async void EnableClipboardToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading || _widgetActions is null || _settingsService is null)
        {
            return;
        }

        bool desired = EnableClipboardToggle.IsOn;
        FeatureWidgetSettings.SetEnabled(_settingsService.Settings, WidgetKind.Clipboard, desired);
        ApplyFeatureEnableGating(desired);
        try
        {
            bool applied = await _widgetActions.SetFeatureWidgetEnabled(WidgetKind.Clipboard, desired);
            if (applied != desired)
            {
                _isLoading = true;
                try
                {
                    FeatureWidgetSettings.SetEnabled(_settingsService.Settings, WidgetKind.Clipboard, applied);
                    EnableClipboardToggle.IsOn = applied;
                }
                finally
                {
                    _isLoading = false;
                }

                ApplyFeatureEnableGating(applied);
            }

            await RefreshClipboardHotkeyRegistrationStateAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardSettings] Toggle clipboard widget failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Grays the capture/behavior/privacy/storage cards while the clipboard
    /// widget is disabled (batch 1 gating fix): editing them with the feature
    /// off had no visible effect, which read as a broken page. Panels are not
    /// Controls (no IsEnabled), so the direct card children are gated
    /// instead — same semantics as the Quick Capture per-card bindings.
    /// </summary>
    private void ApplyFeatureEnableGating(bool enabled)
    {
        foreach (UIElement child in ClipboardSettingsBodyPanel.Children)
        {
            if (ReferenceEquals(child, QuickPastePopupExpander) ||
                ReferenceEquals(child, ClipboardHotkeyReservedWarning))
            {
                continue;
            }

            if (child is Control control)
            {
                control.IsEnabled = enabled;
            }
            else
            {
                child.Opacity = enabled ? 1.0 : 0.45;
            }
        }

        ApplyDependentControlGating();
        RefreshClipboardHotkeyControls();
    }

    private void ApplyDependentControlGating()
    {
        bool featureEnabled = EnableClipboardToggle.IsOn;
        bool isMinimalist =
            CardStyleComboBox.SelectedItem is ComboBoxItem { Tag: string styleTag } &&
            string.Equals(styleTag, CardStyleMinimalist, StringComparison.OrdinalIgnoreCase);
        ShowSourceAppCard.IsEnabled = featureEnabled && !isMinimalist;
        ShowSourceAppToggle.IsEnabled = featureEnabled && !isMinimalist;

        bool captureImages = CaptureImagesCheckBox.IsChecked == true;
        MaxImageSizeCard.IsEnabled = featureEnabled && captureImages;
        MaxImageSizeComboBox.IsEnabled = featureEnabled && captureImages;

        ResetClipboardButton.IsEnabled = featureEnabled && (_widgetActions?.IsHostAvailable ?? false);
    }

    private async Task RefreshClipboardHotkeyRegistrationStateAsync()
    {
        if (_settingsService is null || _localization is null)
        {
            return;
        }

        int generation = ++_hotkeyRefreshGeneration;
        bool registered = false;
        if (_hotkeyActions is not null)
        {
            try
            {
                registered = await _hotkeyActions.IsClipboardHotkeyRegisteredAsync();
            }
            catch
            {
                registered = false;
            }
        }

        if (generation != _hotkeyRefreshGeneration)
        {
            return;
        }

        _clipboardHotkeyRegistered = registered;
        if (registered)
        {
            _clipboardHotkeyError = null;
        }

        RefreshClipboardHotkeyControls();
    }

    private void RefreshClipboardHotkeyControls()
    {
        if (_settingsService is null || _localization is null)
        {
            return;
        }

        ClipboardSettingsSlice slice = _settingsService.Settings.Clipboard;
        bool featureEnabled = EnableClipboardToggle.IsOn;
        bool hotkeyAvailable = featureEnabled && _hotkeyActions is not null;
        bool hotkeyEnabled = slice.ClipboardHotkeyEnabled;

        GlobalHotkeyActivation effectiveActivation = GlobalHotkeyService.NormalizeActivation(
            slice.ClipboardHotkeyActivationKind,
            (int)slice.ClipboardHotkeyModifiers,
            slice.ClipboardHotkeyKey);
        string displayText = GlobalHotkeyService.FormatActivation(effectiveActivation, _localization);

        _isLoading = true;
        try
        {
            ClipboardHotkeyToggle.IsEnabled = hotkeyAvailable;
            ClipboardHotkeyCaptureButton.IsEnabled = hotkeyAvailable;
            ClipboardHotkeyToggle.IsOn = hotkeyEnabled && hotkeyAvailable;

            bool popupPlacementEnabled = hotkeyAvailable && hotkeyEnabled;
            PopupPlacementCard.IsEnabled = popupPlacementEnabled;
            PopupPlacementComboBox.IsEnabled = popupPlacementEnabled;
            DoubleCopyCard.IsEnabled = featureEnabled;
            DoubleCopyComboBox.IsEnabled = featureEnabled;
        }
        finally
        {
            _isLoading = false;
        }

        KeyVisualState visualState = hotkeyAvailable && hotkeyEnabled && !_clipboardHotkeyRegistered
            ? KeyVisualState.Warning
            : KeyVisualState.Normal;
        ClipboardHotkeyCaptureButton.Content = KeyVisualControl.CreateInlineButtonContent(
            effectiveActivation,
            displayText,
            showEditIcon: true,
            state: visualState,
            doubleTapBadgeText: _localization.T("Settings.HotkeyRecorder.DoubleTapBadge"));
        AutomationProperties.SetName(ClipboardHotkeyCaptureButton, displayText);

        QuickPastePopupExpander.Description = !hotkeyAvailable
            ? _localization.T("Settings.Clipboard.QuickPasteHotkey.Status.Disabled")
            : _clipboardHotkeyError ?? _localization.T(
                !hotkeyEnabled ? "Settings.Clipboard.QuickPasteHotkey.Status.Disabled" :
                _clipboardHotkeyRegistered ? "Settings.Clipboard.QuickPasteHotkey.Status.Active" :
                "Settings.Clipboard.QuickPasteHotkey.Status.Failed");

        UpdateReservedHotkeyWarning(effectiveActivation, displayText, hotkeyAvailable && hotkeyEnabled);
    }

    private void UpdateReservedHotkeyWarning(
        GlobalHotkeyActivation activation,
        string displayText,
        bool active)
    {
        if (_localization is null)
        {
            return;
        }

        bool isReserved = active &&
            (activation.Kind == HotkeyActivationKind.WindowsTap ||
             activation.IsStandaloneSpaceDoubleTap ||
             (activation.Kind is HotkeyActivationKind.Chord or HotkeyActivationKind.DoubleTap &&
              !activation.IsModifierOnlyDoubleTap &&
              GlobalHotkeyService.IsReservedSystemGesture(activation.Gesture)));

        if (!isReserved)
        {
            ClipboardHotkeyReservedWarning.IsOpen = false;
            ClipboardHotkeyReservedWarning.Visibility = Visibility.Collapsed;
            return;
        }

        string warningMessage = activation.Kind == HotkeyActivationKind.WindowsTap
            ? _localization.T("Settings.GlobalHotkey.WindowsTapWarning")
            : activation.IsStandaloneSpaceDoubleTap
                ? _localization.T("Settings.GlobalHotkey.DoubleSpaceWarning")
                : activation.Kind is HotkeyActivationKind.Chord or HotkeyActivationKind.DoubleTap &&
                  !activation.IsModifierOnlyDoubleTap &&
                  activation.Gesture.Modifiers == HotkeyModifierKeys.Alt &&
                  activation.Gesture.VirtualKey == (int)VirtualKey.Space
                    ? _localization.T("Settings.GlobalHotkey.AltSpaceWarning")
                    : _localization.T("Settings.GlobalHotkey.ReservedWarning");

        ClipboardHotkeyReservedWarning.Title = displayText;
        ClipboardHotkeyReservedWarning.Message = warningMessage;
        ClipboardHotkeyReservedWarning.Visibility = Visibility.Visible;
        ClipboardHotkeyReservedWarning.IsOpen = true;
    }

    private async void ClipboardHotkeyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading || _settingsService is null)
        {
            return;
        }

        bool enabled = ClipboardHotkeyToggle.IsOn;
        _clipboardHotkeyError = null;
        _hotkeyActions?.SetClipboardHotkeyEnabled(enabled);
        await SaveSliceMutationAsync(slice => slice.ClipboardHotkeyEnabled = enabled);
        await RefreshClipboardHotkeyRegistrationStateAsync();
    }

    private async void ClipboardHotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null || _settingsService is null || _localization is null || _hotkeyActions is null)
        {
            return;
        }

        ClipboardSettingsSlice slice = _settingsService.Settings.Clipboard;
        GlobalHotkeyActivation current = GlobalHotkeyService.NormalizeActivation(
            slice.ClipboardHotkeyActivationKind,
            (int)slice.ClipboardHotkeyModifiers,
            slice.ClipboardHotkeyKey);

        HotkeyRecorderDialog.Preset[] presets =
        [
            new(
                "Alt + V",
                GlobalHotkeyActivation.FromChord(new GlobalHotkeyGesture(
                    HotkeyModifierKeys.Alt,
                    ClipboardSettingsSlice.DefaultHotkeyKey))),
            new(
                "Alt + `",
                GlobalHotkeyActivation.FromChord(new GlobalHotkeyGesture(
                    HotkeyModifierKeys.Alt,
                    0xC0))),
            new(
                "Ctrl + Shift + V",
                GlobalHotkeyActivation.FromChord(new GlobalHotkeyGesture(
                    HotkeyModifierKeys.Control | HotkeyModifierKeys.Shift,
                    ClipboardSettingsSlice.DefaultHotkeyKey))),
            new(
                _localization.T("Settings.GlobalHotkey.Preset.DoubleControl"),
                new GlobalHotkeyActivation(
                    HotkeyActivationKind.DoubleControl,
                    new GlobalHotkeyGesture(HotkeyModifierKeys.Control, 0))),
            new(
                _localization.T("Settings.GlobalHotkey.Preset.DoubleShift"),
                GlobalHotkeyActivation.FromDoubleTapModifier(HotkeyModifierKeys.Shift))
        ];

        var dialog = new HotkeyRecorderDialog(
            XamlRoot,
            _localization,
            _hotkeyActions,
            HotkeyRecorderDialog.Scope.Clipboard,
            current,
            presets,
            ApplyClipboardHotkeyFromRecorderAsync,
            GetClipboardHotkeyRecorderConflict);
        await dialog.ShowAsync();
        await RefreshClipboardHotkeyRegistrationStateAsync();
    }

    private async Task<string?> ApplyClipboardHotkeyFromRecorderAsync(GlobalHotkeyActivation activation)
    {
        if (_hotkeyActions is null || _settingsService is null || _localization is null)
        {
            return _localization?.T("Settings.Clipboard.QuickPasteHotkey.Status.Failed");
        }

        GlobalHotkeyActivation normalized = GlobalHotkeyService.NormalizeActivation(
            activation.Kind,
            (int)activation.Gesture.Modifiers,
            activation.Gesture.VirtualKey);
        (bool applied, string? applyError) = await _hotkeyActions.TryApplyClipboardHotkeyActivationAsync(normalized);
        if (!applied)
        {
            _clipboardHotkeyError = applyError ?? _localization.T("Settings.Clipboard.QuickPasteHotkey.Status.Failed");
            RefreshClipboardHotkeyControls();
            return _clipboardHotkeyError;
        }

        _clipboardHotkeyError = null;
        _clipboardHotkeyRegistered = _settingsService.Settings.Clipboard.ClipboardHotkeyEnabled;
        await SaveSliceMutationAsync(slice =>
        {
            slice.ClipboardHotkeyActivationKind = normalized.Kind;
            slice.ClipboardHotkeyModifiers = (int)normalized.Gesture.Modifiers;
            slice.ClipboardHotkeyKey = normalized.Gesture.VirtualKey;
        });
        await RefreshClipboardHotkeyRegistrationStateAsync();
        return null;
    }

    private string? GetClipboardHotkeyRecorderConflict(GlobalHotkeyActivation activation)
    {
        if (_settingsService is null || _localization is null)
        {
            return null;
        }

        AppSettings settings = _settingsService.Settings;
        if (settings.Core.GlobalHotkeyEnabled)
        {
            GlobalHotkeyActivation mainActivation = GlobalHotkeyService.NormalizeActivation(
                settings.Core.GlobalHotkeyActivationKind,
                settings.Core.GlobalHotkeyModifiers,
                settings.Core.GlobalHotkeyKey);
            if (GlobalHotkeyService.DoActivationsConflict(activation, mainActivation))
            {
                return _localization.T("Settings.Clipboard.Hotkey.Status.GlobalHotkeyConflict");
            }
        }

        if (settings.Search.SearchHotkeyEnabled)
        {
            GlobalHotkeyActivation searchActivation = GlobalHotkeyService.NormalizeActivation(
                settings.Search.SearchHotkeyActivationKind,
                settings.Search.SearchHotkeyModifiers,
                settings.Search.SearchHotkeyKey);
            if (GlobalHotkeyService.DoActivationsConflict(activation, searchActivation))
            {
                return _localization.T("Settings.Clipboard.Hotkey.Status.SearchHotkeyConflict");
            }
        }

        return null;
    }

    private async void PopupPlacementComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || PopupPlacementComboBox.SelectedItem is not ComboBoxItem { Tag: string mode })
        {
            return;
        }

        await SaveSliceMutationAsync(slice => slice.ClipboardPopupPlacementMode = mode);
    }

    private async void DoubleCopyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || DoubleCopyComboBox.SelectedItem is not ComboBoxItem { Tag: string action })
        {
            return;
        }

        await SaveSliceMutationAsync(slice => slice.ClipboardDoubleCopyAction = action);
    }

    private async void CaptureTextCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        bool isChecked = CaptureTextCheckBox.IsChecked == true;
        await SaveSliceMutationAsync(slice => slice.ClipboardCaptureText = isChecked);
    }

    private async void CaptureImagesCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        bool isChecked = CaptureImagesCheckBox.IsChecked == true;
        ApplyDependentControlGating();
        await SaveSliceMutationAsync(slice => slice.ClipboardCaptureImages = isChecked);
    }

    private async void CaptureFilesCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        bool isChecked = CaptureFilesCheckBox.IsChecked == true;
        await SaveSliceMutationAsync(slice => slice.ClipboardCaptureFiles = isChecked);
    }

    private async void MaxImageSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || MaxImageSizeComboBox.SelectedItem is not ComboBoxItem { Tag: string raw } ||
            !int.TryParse(raw, out int mb))
        {
            return;
        }

        await SaveSliceMutationAsync(slice => slice.ClipboardMaxImageSizeMb = mb);
    }

    private async void ClickBehaviorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || ClickBehaviorComboBox.SelectedItem is not ComboBoxItem { Tag: string mode })
        {
            return;
        }

        await SaveSliceMutationAsync(slice => slice.ClipboardClickBehavior = mode);
    }

    private async void DefaultPlainTextToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        await SaveSliceMutationAsync(slice => slice.ClipboardDefaultPastePlainText = DefaultPlainTextToggle.IsOn);
    }

    private async void MoveToTopToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        await SaveSliceMutationAsync(slice => slice.ClipboardMoveToTopOnReuse = MoveToTopToggle.IsOn);
    }

    private async void CardStyleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || CardStyleComboBox.SelectedItem is not ComboBoxItem { Tag: string styleTag })
        {
            return;
        }

        ApplyDependentControlGating();
        await SaveSliceMutationAsync(slice =>
        {
            switch (styleTag)
            {
                case CardStyleMinimalist:
                    slice.ClipboardViewMode = ClipboardSettingsSlice.ViewModeCompact;
                    break;
                case CardStyleCompact:
                    slice.ClipboardViewMode = ClipboardSettingsSlice.ViewModeStandard;
                    slice.ClipboardCardDensity = ClipboardSettingsSlice.DensityCompact;
                    break;
                case CardStyleSpacious:
                    slice.ClipboardViewMode = ClipboardSettingsSlice.ViewModeStandard;
                    slice.ClipboardCardDensity = ClipboardSettingsSlice.DensityRelaxed;
                    break;
                default:
                    slice.ClipboardViewMode = ClipboardSettingsSlice.ViewModeStandard;
                    slice.ClipboardCardDensity = ClipboardSettingsSlice.DensityStandard;
                    break;
            }
        });
    }

    private async void TabDisplayModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || TabDisplayModeComboBox.SelectedItem is not ComboBoxItem { Tag: string modeTag })
        {
            return;
        }

        string normalized = ClipboardSettingsSlice.NormalizeTabDisplayMode(modeTag);
        await SaveSliceMutationAsync(slice => slice.ClipboardTabDisplayMode = normalized);
    }

    private async void ShowSourceAppToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        await SaveSliceMutationAsync(slice => slice.ClipboardShowSourceApp = ShowSourceAppToggle.IsOn);
    }

    private async void IgnorePrivacyFormatsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        await SaveSliceMutationAsync(slice => slice.ClipboardIgnoreSystemPrivacyFormats = IgnorePrivacyFormatsToggle.IsOn);
    }

    private async void SecretProtectionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || SecretProtectionComboBox.SelectedItem is not ComboBoxItem { Tag: string mode })
        {
            return;
        }

        await SaveSliceMutationAsync(slice => slice.ClipboardSensitiveProtectionMode = mode);
    }

    private async void BrowseExcludedAppButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsService is null || _ownerWindow == 0)
        {
            return;
        }

        try
        {
            string? selectedPath = await FileOpenPickerService.PickSingleFileAsync(
                _ownerWindow,
                [".exe"],
                PickerLocationId.ComputerFolder);
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return;
            }

            string normalized = selectedPath.Trim();
            await SaveSliceMutationAsync(slice =>
            {
                if (!slice.ClipboardExcludedApps.Any(existing =>
                        string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    slice.ClipboardExcludedApps.Add(normalized);
                }
            });
            PrivacyExpander.IsExpanded = true;
            RebuildExcludedAppLists();
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardSettings] Browse excluded app failed: {ex.Message}");
        }
    }

    private async void RemoveExcludedAppButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string target } || string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        await SaveSliceMutationAsync(slice =>
        {
            slice.ClipboardExcludedApps.RemoveAll(existing =>
                string.Equals(existing, target, StringComparison.OrdinalIgnoreCase));
        });
        RebuildExcludedAppLists();
    }

    private async void ObservedAppCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string appPath } checkBox || string.IsNullOrWhiteSpace(appPath))
        {
            return;
        }

        bool shouldExclude = checkBox.IsChecked == true;
        string exeName = Path.GetFileName(appPath);
        await SaveSliceMutationAsync(slice =>
        {
            if (shouldExclude)
            {
                if (!slice.ClipboardExcludedApps.Any(existing =>
                        string.Equals(existing, appPath, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(exeName) &&
                         string.Equals(Path.GetFileName(existing), exeName, StringComparison.OrdinalIgnoreCase))))
                {
                    slice.ClipboardExcludedApps.Add(appPath);
                }
            }
            else
            {
                slice.ClipboardExcludedApps.RemoveAll(existing =>
                    string.Equals(existing, appPath, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(exeName) &&
                     string.Equals(Path.GetFileName(existing), exeName, StringComparison.OrdinalIgnoreCase)));
            }
        });
        RebuildExcludedAppLists();
    }

    private async void MaxItemsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || MaxItemsComboBox.SelectedItem is not ComboBoxItem { Tag: string raw } ||
            !int.TryParse(raw, out int count))
        {
            return;
        }

        await SaveSliceMutationAsync(slice => slice.ClipboardMaxItems = count);
    }

    private async void RetentionDaysComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || RetentionDaysComboBox.SelectedItem is not ComboBoxItem { Tag: string raw } ||
            !int.TryParse(raw, out int days))
        {
            return;
        }

        await SaveSliceMutationAsync(slice => slice.ClipboardRetentionDays = days);
    }

    private void CancelClearUnpinnedButton_Click(object sender, RoutedEventArgs e)
    {
        ClearUnpinnedConfirmFlyout.Hide();
    }

    private async void ConfirmClearUnpinnedButton_Click(object sender, RoutedEventArgs e)
    {
        ClearUnpinnedConfirmFlyout.Hide();
        if (_dataActions is null)
        {
            return;
        }

        try
        {
            await _dataActions.ClearClipboardUnpinnedAsync();
            await RefreshRuntimeStatsAndObservedAppsAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardSettings] Clear unpinned failed: {ex.Message}");
        }
    }

    private async void ResetClipboardButton_Click(object sender, RoutedEventArgs e)
    {
        if (_widgetActions is null || !_widgetActions.IsHostAvailable || _localization is null || XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _localization.Format(
                "Settings.FeatureWidgets.ResetDialogTitle",
                _localization.T("Clipboard.Title")),
            Content = new TextBlock
            {
                Text = _localization.T("Settings.FeatureWidgets.ResetDialogBody"),
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = _localization.T("Settings.FeatureWidgets.ResetConfirm"),
            CloseButtonText = _localization.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        ResetClipboardButton.IsEnabled = false;
        try
        {
            if (_settingsService is not null)
            {
                _isSavingSlice = true;
                try
                {
                    _settingsService.MutateGraphUnderLock(() =>
                        SettingsService.ApplyDefaultClipboardPreferences(_settingsService.Settings.Clipboard));
                    await _settingsService.SaveAsync();
                }
                finally
                {
                    _isSavingSlice = false;
                }
            }

            _clipboardHotkeyError = null;
            _hotkeyActions?.RefreshClipboardHotkeyRegistration();
            await _widgetActions.ResetFeatureWidgetAsync(WidgetKind.Clipboard);
            ResetClipboardRequested?.Invoke(this, EventArgs.Empty);
            RefreshFromSettings();
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardSettings] Reset clipboard widget failed: {ex.Message}");
        }
        finally
        {
            ApplyDependentControlGating();
        }
    }

    private async Task SaveSliceMutationAsync(Action<ClipboardSettingsSlice> mutate)
    {
        if (_settingsService is null)
        {
            return;
        }

        _isSavingSlice = true;
        try
        {
            _settingsService.MutateGraphUnderLock(() => mutate(_settingsService.Settings.Clipboard));
            await _settingsService.SaveAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[ClipboardSettings] Save slice mutation failed: {ex.Message}");
        }
        finally
        {
            _isSavingSlice = false;
        }
    }

    private static void SelectByTag(ComboBox comboBox, string expectedTag)
    {
        ComboBoxItem? match = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, expectedTag, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            comboBox.SelectedItem = match;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{Math.Max(0, bytes)} B";
        }

        double kb = bytes / 1024.0;
        if (kb < 1024.0)
        {
            return $"{kb:0.#} KB";
        }

        double mb = kb / 1024.0;
        if (mb < 1024.0)
        {
            return $"{mb:0.#} MB";
        }

        double gb = mb / 1024.0;
        return $"{gb:0.##} GB";
    }
}
