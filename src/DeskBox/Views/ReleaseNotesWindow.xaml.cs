using System.Runtime.InteropServices;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Documents;
using Windows.System;
using Windows.UI.Text;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class ReleaseNotesWindow : Window
{
    private const int DesiredWidth = 820;
    private const int DesiredHeight = 640;
    private const int MinimumWidth = 620;
    private const int MinimumHeight = 460;
    private const int WorkAreaMargin = 72;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmNcDestroy = 0x0082;
    private static readonly UIntPtr WindowSubclassId = new(0xD05C0B12);

    private readonly ThemeService _themeService;
    private readonly LocalizationService _localizationService;
    private readonly ReleaseNotesService _releaseNotesService;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hWnd;
    private readonly Win32Helper.SubclassProc _windowSubclassProc;
    private AppUpdateManifest _manifest;
    private string _currentVersion;
    private CancellationTokenSource? _loadCts;
    private IntPtr _ownerHwnd;
    private bool _isSubclassInstalled;
    private bool _isClosed;

    public ReleaseNotesWindow(
        AppUpdateManifest manifest,
        string currentVersion,
        ThemeService themeService,
        LocalizationService localizationService)
    {
        _manifest = manifest;
        _currentVersion = currentVersion;
        _themeService = themeService;
        _localizationService = localizationService;
        _releaseNotesService = new ReleaseNotesService();

        InitializeComponent();
        WindowsCompatibilityService.ApplySafeBackdrop(this);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _windowSubclassProc = WindowSubclassProc;
        _hWnd = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        AppBranding.ApplyWindowIcon(_appWindow);
        InstallMinimumSizeHook();

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
        }

        _themeService.TrackWindow(this);
        _localizationService.LanguageChanged += OnLanguageChanged;
        RootGrid.ActualThemeChanged += RootGrid_ActualThemeChanged;
        AppTitleBar.ActualThemeChanged += AppTitleBar_ActualThemeChanged;
        Closed += ReleaseNotesWindow_Closed;

        ResizeAndCenter(windowId);
        ApplyTitleBarColors();
        ApplyStaticText();
        _ = LoadReleaseNotesAsync();
    }

    public void UpdateManifest(AppUpdateManifest manifest, string currentVersion)
    {
        if (_isClosed)
        {
            return;
        }

        _manifest = manifest;
        _currentVersion = currentVersion;
        ApplyStaticText();
        _ = LoadReleaseNotesAsync();
    }

    public void ShowWindow(IntPtr ownerHwnd = default)
    {
        if (_isClosed)
        {
            return;
        }

        if (ownerHwnd != IntPtr.Zero && ownerHwnd != _hWnd && ownerHwnd != _ownerHwnd)
        {
            _ownerHwnd = ownerHwnd;
            _ = Win32Helper.SetWindowLongPtr(_hWnd, Win32Helper.GWLP_HWNDPARENT, ownerHwnd);
            ResizeAndCenter(Win32Interop.GetWindowIdFromWindow(ownerHwnd));
        }

        _appWindow.Show();
        Win32Helper.BringWindowTemporarilyToFront(_hWnd);
        Activate();
        _ = Win32Helper.SetForegroundWindow(_hWnd);
    }

    private void ApplyStaticText()
    {
        Title = _localizationService.T("Settings.ReleaseNotes.WindowTitle");
        WindowTitleText.Text = Title;
        HeaderText.Text = _localizationService.Format(
            "Settings.ReleaseNotes.Header",
            _manifest.Version);

        string releaseDate = string.IsNullOrWhiteSpace(_manifest.ReleaseDate)
            ? _localizationService.T("Settings.ReleaseNotes.UnknownDate")
            : _manifest.ReleaseDate;
        string channel = string.IsNullOrWhiteSpace(_manifest.Channel)
            ? "stable"
            : _manifest.Channel;
        MetadataText.Text = _localizationService.Format(
            "Settings.ReleaseNotes.Metadata",
            releaseDate,
            channel,
            _currentVersion);

        OpenOnlineButton.Content = _localizationService.T("Settings.ReleaseNotes.OpenOnline");
        CloseButton.Content = _localizationService.T("Settings.ReleaseNotes.Close");
        OpenOnlineButton.Visibility = AppUpdateManifest.IsSafeReleaseNotesUrl(_manifest.ReleaseNotesUrl)
            ? Visibility.Visible
            : Visibility.Collapsed;

    }

    private async Task LoadReleaseNotesAsync()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        CancellationToken cancellationToken = _loadCts.Token;

        MarkdownHost.Children.Clear();
        FallbackText.Visibility = Visibility.Visible;
        FallbackText.Text = _localizationService.T("Settings.ReleaseNotes.Loading");

        try
        {
            ReleaseNotesLoadResult result = await _releaseNotesService.LoadAsync(
                _manifest,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (result.HasContent)
            {
                RenderMarkdown(result.Content);
                var messages = new List<string>();
                if (result.IsFromCache)
                {
                    messages.Add(_localizationService.T("Settings.ReleaseNotes.Cached"));
                }

                FallbackText.Text = string.Join(" · ", messages);
                FallbackText.Visibility = messages.Count == 0
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
            else
            {
                FallbackText.Text = string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? _localizationService.T("Settings.ReleaseNotes.Empty")
                    : _localizationService.T("Settings.ReleaseNotes.OnlineOnly");
                FallbackText.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[ReleaseNotesWindow] Failed to load release notes: {ex.Message}");
            FallbackText.Text = _localizationService.T("Settings.ReleaseNotes.Empty");
            FallbackText.Visibility = Visibility.Visible;
        }
    }

    private void RenderMarkdown(string markdown)
    {
        MarkdownHost.Children.Clear();
        IReadOnlyList<SimpleMarkdownBlock> blocks = SimpleMarkdownRenderer.Parse(markdown);
        foreach (SimpleMarkdownBlock block in blocks)
        {
            FrameworkElement element = block.Kind switch
            {
                SimpleMarkdownBlockKind.Heading => CreateHeading(block),
                SimpleMarkdownBlockKind.ListItem => CreateListItem(block),
                SimpleMarkdownBlockKind.Quote => CreateQuote(block),
                SimpleMarkdownBlockKind.CodeBlock => CreateCodeBlock(block),
                SimpleMarkdownBlockKind.Separator => CreateSeparator(),
                _ => CreateTextBlock(block.Inlines, 15, FontWeights.Normal)
            };
            MarkdownHost.Children.Add(element);
        }
    }

    private FrameworkElement CreateHeading(SimpleMarkdownBlock block)
    {
        double fontSize = block.Level switch
        {
            1 => 24,
            2 => 20,
            3 => 17,
            _ => 15
        };
        return CreateTextBlock(block.Inlines, fontSize, FontWeights.SemiBold, new Thickness(0, 10, 0, 0));
    }

    private FrameworkElement CreateListItem(SimpleMarkdownBlock block)
    {
        string prefix = block.IsOrdered
            ? $"{block.ListIndex}. "
            : "• ";
        var textBlock = CreateTextBlock(block.Inlines, 15, FontWeights.Normal);
        textBlock.Inlines.Insert(0, new Run { Text = prefix, FontWeight = FontWeights.SemiBold });
        textBlock.Margin = new Thickness(8, 0, 0, 0);
        return textBlock;
    }

    private FrameworkElement CreateQuote(SimpleMarkdownBlock block)
    {
        var container = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        container.Children.Add(new Border
        {
            Background = GetAccentBrush(),
            CornerRadius = new CornerRadius(2)
        });
        var textBlock = CreateTextBlock(block.Inlines, 15, FontWeights.Normal, new Thickness(14, 2, 0, 2));
        Grid.SetColumn(textBlock, 1);
        container.Children.Add(textBlock);
        return container;
    }

    private static FrameworkElement CreateSeparator()
    {
        return new Border
        {
            Height = 1,
            Margin = new Thickness(0, 8, 0, 8),
            Background = GetThemeBrush("CardStrokeColorDefaultBrush")
        };
    }

    private static FrameworkElement CreateCodeBlock(SimpleMarkdownBlock block)
    {
        string text = block.Inlines.FirstOrDefault()?.Text ?? string.Empty;
        return new Border
        {
            Background = GetThemeBrush("ControlFillColorSecondaryBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 4, 0, 4),
            Child = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap
            }
        };
    }

    private TextBlock CreateTextBlock(
        IReadOnlyList<SimpleMarkdownInline> inlines,
        double fontSize,
        FontWeight fontWeight,
        Thickness? margin = null)
    {
        var textBlock = new TextBlock
        {
            FontSize = fontSize,
            FontWeight = fontWeight,
            TextWrapping = TextWrapping.Wrap,
            Margin = margin ?? new Thickness(0, 0, 0, 2)
        };

        foreach (SimpleMarkdownInline inline in inlines)
        {
            var run = new Run { Text = inline.Text };
            switch (inline.Kind)
            {
                case SimpleMarkdownInlineKind.Bold:
                    run.FontWeight = FontWeights.SemiBold;
                    break;
                case SimpleMarkdownInlineKind.Italic:
                    run.FontStyle = Windows.UI.Text.FontStyle.Italic;
                    break;
                case SimpleMarkdownInlineKind.Code:
                    run.FontFamily = new FontFamily("Consolas");
                    run.Foreground = GetAccentBrush();
                    break;
                case SimpleMarkdownInlineKind.Link:
                    run.FontWeight = FontWeights.SemiBold;
                    run.Foreground = GetAccentBrush();
                    run.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
                    break;
            }

            textBlock.Inlines.Add(run);
        }

        return textBlock;
    }

    private void OpenOnlineButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppUpdateManifest.IsSafeReleaseNotesUrl(_manifest.ReleaseNotesUrl))
        {
            Win32Helper.OpenFile(_manifest.ReleaseNotesUrl);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void OnLanguageChanged()
    {
        if (_isClosed)
        {
            return;
        }

        ApplyStaticText();
        _ = LoadReleaseNotesAsync();
    }

    private void RootGrid_ActualThemeChanged(FrameworkElement sender, object args)
    {
        ApplyTitleBarColors();
        if (MarkdownHost.Children.Count > 0)
        {
            _ = LoadReleaseNotesAsync();
        }
    }

    private void AppTitleBar_ActualThemeChanged(FrameworkElement sender, object args) => ApplyTitleBarColors();

    private void ApplyTitleBarColors()
    {
        bool isDark = RootGrid.ActualTheme switch
        {
            ElementTheme.Dark => true,
            ElementTheme.Light => false,
            _ => Win32Helper.IsSystemDarkMode()
        };
        AppWindowTitleBar titleBar = _appWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonInactiveForegroundColor = isDark
            ? Windows.UI.Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0xA0, 0x10, 0x10, 0x10);
    }

    private void ResizeAndCenter(WindowId displayWindowId)
    {
        DisplayArea displayArea = DisplayArea.GetFromWindowId(displayWindowId, DisplayAreaFallback.Primary);
        Windows.Graphics.RectInt32 workArea = displayArea.WorkArea;
        double scale = Win32Helper.GetDpiScaleForWindow(
            _ownerHwnd != IntPtr.Zero ? _ownerHwnd : _hWnd,
            RootGrid.XamlRoot);
        int width = Math.Clamp(
            ToPhysicalPixels(DesiredWidth, scale),
            ToPhysicalPixels(MinimumWidth, scale),
            Math.Max(ToPhysicalPixels(MinimumWidth, scale), workArea.Width - ToPhysicalPixels(WorkAreaMargin, scale)));
        int height = Math.Clamp(
            ToPhysicalPixels(DesiredHeight, scale),
            ToPhysicalPixels(MinimumHeight, scale),
            Math.Max(ToPhysicalPixels(MinimumHeight, scale), workArea.Height - ToPhysicalPixels(WorkAreaMargin, scale)));
        _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            workArea.X + Math.Max(0, (workArea.Width - width) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - height) / 2),
            width,
            height));
    }

    private void InstallMinimumSizeHook()
    {
        _isSubclassInstalled = Win32Helper.SetWindowSubclass(
            _hWnd,
            _windowSubclassProc,
            WindowSubclassId,
            UIntPtr.Zero);
    }

    private void RemoveMinimumSizeHook()
    {
        if (!_isSubclassInstalled)
        {
            return;
        }

        Win32Helper.RemoveWindowSubclass(_hWnd, _windowSubclassProc, WindowSubclassId);
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
            double scale = Win32Helper.GetDpiScaleForWindow(_hWnd, RootGrid.XamlRoot);
            minMaxInfo.MinTrackSize.X = Math.Max(minMaxInfo.MinTrackSize.X, ToPhysicalPixels(MinimumWidth, scale));
            minMaxInfo.MinTrackSize.Y = Math.Max(minMaxInfo.MinTrackSize.Y, ToPhysicalPixels(MinimumHeight, scale));
            Marshal.StructureToPtr(minMaxInfo, lParam, false);
            return IntPtr.Zero;
        }

        if (message == WmNcDestroy)
        {
            RemoveMinimumSizeHook();
        }

        return Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private void ReleaseNotesWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_isClosed)
        {
            return;
        }

        _isClosed = true;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        _localizationService.LanguageChanged -= OnLanguageChanged;
        RootGrid.ActualThemeChanged -= RootGrid_ActualThemeChanged;
        AppTitleBar.ActualThemeChanged -= AppTitleBar_ActualThemeChanged;
        Closed -= ReleaseNotesWindow_Closed;
        RemoveMinimumSizeHook();
    }

    private static Brush GetThemeBrush(string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out object value) && value is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Colors.Transparent);
    }

    private Brush GetAccentBrush() =>
        new SolidColorBrush(_themeService.GetEffectiveAccentColor());

    private static int ToPhysicalPixels(int logicalPixels, double scale)
    {
        double normalizedScale = double.IsFinite(scale) && scale > 0 ? scale : 1.0;
        return Math.Max(1, (int)Math.Round(logicalPixels * normalizedScale, MidpointRounding.AwayFromZero));
    }

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
