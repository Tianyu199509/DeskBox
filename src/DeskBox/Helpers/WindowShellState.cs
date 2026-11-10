// Copyright (c) DeskBox. All rights reserved.

using Microsoft.UI.Windowing;

namespace DeskBox.Helpers;

/// <summary>
/// Best-effort shell window state. Early-logon sessions can surface E_NOTIMPL
/// from windowing APIs (observed on AppWindow.IsShownInSwitchers), so these
/// helpers degrade with a log line instead of failing the caller's window
/// initialization.
/// </summary>
internal static class WindowShellState
{
    /// <summary>
    /// Keeps a helper window out of Alt+Tab. A window that still shows up in
    /// the switcher is an acceptable degraded state; breaking the caller is not.
    /// </summary>
    public static void TryHideFromSwitchers(AppWindow appWindow)
    {
        try
        {
            appWindow.IsShownInSwitchers = false;
        }
        catch (Exception ex)
        {
            App.Log($"[Window] IsShownInSwitchers not applied: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies the borderless, fixed-size overlapped presenter shared by the
    /// popover hosts and the detach preview window.
    /// </summary>
    public static void TryApplyBorderlessOverlappedPresenter(AppWindow appWindow)
    {
        try
        {
            appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.SetBorderAndTitleBar(false, false);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Window] Borderless presenter not applied: {ex.Message}");
        }
    }

    public static void TryApplyFloatingPopupPresenter(AppWindow appWindow)
    {
        try
        {
            appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
                presenter.SetBorderAndTitleBar(true, false);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Window] Floating popup presenter not applied: {ex.Message}");
        }
    }
}
