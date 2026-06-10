using DeskBox.Helpers;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class SettingsWindow : Window
{
    private const int DefaultWindowWidth = 760;
    private const int DefaultWindowHeight = 760;
    private const int MinWindowWidth = 600;
    private const int MinWindowHeight = 560;
    private const double ContentMaxWidth = 720;
    private const double PageSidePadding = 20;
    private const double NarrowLayoutThreshold = 560;
    private const double NarrowTitleThreshold = 560;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmNcDestroy = 0x0082;
    private static readonly UIntPtr SettingsWindowSubclassId = new(1);

    private readonly ThemeService _themeService;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hWnd;
    private readonly SubclassProc _windowSubclassProc;
    private readonly List<Grid> _settingRows = [];
    private readonly List<Grid> _metricRows = [];
    private bool _isSubclassInstalled;

    public SettingsViewModel ViewModel { get; }

    public SettingsWindow(SettingsService settingsService, OrganizerService organizerService, ThemeService themeService)
    {
        _themeService = themeService;
        ViewModel = new SettingsViewModel(settingsService, organizerService, themeService);
        InitializeComponent();

        SettingsRoot.DataContext = ViewModel;
        CollectResponsiveRows(SettingsRoot);
        SettingsRoot.Loaded += (_, _) =>
        {
            CollectResponsiveRows(SettingsRoot);
            UpdateResponsiveLayout(GetWindowWidth());
        };

        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop
        {
            Kind = MicaKind.BaseAlt
        };

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _windowSubclassProc = WindowSubclassProc;
        _hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        AppBranding.ApplyWindowIcon(_appWindow);
        InstallMinimumSizeHook();
        _appWindow.Resize(new Windows.Graphics.SizeInt32(DefaultWindowWidth, DefaultWindowHeight));

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
        }

        var workArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary).WorkArea;
        _appWindow.Move(new Windows.Graphics.PointInt32(
            workArea.X + Math.Max(0, (_appWindow.Size.Width < workArea.Width ? (workArea.Width - _appWindow.Size.Width) / 2 : 0)),
            workArea.Y + Math.Max(0, (_appWindow.Size.Height < workArea.Height ? (workArea.Height - _appWindow.Size.Height) / 2 : 0))));

        _themeService.TrackWindow(this);
        _themeService.AppearanceChanged += OnAppearanceChanged;

        ApplyTitleBarButtonColors();
        UpdateResponsiveLayout(GetWindowWidth());

        SizeChanged += (_, args) =>
        {
            UpdateResponsiveLayout(args.Size.Width);
        };

        Closed += (_, _) =>
        {
            RemoveMinimumSizeHook();
            _themeService.AppearanceChanged -= OnAppearanceChanged;
            ViewModel.Dispose();
        };
    }

    private async void AccentColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanEditCustomAccent || SettingsRoot.XamlRoot is null)
        {
            return;
        }

        var picker = new ColorPicker
        {
            Color = ViewModel.GetCurrentAccentColor(),
            IsAlphaEnabled = false,
            IsColorChannelTextInputVisible = true,
            IsColorSliderVisible = true,
            IsColorSpectrumVisible = true,
            IsHexInputVisible = true,
            MinWidth = 320
        };

        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = "自定义主题色",
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            Content = picker
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.SetCustomAccentColor(picker.Color);
        }
    }

    private void AccentPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string hex } ||
            !AccentColorHelper.TryParseHex(hex, out var color))
        {
            return;
        }

        ViewModel.SetCustomAccentColor(color);
    }

    private async void ChangeManagedStoragePathButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return;
        }

        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, _hWnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        string normalizedPath = SettingsService.NormalizeManagedStorageRootPath(folder.Path);
        if (string.Equals(normalizedPath, ViewModel.ManagedStorageRootPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        int affectedCount = App.Current.WidgetManager?.GetDefaultManagedStorageWidgetCount() ?? 0;
        if (affectedCount > 0)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = "迁移默认收纳路径",
                PrimaryButtonText = "迁移",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                Content = new TextBlock
                {
                    Text = $"将把 {affectedCount} 个跟随默认路径的收纳组件从\n{ViewModel.ManagedStorageRootPath}\n迁移到\n{normalizedPath}\n\n手动映射的文件夹不会受到影响。",
                    TextWrapping = TextWrapping.Wrap
                }
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        if (App.Current.WidgetManager is not null)
        {
            try
            {
                await App.Current.WidgetManager.UpdateDefaultManagedStorageRootAsync(normalizedPath);
            }
            catch (Exception ex)
            {
                var errorDialog = new ContentDialog
                {
                    XamlRoot = SettingsRoot.XamlRoot,
                    Title = "迁移失败",
                    CloseButtonText = "确定",
                    DefaultButton = ContentDialogButton.Close,
                    Content = new TextBlock
                    {
                        Text = $"默认收纳路径没有更新。\n\n{ex.Message}",
                        TextWrapping = TextWrapping.Wrap
                    }
                };

                await errorDialog.ShowAsync();
                return;
            }
        }

        ViewModel.UpdateManagedStorageRootPath(normalizedPath);
    }

    private void OpenManagedStoragePathButton_Click(object sender, RoutedEventArgs e)
    {
        string path = ViewModel.ManagedStorageRootPath;
        Directory.CreateDirectory(path);
        Win32Helper.OpenFile(path);
    }

    private void OnAppearanceChanged()
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            ApplyTitleBarButtonColors();
            return;
        }

        DispatcherQueue.TryEnqueue(ApplyTitleBarButtonColors);
    }

    private void ApplyTitleBarButtonColors()
    {
        bool isDark = _themeService.CurrentTheme switch
        {
            ElementTheme.Dark => true,
            ElementTheme.Light => false,
            _ => Win32Helper.IsSystemDarkMode()
        };

        var titleBar = _appWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonHoverForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonPressedForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonInactiveForegroundColor = isDark
            ? ColorHelper.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0xB8, 0x10, 0x10, 0x10);
        titleBar.ButtonHoverBackgroundColor = isDark
            ? ColorHelper.FromArgb(0x22, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0x10, 0x00, 0x00, 0x00);
        titleBar.ButtonPressedBackgroundColor = isDark
            ? ColorHelper.FromArgb(0x30, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0x18, 0x00, 0x00, 0x00);
    }

    private double GetWindowWidth()
    {
        return Content is FrameworkElement root && root.ActualWidth > 0
            ? root.ActualWidth
            : _appWindow.Size.Width;
    }

    private void UpdateResponsiveLayout(double width)
    {
        bool isNarrow = width < NarrowLayoutThreshold;

        PageScroller.Padding = isNarrow
            ? new Thickness(PageSidePadding, 16, PageSidePadding, 34)
            : new Thickness(PageSidePadding, 16, PageSidePadding, 38);

        double availableContentWidth = Math.Max(0, width - PageSidePadding * 2);
        ContentHost.Width = Math.Min(ContentMaxWidth, availableContentWidth);
        ContentHost.MaxWidth = ContentMaxWidth;
        PathActionsPanel.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        PathActionsPanel.Orientation = isNarrow ? Orientation.Vertical : Orientation.Horizontal;
        OpenPathButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        ChangePathButton.HorizontalAlignment = isNarrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;

        foreach (var row in _settingRows)
        {
            ApplyResponsiveRowLayout(row, isNarrow);
        }

        foreach (var row in _metricRows)
        {
            ApplyResponsiveRowLayout(row, isNarrow);
        }

        TitleTextHost.Visibility = width < NarrowTitleThreshold ? Visibility.Collapsed : Visibility.Visible;
        AppTitleBar.Padding = width < NarrowTitleThreshold
            ? new Thickness(12, 0, 88, 0)
            : new Thickness(18, 0, 128, 0);
    }

    private void CollectResponsiveRows(DependencyObject root)
    {
        if (root == SettingsRoot)
        {
            _settingRows.Clear();
            _metricRows.Clear();
        }

        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is Grid grid && grid.Tag is string tag)
            {
                if (string.Equals(tag, "SettingRow", StringComparison.Ordinal))
                {
                    _settingRows.Add(grid);
                }
                else if (string.Equals(tag, "MetricRow", StringComparison.Ordinal))
                {
                    _metricRows.Add(grid);
                }
            }

            CollectResponsiveRows(child);
        }
    }

    private static void ApplyResponsiveRowLayout(Grid row, bool isNarrow)
    {
        if (row.Children.Count < 2)
        {
            return;
        }

        var action = row.Children[1] as FrameworkElement;
        if (action is null)
        {
            return;
        }

        if (isNarrow)
        {
            if (row.RowDefinitions.Count < 2)
            {
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            if (row.ColumnDefinitions.Count > 1)
            {
                row.ColumnDefinitions[1].Width = new GridLength(0);
            }

            row.ColumnSpacing = 0;
            Grid.SetRow(action, 1);
            Grid.SetColumn(action, 0);
            action.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        else
        {
            if (row.ColumnDefinitions.Count > 1)
            {
                row.ColumnDefinitions[1].Width = GridLength.Auto;
            }

            row.ColumnSpacing = row.Tag is string tag && string.Equals(tag, "MetricRow", StringComparison.Ordinal)
                ? 16
                : 24;
            Grid.SetRow(action, 0);
            Grid.SetColumn(action, 1);
            action.HorizontalAlignment = HorizontalAlignment.Right;
        }
    }

    private void InstallMinimumSizeHook()
    {
        _isSubclassInstalled = SetWindowSubclass(_hWnd, _windowSubclassProc, SettingsWindowSubclassId, UIntPtr.Zero);
    }

    private void RemoveMinimumSizeHook()
    {
        if (!_isSubclassInstalled)
        {
            return;
        }

        RemoveWindowSubclass(_hWnd, _windowSubclassProc, SettingsWindowSubclassId);
        _isSubclassInstalled = false;
    }

    private IntPtr WindowSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr refData)
    {
        if (message == WmGetMinMaxInfo)
        {
            var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            minMaxInfo.MinTrackSize.X = Math.Max(minMaxInfo.MinTrackSize.X, MinWindowWidth);
            minMaxInfo.MinTrackSize.Y = Math.Max(minMaxInfo.MinTrackSize.Y, MinWindowHeight);
            Marshal.StructureToPtr(minMaxInfo, lParam, false);
            return IntPtr.Zero;
        }

        if (message == WmNcDestroy)
        {
            RemoveMinimumSizeHook();
        }

        return DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private delegate IntPtr SubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr hWnd,
        SubclassProc subclassProc,
        UIntPtr subclassId,
        UIntPtr refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        IntPtr hWnd,
        SubclassProc subclassProc,
        UIntPtr subclassId);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }
}
