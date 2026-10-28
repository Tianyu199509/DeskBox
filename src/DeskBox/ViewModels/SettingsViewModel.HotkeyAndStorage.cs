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
    public GlobalHotkeyActivation GetCurrentGlobalHotkeyActivation()
    {
        var settings = _settingsService.Settings;
        return GlobalHotkeyService.NormalizeActivation(
            settings.GlobalHotkeyActivationKind,
            settings.GlobalHotkeyModifiers,
            settings.GlobalHotkeyKey);
    }

    /// <summary>
    /// Recomputes the whole global-hotkey card presentation (enable state,
    /// activation text, registration status, localized description/warning
    /// and the reserved-gesture warning gate) and pushes it onto the
    /// interaction editor's binding surface. The hotkey state machine stays
    /// on this shell; the editor never queries the hotkey service.
    /// </summary>
    public void RefreshGlobalHotkeyState()
    {
        bool enabled = _settingsService.Settings.GlobalHotkeyEnabled;
        GlobalHotkeyActivation activation = GetCurrentGlobalHotkeyActivation();
        string text = GlobalHotkeyService.FormatActivation(activation, _localizationService);

        string statusText;
        if (!enabled)
        {
            statusText = _localizationService.T("Settings.GlobalHotkey.Status.Disabled");
        }
        else if (_hotkeyActions.GetRuntimeState() is not { } hotkeyState)
        {
            statusText = _localizationService.T("Settings.GlobalHotkey.Status.Unavailable");
        }
        else if (hotkeyState.IsRegistered)
        {
            // The risky-activation flag only tinted the legacy status kind,
            // which had no visual consumer; the status text is identical.
            statusText = _localizationService.Format(
                "Settings.GlobalHotkey.Status.Active",
                hotkeyState.CurrentGestureText);
        }
        else
        {
            statusText = string.IsNullOrWhiteSpace(hotkeyState.LastError)
                ? _localizationService.T("Settings.GlobalHotkey.Status.Unregistered")
                : hotkeyState.LastError;
        }

        string warningText = activation.Kind == HotkeyActivationKind.WindowsTap
            ? _localizationService.T("Settings.GlobalHotkey.WindowsTapWarning")
            : activation.Kind == HotkeyActivationKind.Chord &&
              activation.Gesture.Modifiers == HotkeyModifierKeys.Alt &&
              activation.Gesture.VirtualKey == (int)Windows.System.VirtualKey.Space
                ? _localizationService.T("Settings.GlobalHotkey.AltSpaceWarning")
                : _localizationService.T("Settings.GlobalHotkey.ReservedWarning");

        bool canShowWarning = enabled &&
            (activation.Kind == HotkeyActivationKind.WindowsTap ||
             (activation.Kind == HotkeyActivationKind.Chord &&
              GlobalHotkeyService.IsReservedSystemGesture(activation.Gesture)));

        _interactionSettings.UpdateGlobalHotkeyPresentation(new GlobalHotkeyPresentationSettings(
            enabled,
            text,
            statusText,
            _localizationService.T("Settings.GlobalHotkey.Description"),
            warningText,
            canShowWarning));
    }

    public async Task RefreshQuickAccessStateAsync(bool showBusy = false, CancellationToken cancellationToken = default)
    {
        cancellationToken = cancellationToken.CanBeCanceled ? cancellationToken : _lifetimeCts.Token;
        string path = ManagedStorageRootPath;
        if (showBusy)
        {
            IsQuickAccessBusy = true;
        }

        try
        {
            QuickAccessStateResult result = await ExplorerQuickAccessHelper
                .GetQuickAccessPinStateAsync(path)
                .WaitAsync(cancellationToken);
            if (!_isDisposed && string.Equals(path, ManagedStorageRootPath, StringComparison.OrdinalIgnoreCase))
            {
                ManagedStorageQuickAccessPinState = result.State;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsViewModel] Failed to refresh Quick Access state: {ex}");
            if (!_isDisposed && string.Equals(path, ManagedStorageRootPath, StringComparison.OrdinalIgnoreCase))
            {
                ManagedStorageQuickAccessPinState = QuickAccessPinState.Unknown;
            }
        }
        finally
        {
            if (showBusy && !_isDisposed)
            {
                IsQuickAccessBusy = false;
            }
        }
    }

    public void SetQuickAccessBusy(bool isBusy)
    {
        IsQuickAccessBusy = isBusy;
    }

    public void SetQuickAccessPinState(QuickAccessPinState state)
    {
        ManagedStorageQuickAccessPinState = state;
    }
}
