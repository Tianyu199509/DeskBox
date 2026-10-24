using System.Globalization;
using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    public string[] AvailableLanguages { get; } =
    [
        SettingsService.LanguageSystem,
        SettingsService.LanguageChinese,
        SettingsService.LanguageChineseTraditional,
        SettingsService.LanguageEnglish,
        LocalizationService.LanguageJapanese,
        LocalizationService.LanguageGerman,
        LocalizationService.LanguagePortuguese,
        LocalizationService.LanguageHindi,
        LocalizationService.LanguageSpanish,
        LocalizationService.LanguageFrench,
        LocalizationService.LanguageArabic,
        LocalizationService.LanguageBengali,
        LocalizationService.LanguageRussian,
        SettingsService.LanguageKorean
    ];
    public string[] AvailableLanguageDisplayNames => _cachedLanguageDisplayNames ??= AvailableLanguages.Select(_localizationService.GetLanguageDisplayName).ToArray();

    // Host-side working state for the managed-storage section. The XAML
    // binding surface lives on the managed-storage editor (section-level
    // DataContext switch); the shell keeps the path display state it needs
    // for the folder picker / migration / quick-access chains and pushes the
    // quick-access presentation onto the editor whenever the state changes.
    internal string ManagedStorageRootPath
    {
        get => _managedStorageRootPath;
        private set => SetProperty(ref _managedStorageRootPath, value);
    }

    internal QuickAccessPinState ManagedStorageQuickAccessPinState
    {
        get => _quickAccessPinState;
        private set
        {
            if (SetProperty(ref _quickAccessPinState, value))
            {
                PushQuickAccessPresentation();
            }
        }
    }

    internal bool IsQuickAccessBusy
    {
        get => _isQuickAccessBusy;
        private set
        {
            if (SetProperty(ref _isQuickAccessBusy, value))
            {
                PushQuickAccessPresentation();
            }
        }
    }

    private void PushQuickAccessPresentation()
    {
        bool busy = IsQuickAccessBusy;
        bool pinned = ManagedStorageQuickAccessPinState == QuickAccessPinState.Pinned;
        _managedStorageSettings.UpdateQuickAccessPresentation(new QuickAccessPresentationSettings(
            CanInvoke: !busy,
            ShouldUnpin: pinned,
            StatusText: busy
                ? _localizationService.T("Settings.ManagedPath.QuickAccessStatusUpdating")
                : ManagedStorageQuickAccessPinState switch
                {
                    QuickAccessPinState.Pinned => _localizationService.T("Settings.ManagedPath.QuickAccessStatusPinned"),
                    QuickAccessPinState.NotPinned => _localizationService.T("Settings.ManagedPath.QuickAccessStatusNotPinned"),
                    _ => _localizationService.T("Settings.ManagedPath.QuickAccessStatusUnknown")
                },
            ButtonText: busy
                ? _localizationService.T("Settings.ManagedPath.QuickAccessUpdating")
                : pinned
                    ? _localizationService.T("Settings.ManagedPath.UnpinQuickAccess")
                    : _localizationService.T("Settings.ManagedPath.PinQuickAccess"),
            ToolTipText: busy
                ? _localizationService.T("Settings.ManagedPath.QuickAccessUpdatingTooltip")
                : pinned
                    ? _localizationService.T("Settings.ManagedPath.UnpinQuickAccessTooltip")
                    : _localizationService.T("Settings.ManagedPath.PinQuickAccessTooltip")));
    }

    // Host linkage for the interaction editor: the editor owns the hotkey
    // enable switch's binding surface; the registration state machine stays
    // on the shell because it reaches the host's hotkey service.
    private void OnInteractionHotkeyEnabledUserChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _hotkeyActions.SetEnabled(value);
        RefreshGlobalHotkeyState();
    }

    public IEnumerable<FeatureWidgetEntry> FeatureWidgetEntries
    {
        get
        {
            var factory = new FeatureWidgetEntryFactory(
                _localizationService,
                new WidgetContentFactory(_localizationService),
                WidgetRegistry.Default,
                IsWidgetEnabled);
            return factory.CreateEntries();
        }
    }

    public bool IsWidgetEnabled(WidgetKind kind)
    {
        // The Todo switch's state (incl. its pending async transition) is
        // the section editor's projection (batch 47).
        if (kind == WidgetKind.Todo) return _todoSettings.Enabled;
        if (kind == WidgetKind.Search) return _searchFeatureSettings.Enabled;
        return _widgetActions.IsFeatureWidgetEnabled(kind);
    }

    public async void SetWidgetEnabled(WidgetKind kind, bool enabled)
    {
        // Every feature switch rides the one facade command: the main
        // process runs the kind's full chain (coordinator side effects for
        // QuickCapture/Todo/Search, dictionary + manager apply for the
        // rest). The local memory mirror flips the switch UI immediately;
        // the command response reconciles it with the authoritative value —
        // the reload push alone can lag seconds behind the widget
        // animation, and the dictionary never rides settings.json at all,
        // so waiting for the reload would show a permanently stale switch.
        MirrorFeatureEnabledLocally(kind, enabled);
        bool applied = await _widgetActions.SetFeatureWidgetEnabled(kind, enabled);
        if (applied != enabled)
        {
            MirrorFeatureEnabledLocally(kind, applied);
        }
    }

    private void MirrorFeatureEnabledLocally(WidgetKind kind, bool enabled)
    {
        // Memory mirror for the switch UI, routed through each feature's
        // owning editor/coordinator exactly like the pre-split switch did —
        // direct migrated-field writes from the shell are contract-forbidden.
        switch (kind)
        {
            case WidgetKind.QuickCapture:
                _quickCaptureSettingsEditor.SetFeatureEnabled(enabled);
                break;
            case WidgetKind.Todo:
                // Presentation-only: the card's own command below is the
                // write; re-entering the section switch chain here fired a
                // second, racing enable (batch R8).
                _todoSettings.SetEnabledPresentationOnly(enabled);
                break;
            default:
                FeatureWidgetSettings.SetEnabled(_settingsService.Settings, kind, enabled);
                break;
        }

        OnPropertyChanged(nameof(FeatureWidgetEntries));
    }

    public async Task ResetFeatureWidgetAsync(WidgetKind kind)
    {
        if (!FeatureWidgetSettings.IsFeatureWidget(kind))
        {
            return;
        }

        try
        {
            await ApplyFeatureWidgetDefaultSettingsAsync(kind);
            await _widgetActions.ResetFeatureWidgetAsync(kind);
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsViewModel] Failed to reset feature widget kind={kind}: {ex}");
        }
        finally
        {
            RefreshFeatureWidgetViewState(kind);
            OnPropertyChanged(nameof(FeatureWidgetEntries));
        }
    }

    private async Task ApplyFeatureWidgetDefaultSettingsAsync(WidgetKind kind)
    {
        Task recordingDrain = Task.CompletedTask;
        bool wasApplyingSnapshot = _isApplyingSettingsSnapshot;
        _isApplyingSettingsSnapshot = true;
        try
        {
            switch (kind)
            {
                case WidgetKind.QuickCapture:
                    _quickCaptureSettings.ResetTabPreferences(scheduleSave: false);
                    _quickCaptureSettings.ResetPresentationPreferences(scheduleSave: false);
                    _quickCaptureSettings.ResetRecentLimit(scheduleSave: false);
                    recordingDrain = _quickCaptureSettings.ResetRecordingAsync();
                    _quickCaptureSettings.ResetEditorPreferences(scheduleSave: false);
                    // The section editor re-projects from the coordinator's
                    // reset state (the old shell mirror assignments and the
                    // diagnostics rebuild folded into this sync).
                    _quickCaptureSettingsEditor.SyncPresentation();
                    _quickCaptureSettingsEditor.RefreshClipboardDiagnostics();
                    break;
                case WidgetKind.Todo:
                    _todoSettings.ResetDisplayOptions(scheduleSave: false);
                    _todoSettings.ResetPreviewLineCount(scheduleSave: false);
                    _todoSettings.ResetInputPreferences(scheduleSave: false);
                    _todoSettings.ResetLayoutPreferences(scheduleSave: false);
                    _todoSettings.ResetTabPreferences(scheduleSave: false);
                    _todoSettings.ResetReminderPreferences(scheduleSave: false);
                    break;
                case WidgetKind.Music:
                    _featureWidgetsSettings.ResetMusicPresentationPreferences(scheduleSave: false);
                    _musicSettings.SyncPresentation();
                    break;
                case WidgetKind.Weather:
                    // The weather section editor owns the binding surface
                    // (batch 48): the coordinator reset port writes the
                    // fresh-install defaults and the editor's RunWrite
                    // re-projects them (the old shell mirror assignments
                    // are gone).
                    _weatherSettings.ResetPreferences(scheduleSave: false);
                    break;
            }
        }
        finally
        {
            _isApplyingSettingsSnapshot = wasApplyingSnapshot;
        }

        await recordingDrain;
        await _settingsService.SaveAsync();
    }

    private void RefreshFeatureWidgetViewState(WidgetKind kind)
    {
        switch (kind)
        {
            case WidgetKind.QuickCapture:
                // The section editor re-projects the whole Quick Capture
                // surface (the old per-property notifications are its own
                // PropertyChanged broadcasts now).
                _quickCaptureSettingsEditor.SyncPresentation();
                _quickCaptureSettingsEditor.RefreshClipboardDiagnostics();
                break;
            case WidgetKind.Todo:
                // The section editor re-projects the whole Todo surface
                // (the old per-property notifications are its own
                // PropertyChanged broadcasts now).
                _todoSettings.Refresh();
                OnPropertyChanged(nameof(FeatureWidgetEntries));
                break;
            case WidgetKind.Music:
                break;
            case WidgetKind.Weather:
                // The section editor re-projects the whole Weather surface
                // (the old per-property notifications are its own
                // PropertyChanged broadcasts now).
                _weatherSettings.Refresh();
                break;
        }
    }

// ─── Weather Settings Properties ──────────────────────────────
}
