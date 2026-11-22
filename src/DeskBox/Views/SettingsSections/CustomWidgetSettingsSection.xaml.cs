using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.WinUI.Controls;
using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;

namespace DeskBox.Views.SettingsSections;

public sealed partial class CustomWidgetSettingsSection : UserControl
{
    private sealed record InstanceCardMetadata(
        string IconGlyph,
        string Description,
        IReadOnlyList<CustomWidgetSettingDefinition> Settings,
        IReadOnlyDictionary<string, string> SavedValues,
        bool UsesAiCapability = false,
        CustomWidgetManifest? Manifest = null,
        string? LogicJs = null);

    private readonly struct LocalizationAccess(CustomWidgetSettingsSection owner)
    {
        public string CurrentCultureName => AiProviderConfigFormController.ResolveCultureName(owner._localization);
        public string T(string key) => AiProviderConfigFormController.Translate(owner._localization, key);
        public string Format(string key, params object[] args) => AiProviderConfigFormController.Format(owner._localization, key, args);
    }

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private ISettingsWidgetActions _widgetActions = null!;
    private LocalizationService? _localization;
    private IntPtr _ownerWindow;
    private string? _highlightedWidgetId;
    private string? _detailWidgetId;
    private string? _pendingDetailWidgetId;
    private string? _pendingAiWizardWidgetId;
    private FileSystemWatcher? _detailWatcher;
    private CancellationTokenSource? _detailWatchDebounceCts;
    private long _lastLocalDetailWriteTick;
    private readonly List<Action<IReadOnlyDictionary<string, string>>> _detailSyncHandlers = [];
    private bool _isSectionLoaded;
    private bool _isDialogOpen;
    private string _instanceStateSignature = string.Empty;

    public CustomWidgetSettingsSection()
    {
        InitializeComponent();
        StudioStatusInfoBar.RegisterPropertyChangedCallback(
            InfoBar.IsOpenProperty,
            static (sender, _) =>
            {
                if (sender is InfoBar bar)
                {
                    bar.Visibility = bar.IsOpen ? Visibility.Visible : Visibility.Collapsed;
                }
            });
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnActualThemeChanged;
        RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged);
    }

    public event EventHandler? SubPageChanged;

    public bool IsDetailViewOpen =>
        !string.IsNullOrWhiteSpace(_detailWidgetId);

    public string? ActiveDetailTitle { get; private set; }

    private LocalizationAccess Localization => new(this);

    public void Configure(ISettingsWidgetActions widgetActions, IntPtr ownerWindow, LocalizationService? localization = null)
    {
        _widgetActions = widgetActions;
        _ownerWindow = ownerWindow;
        if (localization is not null && !ReferenceEquals(_localization, localization))
        {
            if (_localization is not null)
            {
                _localization.LanguageChanged -= OnLanguageChanged;
            }
            _localization = localization;
            if (_isSectionLoaded)
            {
                _localization.LanguageChanged -= OnLanguageChanged;
                _localization.LanguageChanged += OnLanguageChanged;
            }
        }
    }

    public void SelectWidget(string widgetId)
    {
        if (!string.IsNullOrWhiteSpace(widgetId))
        {
            _highlightedWidgetId = widgetId;
        }
    }

    public void StageDeepLink(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return;
        }

        if (payload.StartsWith("select:", StringComparison.OrdinalIgnoreCase))
        {
            string id = payload["select:".Length..].Trim();
            if (!string.IsNullOrWhiteSpace(id))
            {
                SelectWidget(id);
                _pendingDetailWidgetId = id;
            }
        }
        else if (payload.StartsWith("ai:", StringComparison.OrdinalIgnoreCase))
        {
            string id = payload["ai:".Length..].Trim();
            if (!string.IsNullOrWhiteSpace(id))
            {
                SelectWidget(id);
                _pendingAiWizardWidgetId = id;
            }
        }
        else if (string.Equals(payload, "ai-new", StringComparison.OrdinalIgnoreCase))
        {
            _pendingAiWizardWidgetId = "__new__";
        }
        else if (string.Equals(payload, "agent-hub", StringComparison.OrdinalIgnoreCase))
        {
            NavigateToAiSettingsRequested?.Invoke(this, true);
        }
        else if (string.Equals(payload, "ai-settings", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(payload, "AiSettings", StringComparison.OrdinalIgnoreCase))
        {
            NavigateToAiSettingsRequested?.Invoke(this, false);
        }
    }

    public bool TryNavigateBackToStudio()
    {
        if (!IsDetailViewOpen)
        {
            return false;
        }

        CloseDetailView(animate: true);
        return true;
    }

    public void CloseDetailView(bool animate = true)
    {
        if (!IsDetailViewOpen &&
            DetailViewPanel.Visibility == Visibility.Collapsed)
        {
            return;
        }

        DisposeDetailWatcher();
        _detailSyncHandlers.Clear();
        _detailWidgetId = null;
        ActiveDetailTitle = null;
        StudioStatusInfoBar.IsOpen = false;

        DetailViewPanel.Visibility = Visibility.Collapsed;
        DetailViewPanel.Children.Clear();
        StudioViewPanel.Visibility = Visibility.Visible;

        SubPageChanged?.Invoke(this, EventArgs.Empty);

        if (animate && IsLoaded)
        {
            StudioViewPanel.UpdateLayout();
            DetailPageTransitionHelper.PlayNavigationEnter(StudioViewPanel, -40f);
        }

        _ = RefreshStudioAsync();
    }

    public void RefreshInstances()
    {
        _ = RefreshFromStoreAsync();
    }

    public async Task RefreshFromStoreAsync()
    {
        await RefreshStudioAsync();
        await ProcessPendingDeepLinkModalAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _isSectionLoaded = true;
            SettingsWindow.ApplyNoticeMessageTypography(StudioStatusInfoBar);
            SettingsWindow.EnsureInformationalSeverityPinned(StudioStatusInfoBar);
            if (_localization is not null)
            {
                _localization.LanguageChanged -= OnLanguageChanged;
                _localization.LanguageChanged += OnLanguageChanged;
            }
            _ = CustomWidgetRemotePromptService.Shared.RefreshInBackgroundAsync();
            await RefreshFromStoreAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetSettings] OnLoaded failed: {ex.Message}");
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isSectionLoaded = false;
        if (_localization is not null)
        {
            _localization.LanguageChanged -= OnLanguageChanged;
        }
        DisposeDetailWatcher();
    }

    private void OnVisibilityChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (_isSectionLoaded && Visibility == Visibility.Visible)
        {
            _ = RefreshStudioAsync();
        }
    }

    private void OnLanguageChanged()
    {
        if (!_isSectionLoaded)
        {
            return;
        }

        _ = RefreshStudioAsync();
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (!_isSectionLoaded)
        {
            return;
        }

        _ = RefreshStudioAsync();
    }

    private async Task ProcessPendingDeepLinkModalAsync()
    {
        if (!_isSectionLoaded || XamlRoot is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_pendingAiWizardWidgetId))
        {
            return;
        }

        string target = _pendingAiWizardWidgetId;
        _pendingAiWizardWidgetId = null;

        await Task.Yield();
        if (!_isSectionLoaded || XamlRoot is null)
        {
            return;
        }

        if (string.Equals(target, "__new__", StringComparison.Ordinal))
        {
            await OpenWidgetStudioWindowAsync(null, null);
        }
        else
        {
            await OpenWidgetStudioWindowAsync(target, null);
        }
    }

    private async Task RefreshStudioAsync()
    {
        await _refreshGate.WaitAsync();
        try
        {
            await RefreshStudioCoreAsync();
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task RefreshStudioCoreAsync()
    {
        if (!_isSectionLoaded)
        {
            return;
        }

        IReadOnlyList<GlanceWidgetInstanceInfo> instances =
            _widgetActions is not null && _widgetActions.IsHostAvailable
                ? await _widgetActions.GetCustomWidgetInstancesAsync()
                : [];
        if (!_isSectionLoaded)
        {
            return;
        }

        _instanceStateSignature = CreateInstanceStateSignature(instances);

        bool animateDetailEnter = false;
        if (!string.IsNullOrWhiteSpace(_pendingDetailWidgetId))
        {
            string requestedId = _pendingDetailWidgetId;
            _pendingDetailWidgetId = null;
            if (instances.Any(i => string.Equals(i.Id, requestedId, StringComparison.Ordinal)))
            {
                animateDetailEnter = !string.Equals(_detailWidgetId, requestedId, StringComparison.Ordinal);
                _detailWidgetId = requestedId;
                _highlightedWidgetId = requestedId;
            }
        }

        if (!string.IsNullOrWhiteSpace(_detailWidgetId))
        {
            GlanceWidgetInstanceInfo? detailInstance = instances.FirstOrDefault(i =>
                string.Equals(i.Id, _detailWidgetId, StringComparison.Ordinal));
            if (detailInstance is not null)
            {
                InstanceCardMetadata detailMeta = await ReadInstanceMetadataAsync(detailInstance.Id);
                if (!_isSectionLoaded)
                {
                    return;
                }

                RenderDetailSubviewCore(detailInstance, detailMeta, animateEnter: animateDetailEnter);
                return;
            }

            // Active detail widget was removed externally; fall back cleanly to studio gallery.
            DisposeDetailWatcher();
            _detailSyncHandlers.Clear();
            _detailWidgetId = null;
            ActiveDetailTitle = null;
            DetailViewPanel.Visibility = Visibility.Collapsed;
            DetailViewPanel.Children.Clear();
            StudioViewPanel.Visibility = Visibility.Visible;
            SubPageChanged?.Invoke(this, EventArgs.Empty);
        }

        StudioViewPanel.Visibility = Visibility.Visible;
        DetailViewPanel.Visibility = Visibility.Collapsed;
        UpdateSubsectionHeadersUi(instances.Count);

        if (instances.Count == 0)
        {
            MyWidgetsListPanel.Children.Clear();
            MyWidgetsListPanel.Visibility = Visibility.Collapsed;
            EmptyMyWidgetsCard.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyMyWidgetsCard.Visibility = Visibility.Collapsed;
            MyWidgetsListPanel.Visibility = Visibility.Visible;
            await BuildMyWidgetsListAsync(instances);
        }
    }

    private void UpdateSubsectionHeadersUi(int instanceCount)
    {
        MyWidgetsSectionHeaderText.Text = Localization.Format(
            "CustomWidget.Studio.Tab.MyWidgets",
            instanceCount);
    }

    // ── Single-Column SettingsCard Instance List ────────────────────────────

    private async Task BuildMyWidgetsListAsync(IReadOnlyList<GlanceWidgetInstanceInfo> instances)
    {
        var metadataList = new List<(GlanceWidgetInstanceInfo Info, InstanceCardMetadata Meta)>(instances.Count);
        foreach (GlanceWidgetInstanceInfo info in instances)
        {
            InstanceCardMetadata meta = await ReadInstanceMetadataAsync(info.Id);
            if (!_isSectionLoaded)
            {
                return;
            }

            metadataList.Add((info, meta));
        }

        MyWidgetsListPanel.Children.Clear();

        SettingsCard? highlightedCard = null;
        for (int index = 0; index < metadataList.Count; index++)
        {
            (GlanceWidgetInstanceInfo info, InstanceCardMetadata meta) = metadataList[index];
            bool isHighlighted = !string.IsNullOrWhiteSpace(_highlightedWidgetId) &&
                                 string.Equals(info.Id, _highlightedWidgetId, StringComparison.Ordinal);
            SettingsCard card = CreateMyWidgetSettingsCardRow(info, meta, isHighlighted);
            MyWidgetsListPanel.Children.Add(card);
            if (isHighlighted)
            {
                highlightedCard = card;
            }
        }

        if (highlightedCard is not null)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_isSectionLoaded && !IsDetailViewOpen && highlightedCard.XamlRoot is not null)
                {
                    highlightedCard.StartBringIntoView(new BringIntoViewOptions
                    {
                        AnimationDesired = true,
                        VerticalAlignmentRatio = 0.25
                    });
                }
            });
        }
    }

    private static FrameworkElement CreateIconGlyphContent(
        string? glyph,
        Brush fallbackForeground,
        double emojiFontSize = 18,
        double glyphFontSize = 16)
    {
        if (CustomWidgetTemplates.IsEmojiIcon(glyph))
        {
            var emojiTb = new TextBlock
            {
                Text = glyph!.Trim(),
                FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol, Segoe UI"),
                FontSize = emojiFontSize,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            WidgetTextShadow.SetCast(emojiTb, false);
            return emojiTb;
        }

        string safeGlyph = CustomWidgetSemanticIcons.NormalizeFluentGlyphForOs(
            string.IsNullOrWhiteSpace(glyph) ? "\uE71D" : glyph,
            WindowsCompatibilityService.IsWindows11OrLater);
        string mappedKind = WidgetTitleIconKindNames.FromLegacyGlyph(safeGlyph);
        if (mappedKind != WidgetTitleIconKindNames.Default || string.IsNullOrWhiteSpace(glyph) || safeGlyph == "\uE71D")
        {
            return new Controls.WidgetTitleIcon
            {
                IconKind = mappedKind != WidgetTitleIconKindNames.Default
                    ? mappedKind
                    : WidgetTitleIconKindNames.Custom,
                Mode = WidgetTitleIconModeNames.Color,
                IconSize = 16,
                Glyph = safeGlyph,
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        return new FontIcon
        {
            Glyph = safeGlyph,
            FontSize = glyphFontSize,
            Foreground = fallbackForeground,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private Button CreateInteractiveWidgetIconButton(
        string widgetId,
        string widgetName,
        string? glyph,
        VerticalAlignment verticalAlignment = VerticalAlignment.Top)
    {
        Brush subtleBg = ResolveBrush(
            "SubtleFillColorSecondaryBrush",
            Color.FromArgb(0x14, 0x80, 0x80, 0x80));
        Brush hoverBg = ResolveBrush(
            "ControlFillColorSecondaryBrush",
            Color.FromArgb(0x24, 0x80, 0x80, 0x80));

        string liveGlyph = glyph ?? "🧩";

        var btn = new Button
        {
            Width = 36,
            Height = 36,
            MinWidth = 36,
            MinHeight = 36,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = verticalAlignment,
            Background = subtleBg,
            BorderThickness = new Thickness(0),
            Content = CreateIconGlyphContent(
                liveGlyph,
                ResolveBrush("TextFillColorPrimaryBrush", Colors.White))
        };

        btn.PointerEntered += (_, _) => btn.Background = hoverBg;
        btn.PointerExited += (_, _) => btn.Background = subtleBg;

        string tip = Localization.T("CustomWidget.Native.ChangeIconTooltip");
        ToolTipService.SetToolTip(btn, tip);
        AutomationProperties.SetName(btn, $"{widgetName} - {tip}");
        btn.Flyout = BuildEmojiPickerFlyout(
            currentEmoji: liveGlyph,
            allowAutoMode: false,
            onPickedAsync: async picked =>
            {
                if (!string.IsNullOrWhiteSpace(picked))
                {
                    liveGlyph = picked;
                    btn.Content = CreateIconGlyphContent(
                        liveGlyph,
                        ResolveBrush("TextFillColorPrimaryBrush", Colors.White));
                    await UpdateWidgetManifestEmojiAsync(widgetId, picked);
                }
            },
            resolveLiveEmoji: () => liveGlyph);
        return btn;
    }

    private SettingsCard CreateMyWidgetSettingsCardRow(
        GlanceWidgetInstanceInfo info,
        InstanceCardMetadata meta,
        bool isHighlighted)
    {
        string widgetId = info.Id;
        string widgetName = !string.IsNullOrWhiteSpace(info.Name) &&
                            !info.Name.Contains("$i18n", StringComparison.Ordinal)
            ? info.Name
            : (meta.Manifest?.ResolveLocalizedName(Localization.CurrentCultureName) ?? info.Name);

        var card = new SettingsCard
        {
            IsClickEnabled = true,
            HorizontalContentAlignment = HorizontalAlignment.Right
        };

        if (isHighlighted)
        {
            card.BorderBrush = ResolveBrush(
                "AccentFillColorDefaultBrush",
                Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));
            card.BorderThickness = new Thickness(1.5);
        }

        card.Click += async (_, _) => await OpenWidgetDetailAsync(widgetId);

        var headerGrid = new Grid
        {
            ColumnSpacing = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Button iconButton = CreateInteractiveWidgetIconButton(
            widgetId,
            widgetName,
            meta.IconGlyph,
            VerticalAlignment.Center);
        headerGrid.Children.Add(iconButton);

        var textPanel = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };
        textPanel.Children.Add(new TextBlock
        {
            Text = widgetName,
            Style = (Style)Resources["SettingTitleTextStyle"],
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = meta.Description,
            Style = (Style)Resources["SettingDescriptionTextStyle"],
            MaxLines = 1,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(textPanel, 1);
        headerGrid.Children.Add(textPanel);

        card.Header = headerGrid;
        AutomationProperties.SetName(card, widgetName);

        var rightContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };

        var aiEditPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        aiEditPanel.Children.Add(new FontIcon { Glyph = "\uE945", FontSize = 13 });
        aiEditPanel.Children.Add(new TextBlock
        {
            Text = Localization.T("CustomWidget.Studio.Card.AiEdit"),
            VerticalAlignment = VerticalAlignment.Center
        });
        var aiEditBtn = new Button
        {
            Height = 32,
            MinHeight = 32,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center,
            Content = aiEditPanel
        };
        AutomationProperties.SetName(
            aiEditBtn,
            $"{widgetName} - {Localization.T("CustomWidget.Studio.Card.AiEdit")}");
        aiEditBtn.Click += async (_, _) =>
        {
            _highlightedWidgetId = widgetId;
            await OpenWidgetStudioWindowAsync(widgetId, null);
        };
        rightContent.Children.Add(aiEditBtn);

        var locateBtn = new Button
        {
            Style = (Style)Resources["IconActionButtonStyle"],
            IsEnabled = info.IsEnabled,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon { Glyph = "\uE81D", FontSize = 14 }
        };
        string locateTip = Localization.T("Glance.Instances.Locate");
        ToolTipService.SetToolTip(locateBtn, locateTip);
        AutomationProperties.SetName(locateBtn, $"{widgetName} - {locateTip}");
        locateBtn.Click += async (_, _) =>
        {
            try
            {
                if (_widgetActions.IsHostAvailable)
                {
                    await _widgetActions.LocateCustomWidgetAsync(widgetId);
                }
            }
            catch (Exception ex)
            {
                App.Log($"[CustomWidgetSettings] Locate widget failed: {ex.Message}");
            }
        };
        rightContent.Children.Add(locateBtn);

        var moreBtn = new Button
        {
            Style = (Style)Resources["IconActionButtonStyle"],
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon { Glyph = "\uE712", FontSize = 14 },
            Flyout = BuildCardMoreFlyout(widgetId, widgetName, includeRename: true)
        };
        string moreTip = Localization.T("Glance.Instances.More");
        ToolTipService.SetToolTip(moreBtn, moreTip);
        AutomationProperties.SetName(moreBtn, $"{widgetName} - {moreTip}");
        rightContent.Children.Add(moreBtn);

        var visibleToggle = new ToggleSwitch
        {
            MinWidth = 0,
            OnContent = string.Empty,
            OffContent = string.Empty,
            IsOn = info.IsEnabled,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(visibleToggle, Localization.T("CustomWidget.Studio.Card.ShowOnDesktop"));
        AutomationProperties.SetName(visibleToggle, widgetName);
        bool suppressToggle = false;
        visibleToggle.Toggled += async (_, _) =>
        {
            if (suppressToggle || !_widgetActions.IsHostAvailable)
            {
                return;
            }

            suppressToggle = true;
            try
            {
                await _widgetActions.SetCustomWidgetInstanceEnabledAsync(widgetId, visibleToggle.IsOn);
                locateBtn.IsEnabled = visibleToggle.IsOn;
                await RefreshStudioAsync();
            }
            catch (Exception ex)
            {
                App.Log($"[CustomWidgetSettings] Toggle widget visibility failed: {ex.Message}");
            }
            finally
            {
                suppressToggle = false;
            }
        };
        rightContent.Children.Add(visibleToggle);

        card.Content = rightContent;
        return card;
    }

    // ── Native Drill-in Subview (SettingsCard + Reorderable ListView) ───────

    private async Task OpenWidgetDetailAsync(string widgetId)
    {
        if (!_isSectionLoaded)
        {
            return;
        }

        try
        {
            _highlightedWidgetId = widgetId;
            _detailWidgetId = widgetId;
            StudioStatusInfoBar.IsOpen = false;

            IReadOnlyList<GlanceWidgetInstanceInfo> instances =
                _widgetActions is not null && _widgetActions.IsHostAvailable
                    ? await _widgetActions.GetCustomWidgetInstancesAsync()
                    : [];
            if (!_isSectionLoaded)
            {
                return;
            }

            GlanceWidgetInstanceInfo? info = instances.FirstOrDefault(i =>
                string.Equals(i.Id, widgetId, StringComparison.Ordinal));
            if (info is null)
            {
                _detailWidgetId = null;
                return;
            }

            InstanceCardMetadata meta = await ReadInstanceMetadataAsync(widgetId);
            if (!_isSectionLoaded)
            {
                return;
            }

            RenderDetailSubviewCore(info, meta, animateEnter: true);
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetSettings] OpenWidgetDetailAsync failed: {ex.Message}");
            ShowErrorBanner(ex.Message);
        }
    }

    private void RenderDetailSubviewCore(
        GlanceWidgetInstanceInfo info,
        InstanceCardMetadata meta,
        bool animateEnter)
    {
        string widgetId = info.Id;
        string liveName = !string.IsNullOrWhiteSpace(info.Name) &&
                          !info.Name.Contains("$i18n", StringComparison.Ordinal)
            ? info.Name
            : (meta.Manifest?.ResolveLocalizedName(Localization.CurrentCultureName) ?? info.Name);
        bool titleChanged = !string.Equals(ActiveDetailTitle, liveName, StringComparison.Ordinal);
        ActiveDetailTitle = liveName;

        _detailSyncHandlers.Clear();
        DetailViewPanel.Children.Clear();

        // 1. Instance Identity & Quick Actions Card
        var identityCard = new Border
        {
            Style = (Style)Resources["SettingsGroupStyle"],
            MinHeight = 68,
            Padding = new Thickness(16, 12, 16, 12)
        };

        var identityGrid = new Grid { ColumnSpacing = 16 };
        identityGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        identityGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        identityGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        identityGrid.Children.Add(CreateInteractiveWidgetIconButton(
            widgetId,
            liveName,
            meta.IconGlyph,
            VerticalAlignment.Center));

        var identityTextStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };

        var titleDisplayRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };
        var identityTitleTb = new TextBlock
        {
            Text = liveName,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var inlineRenameBtn = new Button
        {
            Width = 24,
            Height = 24,
            MinWidth = 24,
            MinHeight = 24,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Resources["IconActionButtonStyle"],
            Content = new FontIcon { Glyph = "\uE70F", FontSize = 11.5 }
        };
        string renameTip = Localization.T("Common.Rename");
        ToolTipService.SetToolTip(inlineRenameBtn, renameTip);
        AutomationProperties.SetName(inlineRenameBtn, $"{liveName} - {renameTip}");
        titleDisplayRow.Children.Add(identityTitleTb);
        titleDisplayRow.Children.Add(inlineRenameBtn);

        var inlineNameBox = new TextBox
        {
            MinWidth = 180,
            MaxWidth = 280,
            MaxLength = 60,
            CornerRadius = new CornerRadius(6),
            Text = liveName,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetName(inlineNameBox, Localization.T("CustomWidget.Preferences.NameHeader"));

        identityTextStack.Children.Add(titleDisplayRow);
        identityTextStack.Children.Add(inlineNameBox);
        identityTextStack.Children.Add(new TextBlock
        {
            Text = meta.Description,
            Style = (Style)Resources["SettingDescriptionTextStyle"],
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(identityTextStack, 1);
        identityGrid.Children.Add(identityTextStack);

        var rightActionBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        var aiModifyPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        aiModifyPanel.Children.Add(new FontIcon { Glyph = "\uE945", FontSize = 13 });
        aiModifyPanel.Children.Add(new TextBlock
        {
            Text = Localization.T("CustomWidget.Studio.Card.AiEdit"),
            VerticalAlignment = VerticalAlignment.Center
        });
        var aiModifyBtn = new Button
        {
            Height = 32,
            MinHeight = 32,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(6),
            Content = aiModifyPanel
        };
        aiModifyBtn.Click += async (_, _) =>
        {
            await OpenWidgetStudioWindowAsync(widgetId, null);
        };
        rightActionBar.Children.Add(aiModifyBtn);

        var detailLocateBtn = new Button
        {
            Style = (Style)Resources["IconActionButtonStyle"],
            IsEnabled = info.IsEnabled,
            Content = new FontIcon { Glyph = "\uE81D", FontSize = 14 }
        };
        string locateTip = Localization.T("Glance.Instances.Locate");
        ToolTipService.SetToolTip(detailLocateBtn, locateTip);
        AutomationProperties.SetName(detailLocateBtn, $"{liveName} - {locateTip}");
        detailLocateBtn.Click += async (_, _) =>
        {
            try
            {
                if (_widgetActions.IsHostAvailable)
                {
                    await _widgetActions.LocateCustomWidgetAsync(widgetId);
                }
            }
            catch (Exception ex)
            {
                App.Log($"[CustomWidgetSettings] Locate detail widget failed: {ex.Message}");
            }
        };

        var detailVisibleToggle = new ToggleSwitch
        {
            MinWidth = 0,
            OnContent = string.Empty,
            OffContent = string.Empty,
            IsOn = info.IsEnabled,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(detailVisibleToggle, Localization.T("CustomWidget.Studio.Card.ShowOnDesktop"));
        AutomationProperties.SetName(detailVisibleToggle, liveName);
        bool suppressDetailToggle = false;
        detailVisibleToggle.Toggled += async (_, _) =>
        {
            if (suppressDetailToggle || !_widgetActions.IsHostAvailable)
            {
                return;
            }

            suppressDetailToggle = true;
            try
            {
                await _widgetActions.SetCustomWidgetInstanceEnabledAsync(widgetId, detailVisibleToggle.IsOn);
                detailLocateBtn.IsEnabled = detailVisibleToggle.IsOn;
            }
            catch (Exception ex)
            {
                App.Log($"[CustomWidgetSettings] Toggle detail widget visibility failed: {ex.Message}");
            }
            finally
            {
                suppressDetailToggle = false;
            }
        };
        rightActionBar.Children.Add(detailVisibleToggle);
        rightActionBar.Children.Add(detailLocateBtn);

        var detailMoreBtn = new Button
        {
            Style = (Style)Resources["IconActionButtonStyle"],
            Content = new FontIcon { Glyph = "\uE712", FontSize = 14 },
            Flyout = BuildCardMoreFlyout(widgetId, liveName, includeRename: false)
        };
        string moreTip = Localization.T("Glance.Instances.More");
        ToolTipService.SetToolTip(detailMoreBtn, moreTip);
        AutomationProperties.SetName(detailMoreBtn, $"{liveName} - {moreTip}");
        rightActionBar.Children.Add(detailMoreBtn);

        bool renamingInFlight = false;
        long inlineRenameOpenedAtTick = 0;
        void BeginInlineRename()
        {
            inlineNameBox.Text = liveName;
            titleDisplayRow.Visibility = Visibility.Collapsed;
            inlineNameBox.Visibility = Visibility.Visible;
            inlineRenameOpenedAtTick = Environment.TickCount64;
            DeskBox.Helpers.ImeCompositionGuard.AttachAncestors(inlineNameBox);
            DeskBox.Helpers.InlineEditorFocus.FocusWhenLoaded(
                inlineNameBox,
                static box => box.SelectAll(),
                DispatcherQueue,
                "CustomWidgetSettingsRename");
        }

        async Task CommitOrCloseInlineRenameAsync(bool cancelChanges)
        {
            if (renamingInFlight)
            {
                return;
            }

            if (cancelChanges || _widgetActions is null || !_widgetActions.IsHostAvailable)
            {
                inlineNameBox.Text = liveName;
                inlineNameBox.Visibility = Visibility.Collapsed;
                titleDisplayRow.Visibility = Visibility.Visible;
                return;
            }

            string trimmed = inlineNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) ||
                string.Equals(trimmed, liveName, StringComparison.Ordinal))
            {
                inlineNameBox.Text = liveName;
                inlineNameBox.Visibility = Visibility.Collapsed;
                titleDisplayRow.Visibility = Visibility.Visible;
                return;
            }

            renamingInFlight = true;
            try
            {
                await _widgetActions.RenameWidgetAsync(widgetId, trimmed);
                liveName = trimmed;
                identityTitleTb.Text = trimmed;
                ActiveDetailTitle = trimmed;
                AutomationProperties.SetName(inlineRenameBtn, $"{trimmed} - {renameTip}");
                detailMoreBtn.Flyout = BuildCardMoreFlyout(widgetId, trimmed, includeRename: false);
                SubPageChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                inlineNameBox.Text = liveName;
                ShowErrorBanner(ex.Message);
            }
            finally
            {
                renamingInFlight = false;
                inlineNameBox.Visibility = Visibility.Collapsed;
                titleDisplayRow.Visibility = Visibility.Visible;
            }
        }

        inlineRenameBtn.Click += (_, _) => BeginInlineRename();
        identityTitleTb.Tapped += (_, _) => BeginInlineRename();
        inlineNameBox.LostFocus += async (_, _) =>
        {
            if (DeskBox.Helpers.TransientInputFocusGuard.ShouldDeferInlineEditorBlurCommit(
                    inlineNameBox,
                    inlineRenameOpenedAtTick,
                    default,
                    () => inlineNameBox.Visibility == Visibility.Visible && !renamingInFlight,
                    () => CommitOrCloseInlineRenameAsync(cancelChanges: false)))
            {
                return;
            }

            await CommitOrCloseInlineRenameAsync(cancelChanges: false);
        };
        inlineNameBox.KeyDown += async (_, e) =>
        {
            if (DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e))
            {
                return;
            }

            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                await CommitOrCloseInlineRenameAsync(cancelChanges: false);
            }
            else if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                await CommitOrCloseInlineRenameAsync(cancelChanges: true);
            }
        };

        Grid.SetColumn(rightActionBar, 2);
        identityGrid.Children.Add(rightActionBar);
        identityCard.Child = identityGrid;
        DetailViewPanel.Children.Add(identityCard);

        if (meta.UsesAiCapability)
        {
            string? readyProvider = DeskBoxAiService.Shared.ResolveActiveProviderId(
                CustomWidgetStudioPreferencesStore.Shared.PreferredAiProviderId);
            string aiStatusDesc = !string.IsNullOrWhiteSpace(readyProvider)
                ? Localization.T("CustomWidget.Settings.AiKey.DescriptionReadyShort")
                : Localization.T("CustomWidget.Settings.AiKey.DescriptionMissingShort");

            var configureAiBtn = new Button
            {
                Style = (Style)Resources["ActionButtonStyle"],
                Content = Localization.T("CustomWidget.Settings.AiKey.EditButton")
            };
            configureAiBtn.Click += (_, _) => NavigateToAiSettingsRequested?.Invoke(this, false);

            var aiLinkCard = new SettingsCard
            {
                HorizontalContentAlignment = HorizontalAlignment.Right,
                Header = Localization.T("Settings.AiSettings.EntryCard.Header"),
                Description = aiStatusDesc,
                HeaderIcon = new FontIcon { Glyph = "\uE945" },
                Content = configureAiBtn
            };
            DetailViewPanel.Children.Add(aiLinkCard);
        }

        // 2. Manifest-defined Settings (Module Reorder List + Smart Parameter Controls + Overflow Expander)
        if (meta.Settings.Count > 0)
        {
            var moduleDefs = meta.Settings
                .Where(static d => string.Equals(d.Type, "moduleList", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var paramDefs = meta.Settings
                .Where(static d => !string.Equals(d.Type, "moduleList", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (CustomWidgetSettingDefinition modDef in moduleDefs)
            {
                string initialRaw = meta.SavedValues.TryGetValue(modDef.Key, out string? saved)
                    ? saved
                    : modDef.DefaultValue;
                Border moduleCard = BuildModuleReorderGroupCard(widgetId, modDef, initialRaw);
                moduleCard.Margin = new Thickness(0, 6, 0, 0);
                DetailViewPanel.Children.Add(moduleCard);
            }

            if (paramDefs.Count > 0)
            {
                if (moduleDefs.Count > 0)
                {
                    var subHeader = new StackPanel
                    {
                        Style = (Style)Resources["SettingsSubsectionHeaderStyle"]
                    };
                    subHeader.Children.Add(new TextBlock
                    {
                        Text = Localization.T("CustomWidget.Native.Detail.SectionParameters"),
                        Style = (Style)Resources["SettingsSubsectionTitleTextStyle"]
                    });
                    DetailViewPanel.Children.Add(subHeader);
                }

                foreach (CustomWidgetSettingDefinition paramDef in paramDefs)
                {
                    string initialVal = meta.SavedValues.TryGetValue(paramDef.Key, out string? saved)
                        ? saved
                        : paramDef.DefaultValue;
                    DetailViewPanel.Children.Add(BuildParameterSettingsCard(widgetId, paramDef, initialVal));
                }
            }
        }
        else
        {
            var emptySettingsBtn = new Button
            {
                Style = (Style)Resources["ActionButtonStyle"],
                Content = Localization.T("CustomWidget.Studio.Card.AiEdit")
            };
            emptySettingsBtn.Click += async (_, _) =>
            {
                await OpenWidgetStudioWindowAsync(widgetId, null);
            };

            var emptyCard = new SettingsCard
            {
                Margin = new Thickness(0, 6, 0, 0),
                HorizontalContentAlignment = HorizontalAlignment.Right,
                Header = Localization.T("CustomWidget.Native.OptionsButton"),
                Description = Localization.T("CustomWidget.Native.Flyout.NoExtraSettings"),
                HeaderIcon = new FontIcon { Glyph = "\uE946" },
                Content = emptySettingsBtn
            };
            DetailViewPanel.Children.Add(emptyCard);
        }

        // 3. Per-Widget Permissions & Security Card
        DetailViewPanel.Children.Add(BuildPermissionsSubsectionHeader());
        if (BuildPermissionsExternalWarningBanner(widgetId, meta) is { } externalBanner)
        {
            DetailViewPanel.Children.Add(externalBanner);
        }
        DetailViewPanel.Children.Add(BuildPermissionsGroupCard(widgetId, liveName, meta));

        EnsureDetailWatcher(widgetId);

        bool wasCollapsed = DetailViewPanel.Visibility != Visibility.Visible;
        StudioViewPanel.Visibility = Visibility.Collapsed;
        DetailViewPanel.Visibility = Visibility.Visible;

        if (wasCollapsed || titleChanged)
        {
            SubPageChanged?.Invoke(this, EventArgs.Empty);
        }

        if ((animateEnter || wasCollapsed) && IsLoaded)
        {
            DetailViewPanel.UpdateLayout();
            DetailPageTransitionHelper.PlayNavigationEnter(DetailViewPanel, 40f);
        }
    }

    private CustomWidgetSettingsFormOptions CreateSettingsFormOptions(string widgetId) =>
        new()
        {
            Localization = _localization ?? new LocalizationService(new SettingsService()),
            CultureName = Localization.CurrentCultureName,
            OwnerWindowHandle = _ownerWindow,
            ThemeScope = this,
            CommitSettingAsync = (key, val) => SaveSingleWidgetSettingAsync(widgetId, key, val),
            ReadSecret = key => CustomWidgetSecretStore.Shared.GetSecret(widgetId, key),
            CommitSecretAsync = (key, val) => SaveWidgetSecretSettingAsync(widgetId, key, val),
            RegisterExternalSync = handler => _detailSyncHandlers.Add(handler)
        };

    private Border BuildModuleReorderGroupCard(
        string widgetId,
        CustomWidgetSettingDefinition def,
        string initialRaw)
    {
        bool canDragReorderModules = !DragDropPermissionService.IsCurrentProcessElevatedCached();
        return CustomWidgetSettingsFormBuilder.BuildModuleReorderGroupCard(
            def,
            initialRaw,
            CreateSettingsFormOptions(widgetId),
            canDragReorderModules,
            groupStyle: Resources.TryGetValue("SettingsGroupStyle", out object? gs) ? gs as Style : null,
            titleTextStyle: Resources.TryGetValue("SettingTitleTextStyle", out object? ts) ? ts as Style : null,
            descriptionTextStyle: Resources.TryGetValue("SettingDescriptionTextStyle", out object? ds) ? ds as Style : null);
    }

    private SettingsCard BuildParameterSettingsCard(
        string widgetId,
        CustomWidgetSettingDefinition def,
        string currentVal) =>
        CustomWidgetSettingsFormBuilder.BuildSettingsCard(
            def,
            currentVal,
            CreateSettingsFormOptions(widgetId));

    private void EnsureDetailWatcher(string widgetId)
    {
        if (!_isSectionLoaded)
        {
            DisposeDetailWatcher();
            return;
        }

        DisposeDetailWatcher();
        try
        {
            if (!CustomWidgetDataStore.HasExistingWidgetDirectory(widgetId))
            {
                return;
            }

            CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(widgetId);
            if (!Directory.Exists(store.WidgetDirectory))
            {
                return;
            }

            var watcher = new FileSystemWatcher(store.WidgetDirectory)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            watcher.Changed += (_, e) => ScheduleExternalDetailSync(widgetId, e.Name);
            watcher.Created += (_, e) => ScheduleExternalDetailSync(widgetId, e.Name);
            watcher.Renamed += (_, e) => ScheduleExternalDetailSync(widgetId, e.Name);
            _detailWatcher = watcher;
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetSettings] Detail watcher setup skipped: {ex.Message}");
        }
    }

    private void ScheduleExternalDetailSync(string widgetId, string? fileName)
    {
        if (string.IsNullOrEmpty(fileName) ||
            (!fileName.Equals("store.json", StringComparison.OrdinalIgnoreCase) &&
             !fileName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (Environment.TickCount64 - Interlocked.Read(ref _lastLocalDetailWriteTick) < 450)
        {
            return;
        }

        if (!_isSectionLoaded)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        CancellationToken token = cts.Token;
        if (Interlocked.Exchange(ref _detailWatchDebounceCts, cts) is { } previousCts)
        {
            try { previousCts.Cancel(); } catch { }
            try { previousCts.Dispose(); } catch { }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(180, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            DispatcherQueue.TryEnqueue(async () =>
            {
                if (!_isSectionLoaded ||
                    !string.Equals(_detailWidgetId, widgetId, StringComparison.Ordinal))
                {
                    return;
                }

                try
                {
                    if (fileName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                    {
                        await RefreshStudioAsync();
                        return;
                    }

                    if (!CustomWidgetDataStore.HasExistingWidgetDirectory(widgetId))
                    {
                        return;
                    }

                    CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(widgetId);
                    CustomWidgetStorePayload latest = await store.ReloadFromDiskAsync();
                    foreach (Action<IReadOnlyDictionary<string, string>> sync in _detailSyncHandlers)
                    {
                        sync(latest.Settings);
                    }
                }
                catch (Exception ex)
                {
                    // Ignore transient write lock while desktop widget flushes store.json.
                    App.LogVerbose($"[CustomWidgetSettings] External detail sync skipped: {ex.Message}");
                }
            });
        }, token);
    }

    private void DisposeDetailWatcher()
    {
        if (Interlocked.Exchange(ref _detailWatchDebounceCts, null) is { } debounceCts)
        {
            try { debounceCts.Cancel(); } catch { }
            try { debounceCts.Dispose(); } catch { }
        }

        if (_detailWatcher is { } watcher)
        {
            _detailWatcher = null;
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch
            {
                // Ignore watcher disposal errors.
            }
        }
    }

    private async Task SaveSingleWidgetSettingAsync(string widgetId, string key, string value)
    {
        try
        {
            Interlocked.Exchange(ref _lastLocalDetailWriteTick, Environment.TickCount64);
            if (_widgetActions is { IsHostAvailable: true })
            {
                await _widgetActions.ApplyCustomWidgetMutationAsync(new CustomWidgetSettingsMutation(
                    widgetId,
                    Settings: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [key] = value
                    }));
            }
            else
            {
                CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(widgetId);
                CustomWidgetStorePayload latest = await store.ReloadFromDiskAsync();
                latest.Settings[key] = value;
                if (latest.State.ContainsKey(CustomWidgetPomodoroEngine.KeyRemainingSec) ||
                    latest.State.ContainsKey(CustomWidgetPomodoroEngine.KeyPhase))
                {
                    CustomWidgetPomodoroEngine.SyncIdleDurationWithSettings(latest.State, latest.Settings, key);
                }
                await store.SaveAsync(latest, origin: this);
            }
            Interlocked.Exchange(ref _lastLocalDetailWriteTick, Environment.TickCount64);
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetSettings] Save inline setting failed: {ex.Message}");
            ShowErrorBanner(ex.Message);
        }
    }

    private async Task SaveWidgetSecretSettingAsync(string widgetId, string key, string secretValue)
    {
        try
        {
            Interlocked.Exchange(ref _lastLocalDetailWriteTick, Environment.TickCount64);
            if (_widgetActions is { IsHostAvailable: true })
            {
                await _widgetActions.ApplyCustomWidgetMutationAsync(new CustomWidgetSettingsMutation(
                    widgetId,
                    Secrets: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [key] = secretValue
                    }));
                CustomWidgetSecretStore.Shared.InvalidateCache();
            }
            else
            {
                CustomWidgetSecretStore.Shared.SetSecret(widgetId, key, secretValue);
                CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(widgetId);
                CustomWidgetStorePayload latest = await store.ReloadFromDiskAsync();
                latest.Settings.Remove(key);
                await store.SaveAsync(latest, origin: this);
            }
            Interlocked.Exchange(ref _lastLocalDetailWriteTick, Environment.TickCount64);
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetSettings] Save secret setting failed: {ex.Message}");
            ShowErrorBanner(ex.Message);
        }
    }

    private StackPanel BuildPermissionsSubsectionHeader()
    {
        var subHeader = new StackPanel
        {
            Style = (Style)Resources["SettingsSubsectionHeaderStyle"],
            Margin = new Thickness(4, 12, 4, 2)
        };
        subHeader.Children.Add(new TextBlock
        {
            Text = Localization.T("CustomWidget.Perm.SectionTitle"),
            Style = (Style)Resources["SettingsSubsectionTitleTextStyle"]
        });
        return subHeader;
    }

    private InfoBar? BuildPermissionsExternalWarningBanner(string widgetId, InstanceCardMetadata meta)
    {
        CustomWidgetTrustStore.Shared.InvalidateCache();
        CustomWidgetTrustProfile profile = CustomWidgetTrustStore.Shared.GetProfile(
            widgetId,
            meta.Manifest?.Origin);
        if (profile.TrustedLocalAuthoring)
        {
            return null;
        }

        return new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Warning,
            Margin = new Thickness(0, 2, 0, 2),
            Title = Localization.T("CustomWidget.Perm.ExternalBannerTitle"),
            Message = Localization.T("CustomWidget.Perm.ExternalBannerBody")
        };
    }

    private Border BuildPermissionsGroupCard(
        string widgetId,
        string widgetName,
        InstanceCardMetadata meta)
    {
        CustomWidgetTrustStore.Shared.InvalidateCache();
        CustomWidgetTrustProfile initialProfile = CustomWidgetTrustStore.Shared.GetProfile(
            widgetId,
            meta.Manifest?.Origin);
        IReadOnlySet<CustomWidgetPermissionKind> requested = CustomWidgetTrustStore.DetectRequestedPermissions(
            meta.Manifest,
            meta.LogicJs);
        IReadOnlyList<CustomWidgetPermissionKind> supported = CustomWidgetTrustStore.GetSupportedPermissions();

        var groupBorder = new Border
        {
            Style = (Style)Resources["SettingsGroupStyle"],
            Margin = new Thickness(0, 2, 0, 0)
        };

        var groupStack = new StackPanel();

        // Top Summary Header Row
        var headerGrid = new Grid
        {
            MinHeight = 60,
            Padding = new Thickness(16, 12, 16, 12),
            ColumnSpacing = 16
        };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        headerGrid.Children.Add(new FontIcon
        {
            Glyph = "\uEA18",
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center
        });

        var headerTextStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };
        var headerTitleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        headerTitleRow.Children.Add(new TextBlock
        {
            Text = Localization.T("CustomWidget.Perm.CardTitle"),
            Style = (Style)Resources["SettingTitleTextStyle"],
            VerticalAlignment = VerticalAlignment.Center
        });
        headerTitleRow.Children.Add(BuildProvenancePillBadge(initialProfile.TrustedLocalAuthoring));
        headerTextStack.Children.Add(headerTitleRow);
        headerTextStack.Children.Add(new TextBlock
        {
#if DESKBOX_STORE
            Text = Localization.T("CustomWidget.Perm.CardSubtitleStore"),
#else
            Text = Localization.T("CustomWidget.Perm.CardSubtitleDirect"),
#endif
            Style = (Style)Resources["SettingDescriptionTextStyle"],
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetColumn(headerTextStack, 1);
        headerGrid.Children.Add(headerTextStack);

        var activeCountBadge = new TextBlock
        {
            Style = (Style)Resources["SettingDescriptionTextStyle"],
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(activeCountBadge, 2);
        headerGrid.Children.Add(activeCountBadge);
        groupStack.Children.Add(headerGrid);

        var switches = new Dictionary<CustomWidgetPermissionKind, ToggleSwitch>();
        void RefreshActiveCountBadge()
        {
            int enabledCount = switches.Values.Count(static s => s.IsOn);
            activeCountBadge.Text = Localization.Format(
                "CustomWidget.Perm.ActiveCountFormat",
                enabledCount,
                supported.Count);
        }

        bool usesMutatingPublicNet = CustomWidgetTrustStore.ManifestUsesMutatingPublicNetwork(
            meta.Manifest,
            meta.LogicJs);

        foreach (CustomWidgetPermissionKind kind in supported)
        {
            CustomWidgetPermissionDescriptor desc = CustomWidgetTrustStore.GetDescriptor(
                kind,
                Localization.CurrentCultureName);
            bool isUsedByWidget = requested.Contains(kind);
            bool initialGranted = initialProfile.IsPermissionGranted(
                kind,
                isReadOnlyPublicGet: kind == CustomWidgetPermissionKind.PublicNetwork && !usesMutatingPublicNet);

            groupStack.Children.Add(new Border
            {
                Height = 1,
                Background = ResolveBrush(
                    "DividerStrokeColorDefaultBrush",
                    Color.FromArgb(0x18, 0x80, 0x80, 0x80)),
                Opacity = 0.72
            });

            var rowGrid = new Grid
            {
                Padding = new Thickness(16, 12, 16, 12),
                ColumnSpacing = 16
            };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var rowIcon = new FontIcon
            {
                Glyph = desc.Glyph,
                FontSize = 15,
                Opacity = 0.86,
                VerticalAlignment = VerticalAlignment.Center
            };
            rowGrid.Children.Add(rowIcon);

            var textCol = new StackPanel
            {
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center
            };

            var titleBadgesRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleBadgesRow.Children.Add(new TextBlock
            {
                Text = desc.Title,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            titleBadgesRow.Children.Add(BuildRiskPillBadge(desc.RiskLevel, desc.RiskBadgeText));
            if (isUsedByWidget)
            {
                titleBadgesRow.Children.Add(BuildUsedByWidgetPillBadge());
            }

            textCol.Children.Add(titleBadgesRow);
            textCol.Children.Add(new TextBlock
            {
                Text = desc.RiskDescription,
                Style = (Style)Resources["SettingDescriptionTextStyle"],
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetColumn(textCol, 1);
            rowGrid.Children.Add(textCol);

            var permSwitch = new ToggleSwitch
            {
                MinWidth = 0,
                OnContent = string.Empty,
                OffContent = string.Empty,
                IsOn = initialGranted,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(permSwitch, $"{desc.Title} ({desc.RiskBadgeText})");
            switches[kind] = permSwitch;

            bool suppressPermSwitch = false;
            permSwitch.Toggled += async (_, _) =>
            {
                if (suppressPermSwitch)
                {
                    return;
                }

                bool desired = permSwitch.IsOn;
                if (desired &&
                    !initialProfile.TrustedLocalAuthoring &&
                    desc.RiskLevel >= CustomWidgetPermissionRiskLevel.Elevated &&
                    !_isDialogOpen &&
                    XamlRoot is not null)
                {
                    _isDialogOpen = true;
                    bool confirmed = false;
                    try
                    {
                        ContentDialog warnDialog = CreateStyledDialog(
                            Localization.Format("CustomWidget.Perm.ConfirmHighRiskTitle", desc.Title),
                            new TextBlock
                            {
                                Text = Localization.Format(
                                    "CustomWidget.Perm.ConfirmHighRiskBody",
                                    widgetName,
                                    desc.RiskBadgeText,
                                    desc.RiskDescription),
                                TextWrapping = TextWrapping.Wrap
                            },
                            primaryText: Localization.T("CustomWidget.Perm.ConfirmHighRiskButton"),
                            closeText: Localization.T("Common.Cancel"),
                            defaultButton: ContentDialogButton.Close);
                        confirmed = await warnDialog.ShowAsync() == ContentDialogResult.Primary;
                    }
                    finally
                    {
                        _isDialogOpen = false;
                    }

                    if (!confirmed)
                    {
                        suppressPermSwitch = true;
                        permSwitch.IsOn = false;
                        suppressPermSwitch = false;
                        return;
                    }
                }

                RefreshActiveCountBadge();
                await SaveWidgetPermissionSettingAsync(
                    widgetId,
                    kind,
                    desired,
                    initialProfile.TrustedLocalAuthoring);
            };

            Grid.SetColumn(permSwitch, 2);
            rowGrid.Children.Add(permSwitch);
            groupStack.Children.Add(rowGrid);
        }

        RefreshActiveCountBadge();
        groupBorder.Child = groupStack;
        return groupBorder;
    }

    private Border BuildProvenancePillBadge(bool trustedLocal)
    {
        Color bg = trustedLocal
            ? Color.FromArgb(0x22, 0x10, 0x89, 0x3E)
            : Color.FromArgb(0x28, 0xD8, 0x8A, 0x00);
        Color fg = trustedLocal
            ? Color.FromArgb(0xFF, 0x2E, 0xA0, 0x43)
            : Color.FromArgb(0xFF, 0xE3, 0x9B, 0x00);

        return new Border
        {
            Background = new SolidColorBrush(bg),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, fg.R, fg.G, fg.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = trustedLocal
                    ? Localization.T("CustomWidget.Perm.ProvenanceLocal")
                    : Localization.T("CustomWidget.Perm.ProvenanceExternal"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(fg)
            }
        };
    }

    private static Border BuildRiskPillBadge(CustomWidgetPermissionRiskLevel level, string text)
    {
        (Color bg, Color fg) = level switch
        {
            CustomWidgetPermissionRiskLevel.Low => (
                Color.FromArgb(0x1E, 0x2E, 0xA0, 0x43),
                Color.FromArgb(0xFF, 0x2E, 0xA0, 0x43)),
            CustomWidgetPermissionRiskLevel.Medium => (
                Color.FromArgb(0x1E, 0x00, 0x78, 0xD4),
                Color.FromArgb(0xFF, 0x38, 0x98, 0xFF)),
            CustomWidgetPermissionRiskLevel.Elevated => (
                Color.FromArgb(0x24, 0xD8, 0x8A, 0x00),
                Color.FromArgb(0xFF, 0xE3, 0x9B, 0x00)),
            CustomWidgetPermissionRiskLevel.High => (
                Color.FromArgb(0x26, 0xE5, 0x53, 0x00),
                Color.FromArgb(0xFF, 0xF0, 0x6A, 0x1A)),
            _ => (
                Color.FromArgb(0x2A, 0xD1, 0x34, 0x38),
                Color.FromArgb(0xFF, 0xEF, 0x53, 0x50))
        };

        return new Border
        {
            Background = new SolidColorBrush(bg),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x45, fg.R, fg.G, fg.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(fg)
            }
        };
    }

    private Border BuildUsedByWidgetPillBadge()
    {
        return new Border
        {
            Background = ResolveBrush("SubtleFillColorSecondaryBrush", Color.FromArgb(0x1E, 0x80, 0x80, 0x80)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = Localization.T("CustomWidget.Perm.UsedByWidget"),
                FontSize = 11,
                Opacity = 0.85
            }
        };
    }

    private async Task SaveWidgetPermissionSettingAsync(
        string widgetId,
        CustomWidgetPermissionKind kind,
        bool enabled,
        bool defaultTrustedIfNew)
    {
        try
        {
            string permKey = CustomWidgetTrustStore.ToKey(kind);
            if (_widgetActions is { IsHostAvailable: true })
            {
                await _widgetActions.ApplyCustomWidgetMutationAsync(new CustomWidgetSettingsMutation(
                    widgetId,
                    Permissions: new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                    {
                        [permKey] = enabled
                    }));
                CustomWidgetTrustStore.Shared.InvalidateCache();
            }
            else
            {
                CustomWidgetTrustStore.Shared.SetPermissionOverride(
                    widgetId,
                    kind,
                    enabled,
                    defaultTrustedIfNew);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetSettings] Save permission setting failed: {ex.Message}");
            ShowErrorBanner(ex.Message);
        }
    }

    private MenuFlyout BuildCardMoreFlyout(
        string widgetId,
        string widgetName,
        bool includeRename)
    {
        var flyout = new MenuFlyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight
        };

        if (includeRename)
        {
            var renameItem = new MenuFlyoutItem
            {
                Text = Localization.T("Common.Rename"),
                Icon = new FontIcon { Glyph = "\uE8AC" }
            };
            renameItem.Click += async (_, _) => await ShowRenameDialogAsync(widgetId, widgetName);
            flyout.Items.Add(renameItem);
        }

        var forkItem = new MenuFlyoutItem
        {
            Text = Localization.T("CustomWidget.Action.ForkInStudio"),
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        forkItem.Click += async (_, _) => await OpenWidgetStudioWindowAsync("fork:" + widgetId, null);
        flyout.Items.Add(forkItem);

        var exportItem = new MenuFlyoutItem
        {
            Text = Localization.T("CustomWidget.Menu.ExportPackage"),
            Icon = new FontIcon { Glyph = "\uEDE1" }
        };
        exportItem.Click += async (_, _) => await ExportWidgetPackageAsync(widgetId, widgetName);
        flyout.Items.Add(exportItem);

        var openEditorItem = new MenuFlyoutItem
        {
            Text = Localization.T("CustomWidget.Studio.Menu.OpenInEditor"),
            Icon = new FontIcon { Glyph = "\uE943" }
        };
        openEditorItem.Click += async (_, _) => await OpenWidgetInExternalEditorAsync(widgetId);
        flyout.Items.Add(openEditorItem);

        var copyAgentPromptItem = new MenuFlyoutItem
        {
            Text = Localization.T("CustomWidget.AgentHub.Menu.CopyModifyPrompt"),
            Icon = new FontIcon { Glyph = "\uE756" }
        };
        copyAgentPromptItem.Click += async (_, _) =>
        {
            try
            {
                CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(widgetId);
                await CustomWidgetTemplates.EnsureAgentWorkspacePreparedAsync(store.WidgetDirectory, widgetId);
                string prompt = CustomWidgetAgentConnectorService.BuildKickoffPrompt(
                    AgentPlaybookKind.ModifyExistingWidget,
                    Localization.CurrentCultureName,
                    widgetId,
                    widgetName);
                CopyToClipboard(prompt);
                StudioStatusInfoBar.Severity = InfoBarSeverity.Success;
                StudioStatusInfoBar.Title = string.Empty;
                StudioStatusInfoBar.ActionButton = null;
                StudioStatusInfoBar.Message = Localization.Format("CustomWidget.AgentHub.PlaybookCopiedModify", widgetName);
                StudioStatusInfoBar.IsOpen = true;
            }
            catch (Exception ex)
            {
                App.Log($"[CustomWidgetSettings] Copy agent prompt failed: {ex.Message}");
                ShowErrorBanner(ex.Message);
            }
        };
        flyout.Items.Add(copyAgentPromptItem);

        var openFolderItem = new MenuFlyoutItem
        {
            Text = Localization.T("CustomWidget.Studio.Menu.OpenDataFolder"),
            Icon = new FontIcon { Glyph = "\uE8DA" }
        };
        openFolderItem.Click += async (_, _) => await OpenWidgetFolderAsync(widgetId);
        flyout.Items.Add(openFolderItem);

        var clearDataItem = new MenuFlyoutItem
        {
            Text = Localization.T("CustomWidget.Menu.ClearData"),
            Icon = new FontIcon { Glyph = "\uE75C" }
        };
        clearDataItem.Click += async (_, _) => await ConfirmAndClearWidgetDataAsync(widgetId, widgetName);
        flyout.Items.Add(clearDataItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var deleteItem = new MenuFlyoutItem
        {
            Text = Localization.T("Common.Delete"),
            Icon = new FontIcon { Glyph = "\uE74D" }
        };
        WidgetDangerActionStyle.Apply(deleteItem);
        deleteItem.Click += async (_, _) => await ConfirmAndDeleteWidgetAsync(widgetId, widgetName);
        flyout.Items.Add(deleteItem);

        return flyout;
    }

    // ── Native Windows 11 Acrylic Emoji Picker Flyout ───────────────────────

    private Flyout BuildEmojiPickerFlyout(
        string? currentEmoji,
        bool allowAutoMode,
        Func<string?, Task> onPickedAsync,
        Func<string?>? resolveLiveEmoji = null) =>
        EmojiPickerFlyoutHelper.BuildEmojiPickerFlyout(
            () => ActualTheme,
            k => Localization.T(k),
            currentEmoji,
            allowAutoMode,
            onPickedAsync,
            resolveLiveEmoji);

    private async Task UpdateWidgetManifestEmojiAsync(string widgetId, string newEmoji)
    {
        if (!CustomWidgetTemplates.IsEmojiIcon(newEmoji))
        {
            return;
        }

        try
        {
            if (!CustomWidgetDataStore.HasExistingWidgetDirectory(widgetId))
            {
                return;
            }

            string trimmedEmoji = newEmoji.Trim();
            Interlocked.Exchange(ref _lastLocalDetailWriteTick, Environment.TickCount64);
            CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(widgetId);
            string rawJson = await store.ReadOrCreateManifestJsonAsync(createIfMissing: false);
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                return;
            }

            string updatedJson = CustomWidgetTemplates.WithUpdatedIconJson(rawJson, trimmedEmoji);
            await store.WriteManifestJsonAsync(updatedJson, origin: this);
            await EmojiPickerFlyoutHelper.SyncHostWidgetManifestIconAsync(widgetId, updatedJson);
            if (_widgetActions is { IsHostAvailable: true })
            {
                await _widgetActions.ApplyCustomWidgetMutationAsync(new CustomWidgetSettingsMutation(widgetId));
            }
            Interlocked.Exchange(ref _lastLocalDetailWriteTick, Environment.TickCount64);
            _highlightedWidgetId = widgetId;
            if (!IsDetailViewOpen)
            {
                await RefreshStudioAsync();
            }
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetSettings] Update widget emoji failed: {ex.Message}");
            ShowErrorBanner(ex.Message);
        }
    }

    private async Task<InstanceCardMetadata> ReadInstanceMetadataAsync(string widgetId)
    {
        try
        {
            if (!CustomWidgetDataStore.HasExistingWidgetDirectory(widgetId))
            {
                return new InstanceCardMetadata(
                    "🧩",
                    Localization.T("CustomWidget.Studio.Card.DefaultDescription"),
                    [],
                    new Dictionary<string, string>(StringComparer.Ordinal));
            }

            CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(widgetId);
            IReadOnlyDictionary<string, string> savedValues = new Dictionary<string, string>(StringComparer.Ordinal);
            if (File.Exists(store.StorePath))
            {
                CustomWidgetStorePayload payload = await store.ReloadFromDiskAsync();
                savedValues = new Dictionary<string, string>(payload.Settings, StringComparer.Ordinal);
            }

            if (File.Exists(store.ManifestPath))
            {
                string raw = await File.ReadAllTextAsync(store.ManifestPath);
                CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(raw);
                if (parsed.Manifest is { } manifest)
                {
                    CustomWidgetLocaleCatalog locales = store.ReadLocaleCatalog();
                    if (!locales.HasAnyLocale &&
                        CustomWidgetTemplates.TryNormalizeKnownTemplateId(manifest.Id) is { } knownTplId &&
                        CustomWidgetTemplates.GetTemplateBundle(knownTplId).Locales is { HasAnyLocale: true } tplLocales)
                    {
                        locales = tplLocales;
                    }

                    if (locales.HasAnyLocale)
                    {
                        manifest = manifest.WithLocales(locales);
                    }

                    CustomWidgetPresetInfo? matchedPreset = CustomWidgetTemplates.MatchPresetOrNull(manifest.Id);
                    string localizedManifestDesc = manifest.ResolveLocalizedDescription(Localization.CurrentCultureName);
                    string desc = !string.IsNullOrWhiteSpace(localizedManifestDesc) &&
                                  !localizedManifestDesc.Contains("$i18n", StringComparison.Ordinal)
                        ? localizedManifestDesc
                        : matchedPreset is not null &&
                          manifest.Id.StartsWith("deskbox.", StringComparison.OrdinalIgnoreCase)
                            ? Localization.T(matchedPreset.DescriptionKey)
                            : Localization.T("CustomWidget.Studio.Card.DefaultDescription");

                    string glyph;
                    if (CustomWidgetTemplates.IsEmojiIcon(manifest.Icon))
                    {
                        glyph = manifest.Icon.Trim();
                    }
                    else if (matchedPreset is not null &&
                             manifest.Id.StartsWith("deskbox.", StringComparison.OrdinalIgnoreCase))
                    {
                        glyph = matchedPreset.IconGlyph;
                    }
                    else
                    {
                        glyph = !string.IsNullOrWhiteSpace(manifest.Icon)
                            ? manifest.Icon
                            : "\uE71D";
                    }

                    bool usesAi = manifest.Capabilities.Any(static c =>
                        string.Equals(c, "ai.run", StringComparison.OrdinalIgnoreCase));
                    string? logicJs = File.Exists(store.LogicJsPath)
                        ? await File.ReadAllTextAsync(store.LogicJsPath)
                        : null;

                    return new InstanceCardMetadata(
                        glyph,
                        desc,
                        manifest.ResolveLocalizedSettings(Localization.CurrentCultureName),
                        savedValues,
                        usesAi,
                        manifest,
                        logicJs);
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetSettings] Metadata read failed for {widgetId}: {ex.Message}");
        }

        return new InstanceCardMetadata(
            "🧩",
            Localization.T("CustomWidget.Studio.Card.DefaultDescription"),
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private Brush ResolveBrush(string key, Color fallback) =>
        NeutralInteractionBrush.ResolveThemedResource(key, this) ??
        new SolidColorBrush(fallback);

    private void ShowErrorBanner(string message)
    {
        StudioStatusInfoBar.Severity = InfoBarSeverity.Error;
        StudioStatusInfoBar.Title = string.Empty;
        StudioStatusInfoBar.Message = message;
        StudioStatusInfoBar.IsOpen = true;
    }

    // ── Import, Export & Rename Actions ─────────────────────────────────────

    private async Task ImportPackageFromDiskAsync()
    {
        if (_widgetActions is null || !_widgetActions.IsHostAvailable || _ownerWindow == IntPtr.Zero)
        {
            return;
        }

        try
        {
            string? path = await FileOpenPickerService.PickSingleFileAsync(
                _ownerWindow,
                CustomWidgetPackageService.SupportedImportExtensions.ToArray(),
                PickerLocationId.Desktop);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            StudioStatusInfoBar.IsOpen = false;
            CustomWidgetImportedBundle previewBundle = await CustomWidgetPackageService.ReadPackageBundleAsync(path);
            CustomWidgetManifest previewManifest = previewBundle.Locales is { HasAnyLocale: true }
                ? previewBundle.Manifest.WithLocales(previewBundle.Locales)
                : previewBundle.Manifest;

            Dictionary<string, string>? initialSettings = null;
            Dictionary<string, string>? initialSecrets = null;
            CustomWidgetSetupInspection preflight = CustomWidgetOnboardingHelper.InspectFirstRunSetupRequirements(
                previewManifest,
                Localization.CurrentCultureName);
            if (preflight.RequiresAttention && !_isDialogOpen && XamlRoot is not null)
            {
                _isDialogOpen = true;
                try
                {
                    CustomWidgetFirstRunSetupOutcome outcome = await CustomWidgetFirstRunSetupDialogHelper.ShowAsync(
                        XamlRoot,
                        ActualTheme,
                        _localization,
                        _ownerWindow,
                        previewManifest,
                        preflight,
                        Localization.CurrentCultureName);
                    if (outcome.Applied)
                    {
                        if (outcome.Settings.Count > 0)
                        {
                            initialSettings = new Dictionary<string, string>(outcome.Settings, StringComparer.Ordinal);
                        }
                        if (outcome.Secrets.Count > 0)
                        {
                            initialSecrets = new Dictionary<string, string>(outcome.Secrets, StringComparer.Ordinal);
                        }
                    }
                }
                finally
                {
                    _isDialogOpen = false;
                }
            }

            GlanceWidgetInstanceInfo created = (initialSettings is { Count: > 0 } || initialSecrets is { Count: > 0 })
                ? await _widgetActions.CreateCustomWidgetWithSetupAsync(new CustomWidgetCreateOptions(
                    $"file:{path}",
                    initialSettings,
                    initialSecrets))
                : await _widgetActions.CreateCustomWidgetAsync($"file:{path}");
            if (initialSecrets is { Count: > 0 })
            {
                CustomWidgetSecretStore.Shared.InvalidateCache();
            }

            if (!string.IsNullOrWhiteSpace(created.Id))
            {
                _highlightedWidgetId = created.Id;
                await RefreshStudioAsync();
                ShowPostCommitSuccessBanner(created.Id, created.Name, wasEdit: false);
                if (CustomWidgetStudioPreferencesStore.Shared.LocateOnDesktopAfterCommit)
                {
                    await _widgetActions.LocateCustomWidgetAsync(created.Id);
                }
            }
        }
        catch (CustomWidgetIncompatibleVersionException verEx)
        {
            ShowErrorBanner(verEx.Message);
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetSettings] Import package failed: {ex.Message}");
            ShowErrorBanner(
                Localization.Format("CustomWidget.Studio.Status.ImportFailed", ex.Message));
        }
    }

    private async Task ExportWidgetPackageAsync(string widgetId, string widgetName)
    {
        try
        {
            string? targetFolder = _ownerWindow != IntPtr.Zero
                ? await FolderPickerService.PickFolderAsync(_ownerWindow)
                : null;
            if (string.IsNullOrWhiteSpace(targetFolder))
            {
                return;
            }

            if (Services.DragDrop.StoreVirtualizedPathPolicy.IsWriteBlockedDestination(targetFolder))
            {
                ShowErrorBanner(Localization.T("Widget.DropBlocked.StoreVirtualizedAppData"));
                return;
            }

            string safeBaseName = FileService.SanitizeFileSystemName(widgetName);
            if (string.IsNullOrWhiteSpace(safeBaseName))
            {
                safeBaseName = widgetId;
            }

            string exportPath = FileService.GetAvailablePath(
                Path.Combine(targetFolder, $"{safeBaseName}{CustomWidgetPackageService.SharePackageExtension}"));
            CustomWidgetExportReport report = await CustomWidgetPackageService.ExportSharePackageAsync(widgetId, exportPath);
            if (report.SanitizedPathsCount > 0 || report.StrippedSecretsCount > 0)
            {
                StudioStatusInfoBar.Severity = InfoBarSeverity.Success;
                StudioStatusInfoBar.Title = string.Empty;
                StudioStatusInfoBar.Message = Localization.T("CustomWidget.Package.SanitizedNotice");
                StudioStatusInfoBar.ActionButton = null;
                StudioStatusInfoBar.IsOpen = true;
            }

            Services.ShellLaunchService.FireAndForgetRevealInExplorer(exportPath);
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetSettings] Export package failed: {ex.Message}");
            ShowErrorBanner(ex.Message);
        }
    }

    private static async Task OpenWidgetFolderAsync(string widgetId)
    {
        try
        {
            await CustomWidgetTemplates.EnsureAiAgentWorkspaceDocsAsync();
            CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(widgetId);
            await CustomWidgetTemplates.EnsureAgentWorkspacePreparedAsync(store.WidgetDirectory);
            _ = await Services.ShellLaunchService.OpenPathAsync(store.WidgetDirectory);
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetSettings] Open widget folder failed: {ex.Message}");
        }
    }

    private async Task ShowRenameDialogAsync(string widgetId, string currentName)
    {
        if (_isDialogOpen || XamlRoot is null || _widgetActions is null || !_widgetActions.IsHostAvailable)
        {
            return;
        }

        _isDialogOpen = true;
        try
        {
            var nameBox = new TextBox
            {
                Header = Localization.T("CustomWidget.Preferences.NameHeader"),
                Text = currentName,
                SelectionStart = 0,
                SelectionLength = currentName.Length,
                MinWidth = 340
            };

            ContentDialog dialog = CreateStyledDialog(
                Localization.T("CustomWidget.Native.RenameTitle"),
                nameBox,
                primaryText: Localization.T("Common.Save"),
                closeText: Localization.T("Common.Cancel"),
                defaultButton: ContentDialogButton.Primary);

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                string trimmedName = nameBox.Text.Trim();
                if (!string.IsNullOrWhiteSpace(trimmedName) &&
                    !string.Equals(trimmedName, currentName, StringComparison.Ordinal))
                {
                    _highlightedWidgetId = widgetId;
                    await _widgetActions.RenameWidgetAsync(widgetId, trimmedName);
                    await RefreshStudioAsync();
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidgetSettings] Rename widget failed: {ex.Message}");
            ShowErrorBanner(ex.Message);
        }
        finally
        {
            _isDialogOpen = false;
        }
    }

    private async Task ConfirmAndClearWidgetDataAsync(string widgetId, string widgetName)
    {
        if (_isDialogOpen || XamlRoot is null)
        {
            return;
        }

        _isDialogOpen = true;
        try
        {
            ContentDialog dialog = CreateStyledDialog(
                Localization.Format("CustomWidget.Studio.ClearConfirm.Title", widgetName),
                new TextBlock
                {
                    Text = Localization.T("CustomWidget.Studio.ClearConfirm.Body"),
                    TextWrapping = TextWrapping.Wrap
                },
                primaryText: Localization.T("CustomWidget.Menu.ClearData"),
                closeText: Localization.T("Common.Cancel"),
                defaultButton: ContentDialogButton.Close);

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                Interlocked.Exchange(ref _lastLocalDetailWriteTick, Environment.TickCount64);
                if (_widgetActions is { IsHostAvailable: true })
                {
                    await _widgetActions.ApplyCustomWidgetMutationAsync(new CustomWidgetSettingsMutation(
                        widgetId,
                        ClearAllData: true));
                }
                else
                {
                    CustomWidgetDataStore store = CustomWidgetDataStore.ForWidget(widgetId);
                    await store.ReloadFromDiskAsync();
                    await store.ClearAllDataAsync(origin: this);
                }
                Interlocked.Exchange(ref _lastLocalDetailWriteTick, Environment.TickCount64);
            }
        }
        catch (Exception ex)
        {
            ShowErrorBanner(ex.Message);
        }
        finally
        {
            _isDialogOpen = false;
        }
    }

    private async Task ConfirmAndDeleteWidgetAsync(string widgetId, string widgetName)
    {
        if (_isDialogOpen || XamlRoot is null || _widgetActions is null || !_widgetActions.IsHostAvailable)
        {
            return;
        }

        _isDialogOpen = true;
        try
        {
            ContentDialog dialog = CreateStyledDialog(
                Localization.Format("CustomWidget.DeleteConfirm.Title", widgetName),
                new TextBlock
                {
                    Text = Localization.T("CustomWidget.DeleteConfirm.Note"),
                    TextWrapping = TextWrapping.Wrap
                },
                primaryText: Localization.T("Common.Delete"),
                closeText: Localization.T("Common.Cancel"),
                defaultButton: ContentDialogButton.Close);

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                bool wasCurrentDetail = string.Equals(_detailWidgetId, widgetId, StringComparison.Ordinal);
                DisposeDetailWatcher();
                await _widgetActions.RemoveCustomWidgetAsync(widgetId);
                if (string.Equals(_highlightedWidgetId, widgetId, StringComparison.Ordinal))
                {
                    _highlightedWidgetId = null;
                }

                if (wasCurrentDetail)
                {
                    CloseDetailView(animate: true);
                }
                else
                {
                    await RefreshStudioAsync();
                }
            }
        }
        catch (Exception ex)
        {
            ShowErrorBanner(ex.Message);
        }
        finally
        {
            _isDialogOpen = false;
        }
    }

    private ContentDialog CreateStyledDialog(
        string title,
        object content,
        string? primaryText,
        string closeText,
        ContentDialogButton defaultButton)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = title,
            Content = content,
            CloseButtonText = closeText,
            DefaultButton = defaultButton
        };

        if (!string.IsNullOrEmpty(primaryText))
        {
            dialog.PrimaryButtonText = primaryText;
        }

        if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out object? styleObj) &&
            styleObj is Style dialogStyle)
        {
            dialog.Style = dialogStyle;
        }

        return dialog;
    }

    private static void CopyToClipboard(string text) =>
        McpQuickConnectPanelBuilder.CopyTextToClipboard(text);

    private static string CreateInstanceStateSignature(
        IEnumerable<GlanceWidgetInstanceInfo> instances) =>
        string.Join(
            "|",
            instances.Select(i => $"{i.Id}:{i.Name}:{i.IsEnabled}"));
}
