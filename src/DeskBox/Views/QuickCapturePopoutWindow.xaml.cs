using System.Runtime.InteropServices;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using WinRT.Interop;

namespace DeskBox.Views;

/// <summary>
/// Standalone Quick Capture note pop-out / desktop sticky reference window and
/// compact Quick Jot floating capture surface.
/// Decoupled from DefView desktop-band widget hiding so pinned reference notes
/// stay visible above active applications whenever Always-on-Top is enabled.
/// </summary>
public sealed partial class QuickCapturePopoutWindow : Window
{
    private const int PopoutDesiredWidth = 460;
    private const int PopoutDesiredHeight = 520;
    private const int PopoutMinWidth = 280;
    private const int PopoutMinHeight = 220;
    private const int QuickJotDesiredWidth = 480;
    private const int QuickJotDesiredHeight = 270;
    private const int QuickJotMinWidth = 340;
    private const int QuickJotMinHeight = 200;
    private const int AutoSaveDelayMs = 600;
    private const int ImageDecodeWidth = 1200;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmNcDestroy = 0x0082;
    private static readonly UIntPtr WindowSubclassId = new(0xD05C0B48);

    private readonly QuickCaptureService _quickCaptureService;
    private readonly SettingsService _settingsService;
    private readonly ThemeService _themeService;
    private readonly LocalizationService _localizationService;
    private readonly MarkdownDocumentService _markdownService = new();
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hWnd;
    private readonly Win32Helper.SubclassProc _windowSubclassProc;
    private readonly DispatcherQueueTimer _autoSaveTimer;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly List<DroppedFilePath> _pendingQuickJotAttachments = [];
    private readonly bool _isQuickJotMode;

    private string? _itemId;
    private QuickCaptureItem? _item;
    private QuickCaptureAppearancePreset _appearancePreset = QuickCaptureAppearancePreset.Default;
    private TextContentFormat _contentFormat = TextContentFormat.Markdown;
    private IntPtr _previousForegroundHwnd;
    private bool _isEditing;
    private bool _suppressEditorChanges;
    private bool _hasUnsavedChanges;
    private bool _isSubclassInstalled;
    private bool _isClosed;
    private long _imageLoadVersion;
    private DoubleTappedEventHandler? _readerDoubleTappedHandler;

    public QuickCapturePopoutWindow(
        string? itemId,
        bool startAlwaysOnTop,
        bool startEditing,
        QuickCaptureService quickCaptureService,
        SettingsService settingsService,
        ThemeService themeService,
        LocalizationService localizationService,
        IntPtr previousForegroundHwnd = default)
    {
        ArgumentNullException.ThrowIfNull(quickCaptureService);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(themeService);
        ArgumentNullException.ThrowIfNull(localizationService);

        _itemId = string.IsNullOrWhiteSpace(itemId) ? null : itemId;
        _isQuickJotMode = _itemId is null;
        _previousForegroundHwnd = previousForegroundHwnd;
        _quickCaptureService = quickCaptureService;
        _settingsService = settingsService;
        _themeService = themeService;
        _localizationService = localizationService;

        InitializeComponent();
        WindowsCompatibilityService.ApplySafeBackdrop(this);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _windowSubclassProc = CallbackDepth.WrapSubclassProc(WindowSubclassProc);
        _hWnd = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        AppBranding.ApplyWindowIcon(_appWindow);
        InstallMinimumSizeHook();

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = !_isQuickJotMode;
            presenter.IsMinimizable = !_isQuickJotMode;
            presenter.IsAlwaysOnTop = _isQuickJotMode || startAlwaysOnTop;
        }

        _autoSaveTimer = DispatcherQueue.CreateTimer();
        _autoSaveTimer.Interval = TimeSpan.FromMilliseconds(AutoSaveDelayMs);
        _autoSaveTimer.IsRepeating = false;
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;

        _themeService.TrackWindow(this);
        _localizationService.LanguageChanged += OnLanguageChanged;
        _settingsService.SettingsChanged += OnSettingsChanged;
        _quickCaptureService.Changed += OnQuickCaptureChanged;
        RootGrid.ActualThemeChanged += RootGrid_ActualThemeChanged;
        AppTitleBar.ActualThemeChanged += AppTitleBar_ActualThemeChanged;
        Closed += OnWindowClosed;

        PopoutMarkdownView.AttachmentResolver = ResolveAttachmentPath;
        PopoutMarkdownView.AttachmentOpenRequested += PopoutMarkdownView_AttachmentOpenRequested;
        _readerDoubleTappedHandler = new DoubleTappedEventHandler(ReaderEditorHost_DoubleTapped);
        ReaderEditorHost.AddHandler(UIElement.DoubleTappedEvent, _readerDoubleTappedHandler, handledEventsToo: true);

        PopoutMarkdownEditor.TextResolver = _localizationService.T;
        PopoutMarkdownEditor.EditorTextChanged += PopoutMarkdownEditor_EditorTextChanged;
        PopoutMarkdownEditor.CommitRequested += PopoutMarkdownEditor_CommitRequested;
        PopoutMarkdownEditor.CancelRequested += PopoutMarkdownEditor_CancelRequested;
        PopoutMarkdownEditor.RichPasteRequested += PopoutMarkdownEditor_RichPasteRequested;

        _isEditing = _isQuickJotMode || startEditing;
        ResizeAndPosition(windowId);
        ApplyTitleBarColors();
        ApplyEditorSettings();
        ApplyLocalizedText();
        UpdateAlwaysOnTopVisual();

        if (_isQuickJotMode)
        {
            _contentFormat = ResolvePreferredEditorFormat();
            ApplyMaterialSurface();
            RefreshPresentation();
        }
        else
        {
            _ = LoadItemAsync(initialLoad: true);
        }
    }

    public IntPtr WindowHandle => _hWnd;

    public string? ItemId => _itemId;

    public bool IsQuickJot => _isQuickJotMode;

    public bool IsClosed => _isClosed;

    public bool IsAlwaysOnTop =>
        _appWindow.Presenter is OverlappedPresenter { IsAlwaysOnTop: true };

    public event EventHandler? WindowClosed;

    public void SetAlwaysOnTop(bool alwaysOnTop)
    {
        if (_isClosed)
        {
            return;
        }

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = alwaysOnTop;
        }

        UpdateAlwaysOnTopVisual();
    }

    public void ShowAndFocus(IntPtr previousForegroundHwnd = default)
    {
        if (_isClosed)
        {
            return;
        }

        if (previousForegroundHwnd != IntPtr.Zero && previousForegroundHwnd != _hWnd)
        {
            _previousForegroundHwnd = previousForegroundHwnd;
        }

        _appWindow.Show();
        if (!IsAlwaysOnTop)
        {
            Win32Helper.BringWindowTemporarilyToFront(_hWnd);
        }

        Activate();
        _ = Win32Helper.SetForegroundWindow(_hWnd);

        if (_isEditing)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isClosed && _isEditing)
                {
                    PopoutMarkdownEditor.FocusEditor(moveCaretToEnd: _isQuickJotMode);
                }
            });
        }
    }

    private async Task LoadItemAsync(bool initialLoad)
    {
        if (_isClosed || string.IsNullOrWhiteSpace(_itemId))
        {
            return;
        }

        QuickCaptureStoreData data = await _quickCaptureService.GetDataAsync();
        if (_isClosed)
        {
            return;
        }

        QuickCaptureItem? found = data.Items.FirstOrDefault(candidate =>
            !candidate.IsDeleted &&
            string.Equals(candidate.Id, _itemId, StringComparison.Ordinal));
        if (found is null)
        {
            Close();
            return;
        }

        _item = found;
        if (initialLoad || !_hasUnsavedChanges)
        {
            _appearancePreset = found.AppearancePreset;
            _contentFormat = _isEditing ? ResolvePreferredEditorFormat() : found.ContentFormat;
            if (_isEditing)
            {
                SetEditorText(found.Body);
            }
        }

        ApplyLocalizedText();
        ApplyMaterialSurface();
        RefreshAttachments();
        RefreshPresentation();
    }

    private void OnQuickCaptureChanged()
    {
        if (_isClosed || _isQuickJotMode)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isClosed && !_hasUnsavedChanges)
            {
                _ = LoadItemAsync(initialLoad: false);
            }
        });
    }

    private void OnSettingsChanged()
    {
        if (_isClosed)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isClosed)
            {
                ApplyEditorSettings();
                RefreshPresentation();
            }
        });
    }

    private void OnLanguageChanged()
    {
        if (_isClosed)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isClosed)
            {
                ApplyLocalizedText();
                UpdateAlwaysOnTopVisual();
            }
        });
    }

    private void ApplyEditorSettings()
    {
        var qc = _settingsService.Settings.QuickCapture;
        double contentFontSize = SettingsService.NormalizeTextSize(
            qc.QuickCaptureContentTextSize > 0
                ? qc.QuickCaptureContentTextSize
                : _settingsService.Settings.WidgetShell.TextSize);
        PopoutMarkdownView.FontSize = contentFontSize;
        PopoutMarkdownView.AllowRemoteImages = qc.QuickCaptureAllowRemoteImages;
        PopoutMarkdownEditor.EditorFontSize = contentFontSize;
        PopoutMarkdownEditor.EditorEnterBehavior = _isQuickJotMode
            ? SettingsService.EditorEnterBehaviorCtrlEnterSaves
            : SettingsService.NormalizeEditorEnterBehavior(qc.QuickCaptureEditorEnterBehavior);
    }

    private TextContentFormat ResolvePreferredEditorFormat()
    {
        return string.Equals(
            SettingsService.NormalizeQuickCaptureFormat(
                _settingsService.Settings.QuickCapture.QuickCaptureDefaultFormat),
            SettingsService.QuickCaptureFormatPlainText,
            StringComparison.Ordinal)
            ? TextContentFormat.PlainText
            : TextContentFormat.Markdown;
    }

    private void ApplyLocalizedText()
    {
        if (_isQuickJotMode)
        {
            Title = _localizationService.T("QuickCapture.QuickJotTitle");
            WindowTitleText.Text = Title;
            PopoutMarkdownEditor.PlaceholderText = _localizationService.T("QuickCapture.QuickJotPlaceholder");
            QuickJotHintText.Text = _localizationService.T("QuickCapture.QuickJotHint");
            QuickJotSaveButton.Content = _localizationService.T("Common.Save");
        }
        else
        {
            string noteTitle = _item is not null
                ? ResolveWindowTitle(_item)
                : _localizationService.T("QuickCapture.Name");
            Title = noteTitle;
            WindowTitleText.Text = noteTitle;
            PopoutMarkdownEditor.PlaceholderText = _localizationService.T("QuickCapture.Detail.BodyPlaceholder");
        }

        SetButtonAccessibility(EditButton, _localizationService.T("QuickCapture.Edit"));
        SetButtonAccessibility(DoneButton, _localizationService.T("Common.Save"));
        SetButtonAccessibility(AppearanceButton, _localizationService.T("QuickCapture.Detail.Appearance"));
        SetButtonAccessibility(AddAttachmentButton, _localizationService.T("QuickCapture.AddFile"));
        SetButtonAccessibility(CopyButton, _localizationService.T("Common.Copy"));
        PopoutAttachmentStrip.RemoveAutomationName = _localizationService.T("QuickCapture.Detail.RemoveAttachment");
    }

    private string ResolveWindowTitle(QuickCaptureItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Title))
        {
            return item.Title.Trim();
        }

        string plain = item.ContentFormat == TextContentFormat.Markdown
            ? _markdownService.ToPlainText(item.Body ?? string.Empty)
            : (item.Body ?? string.Empty);
        string? firstLine = plain
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0);
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return _localizationService.T("QuickCapture.Name");
        }

        return firstLine.Length > 36 ? $"{firstLine[..36]}…" : firstLine;
    }

    private void UpdateAlwaysOnTopVisual()
    {
        bool pinnedTop = IsAlwaysOnTop;
        AlwaysOnTopIcon.IsPinned = pinnedTop;
        string tooltip = _localizationService.T(pinnedTop
            ? "QuickCapture.UnpinOnTop"
            : "QuickCapture.PinOnTop");
        SetButtonAccessibility(AlwaysOnTopButton, tooltip);
    }

    private static void SetButtonAccessibility(Button button, string text)
    {
        AutomationProperties.SetName(button, text);
        ToolTipService.SetToolTip(button, text);
    }

    private void ApplyMaterialSurface()
    {
        bool isDark = RootGrid.ActualTheme == ElementTheme.Dark;
        ContentMaterialSurface.Background =
            QuickCaptureMaterialBrushCache.GetSurfaceBrush(_appearancePreset, isDark) ??
            NeutralInteractionBrush.ResolveThemedResource("CardBackgroundFillColorDefaultBrush", RootGrid) ??
            new SolidColorBrush(Colors.Transparent);
    }

    private void RefreshPresentation()
    {
        if (_isClosed)
        {
            return;
        }

        if (_isQuickJotMode)
        {
            EditButton.Visibility = Visibility.Collapsed;
            DoneButton.Visibility = Visibility.Collapsed;
            CopyButton.Visibility = Visibility.Collapsed;
            AddAttachmentButton.Visibility = Visibility.Visible;
            QuickJotHintText.Visibility = Visibility.Visible;
            QuickJotSaveButton.Visibility = Visibility.Visible;
            PopoutMarkdownView.Visibility = Visibility.Collapsed;
            PopoutMarkdownEditor.Visibility = Visibility.Visible;
            PopoutMarkdownEditor.ShowFormattingToolbar = _contentFormat == TextContentFormat.Markdown;
            StatusTimestampText.Text = _pendingQuickJotAttachments.Count > 0
                ? $"📎 {_pendingQuickJotAttachments.Count}"
                : string.Empty;
            RefreshAttachments();
            return;
        }

        QuickJotHintText.Visibility = Visibility.Collapsed;
        QuickJotSaveButton.Visibility = Visibility.Collapsed;
        EditButton.Visibility = !_isEditing ? Visibility.Visible : Visibility.Collapsed;
        DoneButton.Visibility = _isEditing ? Visibility.Visible : Visibility.Collapsed;
        AddAttachmentButton.Visibility = _isEditing ? Visibility.Visible : Visibility.Collapsed;
        CopyButton.Visibility = Visibility.Visible;

        string currentBody = _isEditing
            ? PopoutMarkdownEditor.Text
            : (_item?.Body ?? string.Empty);
        bool hasPrimaryImage = _item?.Type == QuickCaptureItemType.Image &&
                               _item.SourceKind is QuickCaptureSourceKind.Clipboard or QuickCaptureSourceKind.Image &&
                               !string.IsNullOrWhiteSpace(_item.ImagePath) &&
                               File.Exists(_item.ImagePath);
        bool showText = _isEditing ||
                        !hasPrimaryImage ||
                        (!string.IsNullOrWhiteSpace(currentBody) &&
                         !string.Equals(currentBody.Trim(), "Image", StringComparison.OrdinalIgnoreCase));

        PrimaryImageHost.Visibility = hasPrimaryImage ? Visibility.Visible : Visibility.Collapsed;
        PrimaryImageRow.Height = hasPrimaryImage
            ? new GridLength(showText ? 2 : 1, GridUnitType.Star)
            : new GridLength(0);
        BodyRow.Height = showText ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        PopoutMarkdownView.Markdown = currentBody;
        PopoutMarkdownView.ContentFormat = _contentFormat;
        PopoutMarkdownView.AreTaskListsInteractive = _contentFormat == TextContentFormat.Markdown;
        PopoutMarkdownView.Visibility = !_isEditing && showText ? Visibility.Visible : Visibility.Collapsed;

        PopoutMarkdownEditor.ShowFormattingToolbar = _contentFormat == TextContentFormat.Markdown;
        PopoutMarkdownEditor.Visibility = _isEditing && showText ? Visibility.Visible : Visibility.Collapsed;

        if (_item is not null)
        {
            StatusTimestampText.Text = _localizationService.Format(
                "QuickCapture.Detail.Created",
                _item.CreatedAt.ToLocalTime().ToString("yyyy/M/d HH:mm"));
        }

        _ = LoadPrimaryImageAsync(hasPrimaryImage ? _item!.ImagePath : null);
    }

    private void RefreshAttachments()
    {
        IReadOnlyList<TodoAttachmentViewModel> attachments = _isQuickJotMode
            ? _pendingQuickJotAttachments
                .Select(file => new TodoAttachmentViewModel(new TodoAttachment
                {
                    FilePath = file.Path,
                    DisplayName = file.DisplayName,
                    Type = AttachmentStorageService.GetAttachmentType(file.Path)
                }))
                .ToArray()
            : (_item?.Attachments ?? [])
                .Select(att => new TodoAttachmentViewModel(att, TodoAttachmentViewModel.QuickCapturePreviewCacheScope))
                .ToArray();

        PopoutAttachmentStrip.ItemsSource = attachments.Cast<object>().ToArray();
        PopoutAttachmentStrip.CanRemove = _isEditing;
        PopoutAttachmentStrip.Visibility = attachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadPrimaryImageAsync(string? path)
    {
        long version = ++_imageLoadVersion;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            PrimaryImage.Source = null;
            return;
        }

        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var bitmap = new BitmapImage { DecodePixelWidth = ImageDecodeWidth };
            await bitmap.SetSourceAsync(stream);
            if (!_isClosed && version == _imageLoadVersion)
            {
                PrimaryImage.Source = bitmap;
            }
        }
        catch
        {
            if (!_isClosed && version == _imageLoadVersion)
            {
                PrimaryImage.Source = null;
            }
        }
    }

    private string? ResolveAttachmentPath(string attachmentId) =>
        _item?.Attachments.FirstOrDefault(att =>
            string.Equals(att.Id, attachmentId, StringComparison.Ordinal))?.FilePath;

    private void SetEditorText(string value)
    {
        _suppressEditorChanges = true;
        try
        {
            PopoutMarkdownEditor.Text = value ?? string.Empty;
        }
        finally
        {
            _suppressEditorChanges = false;
        }
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        BeginEditing();
    }

    private void ReaderEditorHost_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (!_isEditing && BeginEditing())
        {
            e.Handled = true;
        }
    }

    private bool BeginEditing()
    {
        if (_isEditing || _item is null)
        {
            return false;
        }

        _contentFormat = ResolvePreferredEditorFormat();
        SetEditorText(_item.Body);
        _isEditing = true;
        RefreshAttachments();
        RefreshPresentation();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isClosed && _isEditing)
            {
                PopoutMarkdownEditor.FocusEditor(moveCaretToEnd: false);
            }
        });
        return true;
    }

    private async void DoneButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveAsync(completeEditing: true);
    }

    private async void QuickJotSaveButton_Click(object sender, RoutedEventArgs e)
    {
        await CommitQuickJotAndCloseAsync();
    }

    private void AlwaysOnTopButton_Click(object sender, RoutedEventArgs e)
    {
        SetAlwaysOnTop(!IsAlwaysOnTop);
    }

    private void AppearanceButton_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout { ShouldConstrainToRootBounds = false };
        foreach ((QuickCaptureAppearancePreset preset, string key) in new[]
                 {
                     (QuickCaptureAppearancePreset.Default, "QuickCapture.Material.Default"),
                     (QuickCaptureAppearancePreset.Paper, "QuickCapture.Material.Paper"),
                     (QuickCaptureAppearancePreset.StickyYellow, "QuickCapture.Material.Yellow"),
                     (QuickCaptureAppearancePreset.Rose, "QuickCapture.Material.Rose"),
                     (QuickCaptureAppearancePreset.Mint, "QuickCapture.Material.Mint"),
                     (QuickCaptureAppearancePreset.MistBlue, "QuickCapture.Material.Blue")
                 })
        {
            var menuItem = new ToggleMenuFlyoutItem
            {
                Text = _localizationService.T(key),
                IsChecked = _appearancePreset == preset
            };
            menuItem.Click += async (_, _) =>
            {
                _appearancePreset = preset;
                ApplyMaterialSurface();
                if (!_isQuickJotMode && _item is not null)
                {
                    _hasUnsavedChanges = true;
                    await SaveAsync(completeEditing: false);
                }
            };
            flyout.Items.Add(menuItem);
        }

        flyout.ShowAt(AppearanceButton);
    }

    private async void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_item is null)
        {
            return;
        }

        string text = _isEditing ? PopoutMarkdownEditor.Text : _item.Body;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var pkg = new DataPackage();
        DeskBoxClipboardWriteScope.MarkWrite(text: text);
        pkg.SetText(text);
        Clipboard.SetContent(pkg);
        Clipboard.Flush();
    }

    private async void AddAttachmentButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            IReadOnlyList<string> picked = await FileOpenPickerService.PickMultipleFilesAsync(
                _hWnd,
                [],
                PickerLocationId.Desktop);
            DroppedFilePath[] files = picked
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Select(path => new DroppedFilePath(path, Path.GetFileName(path), ForceManagedCopy: false))
                .ToArray();
            if (files.Length == 0)
            {
                return;
            }

            await AttachDroppedFilesAsync(files);
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapturePopout] Add attachment failed: {ex.Message}");
        }
    }

    private async Task AttachDroppedFilesAsync(IReadOnlyList<DroppedFilePath> files)
    {
        if (files.Count == 0)
        {
            return;
        }

        if (_isQuickJotMode)
        {
            foreach (DroppedFilePath file in files)
            {
                if (!_pendingQuickJotAttachments.Any(existing =>
                        string.Equals(existing.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    _pendingQuickJotAttachments.Add(file);
                }
            }

            RefreshPresentation();
            return;
        }

        if (_item is not null)
        {
            bool copyLinked = SettingsService.NormalizeAttachmentStorageMode(
                _settingsService.Settings.QuickCapture.AttachmentStorageMode) == SettingsService.AttachmentStorageModeCopy;
            string[] regularPaths = files.Where(f => !f.ForceManagedCopy).Select(f => f.Path).ToArray();
            string[] managedPaths = files.Where(f => f.ForceManagedCopy).Select(f => f.Path).ToArray();
            QuickCaptureItem? updated = null;
            if (regularPaths.Length > 0)
            {
                updated = await _quickCaptureService.AddAttachmentsAsync(_item.Id, regularPaths, copyLinked);
            }

            if (managedPaths.Length > 0)
            {
                updated = await _quickCaptureService.AddAttachmentsAsync(_item.Id, managedPaths, copyToManagedStorage: true) ?? updated;
            }

            if (updated is not null)
            {
                _item = updated;
                RefreshAttachments();
                RefreshPresentation();
            }
        }
    }

    private void PopoutMarkdownEditor_EditorTextChanged(object? sender, EventArgs e)
    {
        if (_suppressEditorChanges || !_isEditing || _isQuickJotMode)
        {
            return;
        }

        _hasUnsavedChanges = true;
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private async void AutoSaveTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (!_isClosed && !_isQuickJotMode && _hasUnsavedChanges)
        {
            await SaveAsync(completeEditing: false);
        }
    }

    private async void PopoutMarkdownEditor_CommitRequested(object? sender, EventArgs e)
    {
        if (_isQuickJotMode)
        {
            await CommitQuickJotAndCloseAsync();
            return;
        }

        await SaveAsync(completeEditing: true);
    }

    private async void PopoutMarkdownEditor_CancelRequested(object? sender, EventArgs e)
    {
        if (_isQuickJotMode)
        {
            if (string.IsNullOrWhiteSpace(PopoutMarkdownEditor.Text) && _pendingQuickJotAttachments.Count == 0)
            {
                CloseAndRestorePreviousFocus();
            }

            return;
        }

        if (_isEditing)
        {
            await SaveAsync(completeEditing: true);
        }
    }

    private async void PopoutMarkdownEditor_RichPasteRequested(
        object? sender,
        MarkdownClipboardPasteEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            if (DeskBoxDragData.HasDroppedFiles(e.DataView))
            {
                using DroppedFileBatch batch = await DeskBoxDragData.TryGetDroppedFilesAsync(e.DataView);
                if (batch.Files.Count > 0)
                {
                    await AttachDroppedFilesAsync(batch.Files);
                    e.Handled = true;
                    return;
                }
            }

            byte[]? pngBytes = await QuickCaptureService.TryExtractClipboardPngBytesAsync(e.DataView);
            if (pngBytes is { Length: > 0 })
            {
                if (_isQuickJotMode)
                {
                    DroppedFilePath? tempFile =
                        await QuickCaptureWidgetViewModel.SaveClipboardImageBytesToTempFileAsync(pngBytes);
                    if (tempFile is not null)
                    {
                        _pendingQuickJotAttachments.Add(tempFile);
                        RefreshPresentation();
                        e.Handled = true;
                    }
                }
                else if (_item is not null)
                {
                    string displayName = $"Screenshot_{DateTimeOffset.Now:yyyyMMdd_HHmmss}.png";
                    QuickCaptureItem? updated =
                        await _quickCaptureService.AddImageBytesAttachmentAsync(_item.Id, pngBytes, displayName);
                    if (updated is not null)
                    {
                        _item = updated;
                        RefreshAttachments();
                        RefreshPresentation();
                        e.Handled = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapturePopout] Rich paste failed: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task<bool> SaveAsync(bool completeEditing)
    {
        if (_isQuickJotMode || _item is null)
        {
            return false;
        }

        _autoSaveTimer.Stop();
        await _saveGate.WaitAsync();
        try
        {
            string body = _isEditing ? PopoutMarkdownEditor.Text : _item.Body;
            if (!string.IsNullOrWhiteSpace(body))
            {
                QuickCaptureWriteResult result = await _quickCaptureService.UpdateItemDetailsWithResultAsync(
                    _item.Id,
                    _item.Title,
                    body,
                    _appearancePreset,
                    _contentFormat);
                if (result.Saved && result.Item is not null)
                {
                    _item = result.Item;
                    _hasUnsavedChanges = false;
                }
            }

            if (completeEditing)
            {
                _isEditing = false;
                SetEditorText(string.Empty);
            }

            ApplyLocalizedText();
            ApplyMaterialSurface();
            RefreshAttachments();
            RefreshPresentation();
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapturePopout] Save failed: {ex.Message}");
            return false;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task CommitQuickJotAndCloseAsync()
    {
        string body = PopoutMarkdownEditor.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(body) && _pendingQuickJotAttachments.Count == 0)
        {
            CloseAndRestorePreviousFocus();
            return;
        }

        await _saveGate.WaitAsync();
        try
        {
            if (_pendingQuickJotAttachments.Count > 0)
            {
                bool copyLinked = SettingsService.NormalizeAttachmentStorageMode(
                    _settingsService.Settings.QuickCapture.AttachmentStorageMode) == SettingsService.AttachmentStorageModeCopy;
                string[] regularPaths = _pendingQuickJotAttachments.Where(f => !f.ForceManagedCopy).Select(f => f.Path).ToArray();
                string[] managedPaths = _pendingQuickJotAttachments.Where(f => f.ForceManagedCopy).Select(f => f.Path).ToArray();
                QuickCaptureItem? created = null;
                if (regularPaths.Length > 0)
                {
                    created = await _quickCaptureService.AddItemWithAttachmentsAsync(regularPaths, copyLinked, pin: false);
                }

                if (managedPaths.Length > 0)
                {
                    created = created is null
                        ? await _quickCaptureService.AddItemWithAttachmentsAsync(managedPaths, copyToManagedStorage: true, pin: false)
                        : await _quickCaptureService.AddAttachmentsAsync(created.Id, managedPaths, copyToManagedStorage: true) ?? created;
                }
                if (created is not null && !string.IsNullOrWhiteSpace(body))
                {
                    await _quickCaptureService.UpdateItemDetailsWithResultAsync(
                        created.Id,
                        null,
                        body,
                        _appearancePreset,
                        _contentFormat);
                }
                else if (created is not null && _appearancePreset != QuickCaptureAppearancePreset.Default)
                {
                    await _quickCaptureService.SetAppearanceAsync([created.Id], _appearancePreset);
                }
            }
            else
            {
                await _quickCaptureService.AddDetailedItemWithResultAsync(
                    null,
                    body,
                    _appearancePreset,
                    pin: false,
                    contentFormat: _contentFormat);
            }

            _pendingQuickJotAttachments.Clear();
            SetEditorText(string.Empty);
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapturePopout] Quick Jot commit failed: {ex.Message}");
        }
        finally
        {
            _saveGate.Release();
        }

        CloseAndRestorePreviousFocus();
    }

    private void CloseAndRestorePreviousFocus()
    {
        IntPtr prev = _previousForegroundHwnd;
        Close();
        if (prev != IntPtr.Zero && prev != _hWnd && Win32Helper.IsWindow(prev))
        {
            _ = Win32Helper.SetForegroundWindow(prev);
        }
    }

    private async void PopoutMarkdownView_TaskToggleRequested(
        object? sender,
        MarkdownTaskToggleRequestedEventArgs e)
    {
        if (_item is null ||
            _contentFormat != TextContentFormat.Markdown ||
            !_markdownService.TryToggleTask(_item.Body, e.TaskIndex, out string updated))
        {
            return;
        }

        QuickCaptureWriteResult res = await _quickCaptureService.UpdateItemDetailsWithResultAsync(
            _item.Id,
            _item.Title,
            updated,
            _appearancePreset,
            _contentFormat);
        if (res.Saved && res.Item is not null)
        {
            _item = res.Item;
            RefreshPresentation();
        }
    }

    private async void PopoutMarkdownView_AttachmentOpenRequested(
        object? sender,
        MarkdownAttachmentRequestedEventArgs e)
    {
        string? path = ResolveAttachmentPath(e.AttachmentId);
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            await LaunchAttachmentFileAsync(path);
        }
    }

    private async void PopoutAttachmentStrip_OpenRequested(object? sender, AttachmentTileEventArgs e)
    {
        if (File.Exists(e.Attachment.FilePath))
        {
            await LaunchAttachmentFileAsync(e.Attachment.FilePath);
        }
    }

    private async void PopoutAttachmentStrip_RemoveRequested(object? sender, AttachmentTileEventArgs e)
    {
        if (!_isEditing)
        {
            return;
        }

        if (_isQuickJotMode)
        {
            _pendingQuickJotAttachments.RemoveAll(f =>
                string.Equals(f.Path, e.Attachment.FilePath, StringComparison.OrdinalIgnoreCase));
            RefreshPresentation();
            return;
        }

        if (_item is not null)
        {
            QuickCaptureItem? updated = await _quickCaptureService.DeleteAttachmentAsync(_item.Id, e.Attachment.Id);
            if (updated is not null)
            {
                _item = updated;
                RefreshAttachments();
                RefreshPresentation();
            }
        }
    }

    private static async Task LaunchAttachmentFileAsync(string path)
    {
        try
        {
            bool launched = false;
            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(path);
                launched = await Launcher.LaunchFileAsync(file);
            }
            catch
            {
            }

            if (!launched)
            {
                Win32Helper.OpenFileOrChooseApp(IntPtr.Zero, path);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapturePopout] Open attachment failed: {ex.Message}");
        }
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.Handled = true;
        }
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        if (!DeskBoxDragData.HasDroppedFiles(e.DataView))
        {
            return;
        }

        e.Handled = true;
        var deferral = e.GetDeferral();
        try
        {
            using DroppedFileBatch batch = await DeskBoxDragData.TryGetDroppedFilesAsync(e.DataView);
            await AttachDroppedFilesAsync(batch.Files);
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCapturePopout] File drop failed: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = Win32Helper.IsKeyPressed(VirtualKey.Control);
        if (_isQuickJotMode && ctrl && e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await CommitQuickJotAndCloseAsync();
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            if (_isQuickJotMode)
            {
                if (string.IsNullOrWhiteSpace(PopoutMarkdownEditor.Text) && _pendingQuickJotAttachments.Count == 0)
                {
                    e.Handled = true;
                    CloseAndRestorePreviousFocus();
                }
            }
            else if (_isEditing)
            {
                e.Handled = true;
                await SaveAsync(completeEditing: true);
            }
        }
    }

    private void RootGrid_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!Win32Helper.IsKeyPressed(VirtualKey.Control))
        {
            return;
        }

        int delta = e.GetCurrentPoint(RootGrid).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        var qc = _settingsService.Settings.QuickCapture;
        double current = SettingsService.NormalizeTextSize(
            qc.QuickCaptureContentTextSize > 0
                ? qc.QuickCaptureContentTextSize
                : _settingsService.Settings.WidgetShell.TextSize);
        double next = SettingsService.NormalizeTextSize(current + (delta > 0 ? 1.0 : -1.0));
        if (Math.Abs(next - current) > 0.01)
        {
            qc.QuickCaptureContentTextSize = next;
            _settingsService.SaveDebounced();
        }

        e.Handled = true;
    }

    private void RootGrid_ActualThemeChanged(FrameworkElement sender, object args)
    {
        ApplyTitleBarColors();
        ApplyMaterialSurface();
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

    private void ResizeAndPosition(WindowId windowId)
    {
        DisplayArea displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        Windows.Graphics.RectInt32 workArea = displayArea.WorkArea;
        double scale = Win32Helper.GetDpiScaleForWindow(_hWnd, RootGrid.XamlRoot);

        int desiredW = _isQuickJotMode ? QuickJotDesiredWidth : PopoutDesiredWidth;
        int desiredH = _isQuickJotMode ? QuickJotDesiredHeight : PopoutDesiredHeight;
        int minW = _isQuickJotMode ? QuickJotMinWidth : PopoutMinWidth;
        int minH = _isQuickJotMode ? QuickJotMinHeight : PopoutMinHeight;

        int width = Math.Clamp(
            ToPhysicalPixels(desiredW, scale),
            ToPhysicalPixels(minW, scale),
            Math.Max(ToPhysicalPixels(minW, scale), workArea.Width - ToPhysicalPixels(48, scale)));
        int height = Math.Clamp(
            ToPhysicalPixels(desiredH, scale),
            ToPhysicalPixels(minH, scale),
            Math.Max(ToPhysicalPixels(minH, scale), workArea.Height - ToPhysicalPixels(48, scale)));

        int x = workArea.X + Math.Max(0, (workArea.Width - width) / 2);
        int y = _isQuickJotMode
            ? workArea.Y + Math.Max(0, (int)((workArea.Height - height) * 0.30))
            : workArea.Y + Math.Max(0, (workArea.Height - height) / 2);

        _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
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
            int minW = _isQuickJotMode ? QuickJotMinWidth : PopoutMinWidth;
            int minH = _isQuickJotMode ? QuickJotMinHeight : PopoutMinHeight;
            minMaxInfo.MinTrackSize.X = Math.Max(minMaxInfo.MinTrackSize.X, ToPhysicalPixels(minW, scale));
            minMaxInfo.MinTrackSize.Y = Math.Max(minMaxInfo.MinTrackSize.Y, ToPhysicalPixels(minH, scale));
            Marshal.StructureToPtr(minMaxInfo, lParam, false);
            return IntPtr.Zero;
        }

        if (message == WmNcDestroy)
        {
            RemoveMinimumSizeHook();
        }

        return Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_isClosed)
        {
            return;
        }

        _isClosed = true;
        _autoSaveTimer.Stop();
        _autoSaveTimer.Tick -= AutoSaveTimer_Tick;

        if (!_isQuickJotMode && _hasUnsavedChanges && _item is not null)
        {
            string body = PopoutMarkdownEditor.Text ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    await _quickCaptureService.UpdateItemDetailsWithResultAsync(
                        _item.Id,
                        _item.Title,
                        body,
                        _appearancePreset,
                        _contentFormat);
                }
                catch
                {
                }
            }
        }

        WindowsCompatibilityService.ReleaseTitleBarExtensions(this, _appWindow);
        _localizationService.LanguageChanged -= OnLanguageChanged;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _quickCaptureService.Changed -= OnQuickCaptureChanged;
        RootGrid.ActualThemeChanged -= RootGrid_ActualThemeChanged;
        AppTitleBar.ActualThemeChanged -= AppTitleBar_ActualThemeChanged;
        Closed -= OnWindowClosed;

        if (_readerDoubleTappedHandler is not null)
        {
            ReaderEditorHost.RemoveHandler(UIElement.DoubleTappedEvent, _readerDoubleTappedHandler);
            _readerDoubleTappedHandler = null;
        }

        PopoutMarkdownView.AttachmentOpenRequested -= PopoutMarkdownView_AttachmentOpenRequested;
        PopoutMarkdownView.AttachmentResolver = null;
        PopoutMarkdownView.ReleaseVisualHandlers();

        PopoutMarkdownEditor.EditorTextChanged -= PopoutMarkdownEditor_EditorTextChanged;
        PopoutMarkdownEditor.CommitRequested -= PopoutMarkdownEditor_CommitRequested;
        PopoutMarkdownEditor.CancelRequested -= PopoutMarkdownEditor_CancelRequested;
        PopoutMarkdownEditor.RichPasteRequested -= PopoutMarkdownEditor_RichPasteRequested;
        PopoutMarkdownEditor.ReleaseVisualHandlers();
        PopoutAttachmentStrip.ReleaseVisualHandlers();
        PrimaryImage.Source = null;

        RemoveMinimumSizeHook();
        WindowClosed?.Invoke(this, EventArgs.Empty);
    }

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
