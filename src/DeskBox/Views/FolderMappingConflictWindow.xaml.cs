// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace DeskBox.Views;

public enum FolderMappingConflictResult
{
    /// <summary>The window closed without a choice (X button).</summary>
    None,

    /// <summary>Reveal the widget that already owns the folder.</summary>
    OpenWidget,

    /// <summary>Open settings on the managed-storage location page.</summary>
    ChangeStorage,

    /// <summary>Reopen the folder picker.</summary>
    Repick,
}

internal readonly record struct FolderMappingConflictButtonPlan(
    bool ShowOpenWidget,
    bool ShowChangeStorage);

/// <summary>
/// Explains a rejected folder mapping in place. The previous flow announced
/// the conflict through a notification that never renders in elevated
/// processes (AppNotificationManager is unsupported there), then opened the
/// storage settings unasked — field users read that as "confirm does
/// nothing and DeskBox jumps somewhere" (WeChat case 2026-10-27). This
/// window names the actual conflict and offers only actions that can really
/// resolve the user's case.
/// </summary>
internal sealed partial class FolderMappingConflictWindow : Window
{
    private const int DesiredWindowWidth = 480;
    private const int DesiredWindowHeight = 280;
    private const int WindowWorkAreaMargin = 80;

    private readonly AppWindow _appWindow;
    private readonly IntPtr _hWnd;
    private readonly TaskCompletionSource<FolderMappingConflictResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FolderMappingConflictResult _primaryResult;
    private FolderMappingConflictResult? _secondaryResult;

    /// <summary>
    /// Pure, testable button selection. The actions must fit the conflict:
    /// moving the managed-storage root migrates the whole tree with it, so a
    /// folder inside the tree stays inside and "change storage location"
    /// would be a dead-end action there — for that case only re-picking is
    /// offered, and the body carries the "move it out first" guidance.
    /// </summary>
    internal static FolderMappingConflictButtonPlan PlanButtons(
        FileWidgetPathConflictKind kind,
        FileWidgetPathRelation relation) =>
        kind == FileWidgetPathConflictKind.ExistingWidget
            ? new FolderMappingConflictButtonPlan(ShowOpenWidget: true, ShowChangeStorage: false)
            : new FolderMappingConflictButtonPlan(
                ShowOpenWidget: false,
                ShowChangeStorage: relation is not FileWidgetPathRelation.CandidateInsideOther);

    public FolderMappingConflictWindow(
        LocalizationService localizationService,
        string body,
        FolderMappingConflictButtonPlan plan)
    {
        InitializeComponent();
        WindowsCompatibilityService.ApplySafeBackdrop(this);

        string title = localizationService.T("Widget.MapFolder.ConflictDialogTitle");
        Title = title;
        TitleText.Text = title;
        BodyText.Text = body;
        CloseButton.Content = localizationService.T("Widget.MapFolder.ConflictDialogClose");

        string repickLabel = localizationService.T("Widget.MapFolder.ConflictDialogPickAnother");
        if (plan.ShowOpenWidget)
        {
            SetPrimary(
                localizationService.T("Widget.MapFolder.ConflictDialogOpenWidget"),
                FolderMappingConflictResult.OpenWidget);
            SetSecondary(repickLabel, FolderMappingConflictResult.Repick);
        }
        else if (plan.ShowChangeStorage)
        {
            SetPrimary(
                localizationService.T("Widget.MapFolder.ConflictDialogChangeStorage"),
                FolderMappingConflictResult.ChangeStorage);
            SetSecondary(repickLabel, FolderMappingConflictResult.Repick);
        }
        else
        {
            SetPrimary(repickLabel, FolderMappingConflictResult.Repick);
        }

        Closed += (_, _) => _completion.TrySetResult(FolderMappingConflictResult.None);

        _hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        AppBranding.ApplyWindowIcon(_appWindow);

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        ResizeAndCenterForDisplay(windowId);
    }

    /// <summary>Completes when the user picks an action or closes the window.</summary>
    public Task<FolderMappingConflictResult> Completion => _completion.Task;

    private void SetPrimary(string label, FolderMappingConflictResult result)
    {
        PrimaryButton.Content = label;
        _primaryResult = result;
    }

    private void SetSecondary(string label, FolderMappingConflictResult result)
    {
        SecondaryButton.Content = label;
        SecondaryButton.Visibility = Visibility.Visible;
        _secondaryResult = result;
    }

    private void PrimaryButton_Click(object sender, RoutedEventArgs e) =>
        Complete(_primaryResult);

    private void SecondaryButton_Click(object sender, RoutedEventArgs e) =>
        Complete(_secondaryResult ?? FolderMappingConflictResult.None);

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        Complete(FolderMappingConflictResult.None);

    private void Complete(FolderMappingConflictResult result)
    {
        _completion.TrySetResult(result);
        Close();
    }

    private void ResizeAndCenterForDisplay(Microsoft.UI.WindowId windowId)
    {
        var workArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
        double scale = Win32Helper.GetDpiScaleForWindow(_hWnd, RootGrid.XamlRoot);
        int width = ToPhysicalPixels(DesiredWindowWidth, scale);
        int height = ToPhysicalPixels(DesiredWindowHeight, scale);
        int workAreaMargin = ToPhysicalPixels(WindowWorkAreaMargin, scale);
        width = Math.Min(width, Math.Max(0, workArea.Width - workAreaMargin));
        height = Math.Min(height, Math.Max(0, workArea.Height - workAreaMargin));

        _appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
        _appWindow.Move(new Windows.Graphics.PointInt32(
            workArea.X + Math.Max(0, (workArea.Width - width) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - height) / 2)));
    }

    private static int ToPhysicalPixels(int logicalPixels, double scale)
    {
        return (int)Math.Round(logicalPixels * scale);
    }
}
