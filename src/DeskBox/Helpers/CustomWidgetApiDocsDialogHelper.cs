using DeskBox.Controls.WidgetContents;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Helpers;

/// <summary>
/// Standardized 5-Tab Windows 11 Fluent 2 Custom Widget &amp; API Reference Manual powered by
/// <see cref="CustomWidgetCapabilityRegistry"/> (Single Source of Truth).
/// Uses pure <c>Microsoft.UI.Xaml.Controls</c> so it compiles cleanly in both NativeAOT and Settings builds.
/// </summary>
public static class CustomWidgetApiDocsDialogHelper
{
    private enum ReferenceSectionTab
    {
        Capabilities = 0,
        Components = 1,
        Actions = 2,
        Expressions = 3,
        PublicEndpoints = 4
    }

    private sealed record ReferenceEntryDoc(
        string Id,
        string Glyph,
        string GroupZh,
        string GroupEn,
        string TitleZh,
        string TitleEn,
        string SummaryZh,
        string SummaryEn,
        IReadOnlyList<CustomWidgetApiMemberDoc> Members,
        string SnippetJson,
        string? LinkedCapabilityId = null)
    {
        public string GetGroup(bool zh) => zh ? GroupZh : GroupEn;
        public string GetTitle(bool zh) => zh ? TitleZh : TitleEn;
        public string GetSummary(bool zh) => zh ? SummaryZh : SummaryEn;
    }

    private static readonly IReadOnlyList<ReferenceEntryDoc> ComponentDocs = BuildComponentDocs();
    private static readonly IReadOnlyList<ReferenceEntryDoc> ActionDocs = BuildActionDocs();
    private static readonly IReadOnlyList<ReferenceEntryDoc> ExpressionDocs = BuildExpressionDocs();
    private static readonly IReadOnlyList<ReferenceEntryDoc> EndpointDocs = BuildPublicEndpointDocs();

    public sealed record ApiReferenceViewHandle(
        FrameworkElement RootElement,
        Action<string?> SelectCapability,
        Action RefreshPinnedState);

    public static async Task ShowAsync(
        XamlRoot xamlRoot,
        ElementTheme theme,
        LocalizationService loc,
        string? initialCapabilityId = null,
        Func<string, bool>? isCapabilityPinned = null,
        Action<string>? onTogglePinCapability = null)
    {
        if (xamlRoot is null)
        {
            return;
        }

        ApiReferenceViewHandle handle = CreateReferenceView(
            loc,
            initialCapabilityId,
            isCapabilityPinned,
            onTogglePinCapability,
            onInsertIntoPrompt: null,
            onRequestClose: null,
            isSideDrawerMode: false);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = theme,
            Title = loc.T("CustomWidget.ApiDocs.DialogTitle"),
            Content = handle.RootElement,
            CloseButtonText = loc.T("Common.Close"),
            DefaultButton = ContentDialogButton.None
        };
        if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out object? dStyle) &&
            dStyle is Style dialogStyle)
        {
            dialog.Style = dialogStyle;
        }
        dialog.Resources["ContentDialogMaxWidth"] = 1000d;

        await dialog.ShowAsync();
    }

    public static ApiReferenceViewHandle CreateReferenceView(
        LocalizationService loc,
        string? initialCapabilityId = null,
        Func<string, bool>? isCapabilityPinned = null,
        Action<string>? onTogglePinCapability = null,
        Action<string>? onInsertIntoPrompt = null,
        Action? onRequestClose = null,
        bool isSideDrawerMode = false)
    {
        bool isChinese = CustomWidgetRemotePromptService.IsChineseCulture(loc.CurrentCultureName);
        ReferenceSectionTab activeTab = ReferenceSectionTab.Capabilities;
        string searchQuery = string.Empty;
        bool suppressTabSelection = false;
        bool suppressNavSelection = false;

        CustomWidgetCapabilitySpec? initialMatchedCap = CustomWidgetCapabilityRegistry.GetById(initialCapabilityId);
        bool isDetailOpenInDrawer = isSideDrawerMode && initialMatchedCap is not null;

        string selectedCapabilityId = initialMatchedCap?.Id ?? CustomWidgetCapabilityRegistry.All[0].Id;
        string selectedComponentId = ComponentDocs[0].Id;
        string selectedActionId = ActionDocs[0].Id;
        string selectedExpressionId = ExpressionDocs[0].Id;
        string selectedEndpointId = EndpointDocs[0].Id;

        var root = new Grid
        {
            RowSpacing = isSideDrawerMode ? 10 : 12,
            Padding = isSideDrawerMode ? new Thickness(16, 12, 16, 14) : new Thickness(0)
        };
        if (!isSideDrawerMode)
        {
            root.Width = 900;
            root.MaxWidth = 940;
        }

        Brush LookupBrush(string key) =>
            NeutralInteractionBrush.ResolveThemedResource(key, root)
            ?? new SolidColorBrush(Colors.Transparent);

        string copyMdLabel = loc.T(isSideDrawerMode
            ? "CustomWidget.ApiDocs.CopyMarkdownShort"
            : "CustomWidget.ApiDocs.CopyMarkdown");
        var copyMdBtn = new Button
        {
            Height = isSideDrawerMode ? 28 : 32,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 0, 10, 0),
            FontSize = 12,
            Content = CreateIconText("\uE8C8", copyMdLabel)
        };
        ToolTipService.SetToolTip(copyMdBtn, loc.T("CustomWidget.ApiDocs.CopyMarkdown"));
        copyMdBtn.Click += async (_, _) =>
        {
            CopyPlainText(CustomWidgetCapabilityRegistry.GenerateApiReferenceMarkdown(isChinese));
            copyMdBtn.Content = CreateIconText("\uE73E", loc.T("CustomWidget.Native.Dialog.CopiedButton"));
            await Task.Delay(1500);
            copyMdBtn.Content = CreateIconText("\uE8C8", copyMdLabel);
        };

        var selectorBar = new SelectorBar
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };

        (ReferenceSectionTab Tab, string Glyph, string Label)[] sectionTabs =
        [
            (ReferenceSectionTab.Capabilities, "\uE74C", loc.T("CustomWidget.ApiDocs.Tab.Capabilities")),
            (ReferenceSectionTab.Components, "\uE8A9", loc.T("CustomWidget.ApiDocs.Tab.Components")),
            (ReferenceSectionTab.Actions, "\uE945", loc.T("CustomWidget.ApiDocs.Tab.Actions")),
            (ReferenceSectionTab.Expressions, "\uE943", loc.T("CustomWidget.ApiDocs.Tab.Expressions")),
            (ReferenceSectionTab.PublicEndpoints, "\uE774", loc.T("CustomWidget.ApiDocs.Tab.Endpoints"))
        ];

        suppressTabSelection = true;
        foreach ((ReferenceSectionTab tab, string glyph, string label) in sectionTabs)
        {
            var item = new SelectorBarItem
            {
                Tag = tab,
                Text = label,
                Icon = new FontIcon { Glyph = glyph, FontSize = 13 },
                IsSelected = tab == activeTab
            };
            selectorBar.Items.Add(item);
        }
        suppressTabSelection = false;

        int nextRow = 0;
        if (isSideDrawerMode)
        {
            // Row 0: Drawer Title + Export + Close button
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var drawerHeader = new Grid { ColumnSpacing = 8 };
            drawerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            drawerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            drawerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleStack.Children.Add(new FontIcon
            {
                Glyph = "\uE8A1",
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = loc.T("CustomWidget.ApiDocs.Button"),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            drawerHeader.Children.Add(titleStack);

            Grid.SetColumn(copyMdBtn, 1);
            drawerHeader.Children.Add(copyMdBtn);

            if (onRequestClose is not null)
            {
                var closeBtn = new Button
                {
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Content = new FontIcon { Glyph = "\uE711", FontSize = 11 }
                };
                ToolTipService.SetToolTip(closeBtn, loc.T("Common.Close"));
                AutomationProperties.SetName(closeBtn, loc.T("Common.Close"));
                closeBtn.Click += (_, _) => onRequestClose();
                Grid.SetColumn(closeBtn, 2);
                drawerHeader.Children.Add(closeBtn);
            }

            Grid.SetRow(drawerHeader, nextRow++);
            root.Children.Add(drawerHeader);

            // Row 1: Scrollable SelectorBar
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var selectorScroller = new ScrollViewer
            {
                HorizontalScrollMode = ScrollMode.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollMode = ScrollMode.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = selectorBar
            };
            Grid.SetRow(selectorScroller, nextRow++);
            root.Children.Add(selectorScroller);
        }
        else
        {
            // Row 0 (Modal): SelectorBar + Export Markdown button
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var tabHeaderRow = new Grid { ColumnSpacing = 8 };
            tabHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tabHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var selectorScroller = new ScrollViewer
            {
                HorizontalScrollMode = ScrollMode.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollMode = ScrollMode.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = selectorBar
            };
            tabHeaderRow.Children.Add(selectorScroller);

            Grid.SetColumn(copyMdBtn, 1);
            tabHeaderRow.Children.Add(copyMdBtn);
            Grid.SetRow(tabHeaderRow, nextRow++);
            root.Children.Add(tabHeaderRow);
        }

        // Search Row: Native AutoSuggestBox with Find icon
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var searchBox = new AutoSuggestBox
        {
            PlaceholderText = loc.T("CustomWidget.ApiDocs.SearchPlaceholder"),
            QueryIcon = new SymbolIcon(Symbol.Find),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(searchBox, loc.T("CustomWidget.ApiDocs.SearchPlaceholder"));
        Grid.SetRow(searchBox, nextRow++);
        root.Children.Add(searchBox);

        // Content Row: Single-column drill-down in Side Drawer vs 2-column Master-Detail in Wide Modal
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = isSideDrawerMode
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(476)
        });

        var navEmptyHint = new TextBlock
        {
            Text = loc.T("CustomWidget.ApiDocs.EmptySearch"),
            FontSize = 12.5,
            Margin = new Thickness(14, 18, 14, 18),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };

        var navListView = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            SingleSelectionFollowsFocus = false,
            IsItemClickEnabled = false,
            Padding = new Thickness(4)
        };
        ScrollViewer.SetVerticalScrollBarVisibility(navListView, ScrollBarVisibility.Auto);

        var navHostGrid = new Grid();
        navHostGrid.Children.Add(navListView);
        navHostGrid.Children.Add(navEmptyHint);

        var navBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = navHostGrid
        };

        var drawerDirectoryStack = new StackPanel
        {
            Spacing = 6,
            Padding = new Thickness(2, 2, 6, 10)
        };
        var drawerDirectoryScroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = drawerDirectoryStack
        };

        var detailStack = new StackPanel
        {
            Spacing = 14,
            Padding = isSideDrawerMode
                ? new Thickness(14, 12, 14, 16)
                : new Thickness(18, 16, 18, 18)
        };
        var detailScroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = detailStack
        };
        var detailBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = detailScroller
        };

        var drawerBodyHost = new Grid();
        if (isSideDrawerMode)
        {
            drawerBodyHost.Children.Add(drawerDirectoryScroller);
            drawerBodyHost.Children.Add(detailBorder);
            Grid.SetRow(drawerBodyHost, nextRow);
            root.Children.Add(drawerBodyHost);
        }
        else
        {
            var splitGrid = new Grid { ColumnSpacing = 12 };
            splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(248) });
            splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            splitGrid.Children.Add(navBorder);
            Grid.SetColumn(detailBorder, 1);
            splitGrid.Children.Add(detailBorder);
            Grid.SetRow(splitGrid, nextRow);
            root.Children.Add(splitGrid);
        }

        void ApplySurfaceBrushes()
        {
            navBorder.BorderBrush = LookupBrush("CardStrokeColorDefaultBrush");
            navBorder.Background = LookupBrush("CardBackgroundFillColorSecondaryBrush");
            navEmptyHint.Foreground = LookupBrush("TextFillColorTertiaryBrush");
            detailBorder.BorderBrush = LookupBrush("CardStrokeColorDefaultBrush");
            detailBorder.Background = LookupBrush("CardBackgroundFillColorDefaultBrush");
        }

        Border CreateScopedCodeBadge(string code) => new()
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = LookupBrush("SubtleFillColorSecondaryBrush"),
            Child = new TextBlock
            {
                Text = code,
                FontSize = 11.5,
                FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
                Foreground = LookupBrush("TextFillColorSecondaryBrush")
            }
        };

        Button CreateCopyableTokenChip(string token)
        {
            var labelText = new TextBlock
            {
                Text = token,
                FontSize = 11.5,
                FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
                Foreground = LookupBrush("TextFillColorPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            var chipBtn = new Button
            {
                MinHeight = 26,
                MinWidth = 0,
                Padding = new Thickness(8, 3, 8, 3),
                CornerRadius = new CornerRadius(4),
                Background = LookupBrush("SubtleFillColorSecondaryBrush"),
                BorderThickness = new Thickness(1),
                BorderBrush = LookupBrush("DividerStrokeColorDefaultBrush"),
                Content = labelText
            };
            ToolTipService.SetToolTip(chipBtn, loc.T("Common.Copy") + ": " + token);
            AutomationProperties.SetName(chipBtn, loc.T("Common.Copy") + " " + token);
            chipBtn.Click += async (_, _) =>
            {
                CopyPlainText(token);
                labelText.Text = "✓ " + token;
                labelText.Foreground = LookupBrush("AccentTextFillColorPrimaryBrush");
                await Task.Delay(1100);
                labelText.Text = token;
                labelText.Foreground = LookupBrush("TextFillColorPrimaryBrush");
            };
            return chipBtn;
        }

        void AppendDrawerBackHeader(string groupLabel)
        {
            if (!isSideDrawerMode)
            {
                return;
            }

            var backBar = new Grid
            {
                ColumnSpacing = 8,
                Padding = new Thickness(0, 0, 0, 4),
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = LookupBrush("DividerStrokeColorDefaultBrush")
            };
            backBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            backBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            backBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var backBtn = new Button
            {
                Height = 28,
                Padding = new Thickness(8, 0, 10, 0),
                CornerRadius = new CornerRadius(6),
                Background = LookupBrush("SubtleFillColorSecondaryBrush"),
                BorderThickness = new Thickness(0),
                FontSize = 12,
                Content = CreateIconText("\uE72B", loc.T("CustomWidget.ApiDocs.BackToList"))
            };
            AutomationProperties.SetName(backBtn, loc.T("CustomWidget.ApiDocs.BackToList"));
            backBtn.Click += (_, _) =>
            {
                isDetailOpenInDrawer = false;
                RenderNavAndDetail();
                FluentMotionHelper.PlayEntranceSlideAndFade(drawerDirectoryScroller, offsetX: -10f, offsetY: 0f, durationMs: 150);
            };
            backBar.Children.Add(backBtn);

            if (!string.IsNullOrWhiteSpace(groupLabel))
            {
                var catLabel = new TextBlock
                {
                    Text = groupLabel,
                    FontSize = 11.5,
                    Foreground = LookupBrush("TextFillColorTertiaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(catLabel, 2);
                backBar.Children.Add(catLabel);
            }

            detailStack.Children.Add(backBar);
        }

        void AppendMembersTable(IReadOnlyList<CustomWidgetApiMemberDoc> members)
        {
            if (members.Count == 0)
            {
                return;
            }

            detailStack.Children.Add(new TextBlock
            {
                Text = loc.T("CustomWidget.ApiDocs.MembersHeader"),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = LookupBrush("TextFillColorSecondaryBrush"),
                Margin = new Thickness(0, 2, 0, -4)
            });

            var membersList = new StackPanel { Spacing = 8 };
            foreach (CustomWidgetApiMemberDoc member in members)
            {
                var card = new Border
                {
                    Padding = new Thickness(12, 10, 12, 10),
                    CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(1),
                    BorderBrush = LookupBrush("DividerStrokeColorDefaultBrush"),
                    Background = LookupBrush("SubtleFillColorTertiaryBrush")
                };

                var cardRows = new StackPanel { Spacing = 6 };
                string[] splitTokens = member.Name.Contains(" / ", StringComparison.Ordinal)
                    ? member.Name.Split(" / ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : [];

                if (splitTokens.Length > 1)
                {
                    var topRow = new Grid { ColumnSpacing = 8 };
                    topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var chipsWrap = new CustomWidgetWrapPanel
                    {
                        HorizontalSpacing = 6,
                        VerticalSpacing = 6
                    };
                    foreach (string token in splitTokens)
                    {
                        chipsWrap.Children.Add(CreateCopyableTokenChip(token));
                    }
                    topRow.Children.Add(chipsWrap);

                    var kindBadge = new Border
                    {
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(6, 2, 6, 2),
                        VerticalAlignment = VerticalAlignment.Top,
                        Background = LookupBrush("SubtleFillColorSecondaryBrush"),
                        Child = new TextBlock
                        {
                            Text = member.Kind,
                            FontSize = 10.5,
                            Foreground = LookupBrush("TextFillColorTertiaryBrush")
                        }
                    };
                    Grid.SetColumn(kindBadge, 1);
                    topRow.Children.Add(kindBadge);
                    cardRows.Children.Add(topRow);
                }
                else
                {
                    var topRow = new Grid { ColumnSpacing = 8 };
                    topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var sigBorder = new Border
                    {
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(8, 4, 8, 4),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Background = LookupBrush("SubtleFillColorSecondaryBrush"),
                        Child = new TextBlock
                        {
                            Text = member.Name,
                            FontSize = 11.5,
                            FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
                            Foreground = LookupBrush("TextFillColorPrimaryBrush"),
                            TextWrapping = TextWrapping.Wrap,
                            IsTextSelectionEnabled = true
                        }
                    };
                    topRow.Children.Add(sigBorder);

                    var kindBadge = new Border
                    {
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(6, 2, 6, 2),
                        VerticalAlignment = VerticalAlignment.Top,
                        Background = LookupBrush("SubtleFillColorSecondaryBrush"),
                        Child = new TextBlock
                        {
                            Text = member.Kind,
                            FontSize = 10.5,
                            Foreground = LookupBrush("TextFillColorTertiaryBrush")
                        }
                    };
                    Grid.SetColumn(kindBadge, 1);
                    topRow.Children.Add(kindBadge);

                    string capturedName = member.Name;
                    var copyIcon = new FontIcon { Glyph = "\uE8C8", FontSize = 11.5 };
                    var copyBtn = new Button
                    {
                        Width = 26,
                        Height = 26,
                        Padding = new Thickness(0),
                        MinWidth = 0,
                        MinHeight = 0,
                        CornerRadius = new CornerRadius(4),
                        VerticalAlignment = VerticalAlignment.Top,
                        Background = new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        Content = copyIcon
                    };
                    ToolTipService.SetToolTip(copyBtn, loc.T("Common.Copy") + ": " + member.Name);
                    AutomationProperties.SetName(copyBtn, loc.T("Common.Copy") + " " + member.Name);
                    copyBtn.Click += async (_, _) =>
                    {
                        CopyPlainText(capturedName);
                        copyIcon.Glyph = "\uE73E";
                        await Task.Delay(1200);
                        copyIcon.Glyph = "\uE8C8";
                    };
                    Grid.SetColumn(copyBtn, 2);
                    topRow.Children.Add(copyBtn);

                    cardRows.Children.Add(topRow);
                }

                cardRows.Children.Add(new TextBlock
                {
                    Text = isChinese ? member.DescriptionZh : member.DescriptionEn,
                    FontSize = 12.5,
                    LineHeight = 19,
                    Foreground = LookupBrush("TextFillColorSecondaryBrush"),
                    TextWrapping = TextWrapping.Wrap
                });

                card.Child = cardRows;
                membersList.Children.Add(card);
            }

            detailStack.Children.Add(membersList);
        }

        void AppendSnippetBlock(string snippetRaw)
        {
            if (string.IsNullOrWhiteSpace(snippetRaw))
            {
                return;
            }

            string snippetText = snippetRaw.Trim();
            var snippetHeader = new Grid
            {
                ColumnSpacing = 8,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            snippetHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            snippetHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            snippetHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            snippetHeader.Children.Add(new FontIcon
            {
                Glyph = "\uE943",
                FontSize = 12,
                Foreground = LookupBrush("TextFillColorSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });

            var headerLabel = new TextBlock
            {
                Text = loc.T("CustomWidget.ApiDocs.GoldenSnippetHeader"),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = LookupBrush("TextFillColorPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(headerLabel, 1);
            snippetHeader.Children.Add(headerLabel);

            string copySnippetLabel = loc.T("CustomWidget.ApiDocs.CopySnippet");
            var copySnippetBtn = new Button
            {
                Height = 26,
                Padding = new Thickness(9, 0, 9, 0),
                CornerRadius = new CornerRadius(6),
                FontSize = 11.5,
                Content = CreateIconText("\uE8C8", copySnippetLabel)
            };
            copySnippetBtn.Click += async (_, _) =>
            {
                CopyPlainText(snippetText);
                copySnippetBtn.Content = CreateIconText("\uE73E", loc.T("CustomWidget.Native.Dialog.CopiedButton"));
                await Task.Delay(1400);
                copySnippetBtn.Content = CreateIconText("\uE8C8", copySnippetLabel);
            };
            Grid.SetColumn(copySnippetBtn, 2);
            snippetHeader.Children.Add(copySnippetBtn);

            var snippetExpander = new Expander
            {
                IsExpanded = !isSideDrawerMode,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                CornerRadius = new CornerRadius(8),
                Header = snippetHeader,
                Content = new TextBlock
                {
                    Text = snippetText,
                    FontSize = 11.5,
                    LineHeight = 18,
                    FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap
                }
            };

            detailStack.Children.Add(snippetExpander);
        }

        UIElement? BuildCapabilityActionButtons(string capabilityId)
        {
            if (onTogglePinCapability is null && onInsertIntoPrompt is null)
            {
                return null;
            }

            var actionsWrap = new CustomWidgetWrapPanel
            {
                HorizontalSpacing = 6,
                VerticalSpacing = 6
            };

            if (onInsertIntoPrompt is not null)
            {
                string insertLabel = loc.Format("CustomWidget.ApiDocs.InsertIdButton", capabilityId);
                var insertBtn = new Button
                {
                    Height = 28,
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 0, 10, 0),
                    FontSize = 12,
                    Content = CreateIconText("\uE8A7", insertLabel)
                };
                ToolTipService.SetToolTip(insertBtn, loc.T("CustomWidget.ApiDocs.InsertIdTooltip"));
                insertBtn.Click += (_, _) => onInsertIntoPrompt("@" + capabilityId);
                actionsWrap.Children.Add(insertBtn);
            }

            if (onTogglePinCapability is not null)
            {
                bool pinned = isCapabilityPinned?.Invoke(capabilityId) ?? false;
                var pinBtn = new Button
                {
                    Height = 28,
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 0, 10, 0),
                    FontSize = 12,
                    Style = !pinned && !isSideDrawerMode ? LookupStyle("AccentButtonStyle") : null,
                    Background = pinned ? LookupBrush("SubtleFillColorSecondaryBrush") : null,
                    BorderBrush = pinned ? LookupBrush("AccentFillColorDefaultBrush") : LookupBrush("CardStrokeColorDefaultBrush"),
                    Content = CreateIconText(
                        pinned ? "\uE73E" : "\uE710",
                        loc.T(pinned ? "CustomWidget.ApiDocs.PinnedButton" : "CustomWidget.ApiDocs.PinToPromptButton"))
                };
                pinBtn.Click += (_, _) =>
                {
                    onTogglePinCapability(capabilityId);
                    RenderNavAndDetail();
                };
                actionsWrap.Children.Add(pinBtn);
            }

            return actionsWrap;
        }

        string ResolveShortCategoryTag(CustomWidgetCapabilityCategory cat) => cat switch
        {
            CustomWidgetCapabilityCategory.DataSource => loc.T("CustomWidget.ApiDocs.Category.Sources"),
            CustomWidgetCapabilityCategory.Component => loc.T("CustomWidget.ApiDocs.Category.Components"),
            CustomWidgetCapabilityCategory.Action => loc.T("CustomWidget.ApiDocs.Category.Actions"),
            CustomWidgetCapabilityCategory.ConfigAndSecurity => loc.T("CustomWidget.ApiDocs.Category.Security"),
            _ => string.Empty
        };

        void RenderCapabilityDetail()
        {
            detailStack.Children.Clear();
            CustomWidgetCapabilitySpec? spec = CustomWidgetCapabilityRegistry.GetById(selectedCapabilityId)
                ?? CustomWidgetCapabilityRegistry.All.FirstOrDefault();
            if (spec is null)
            {
                return;
            }

            AppendDrawerBackHeader(ResolveShortCategoryTag(spec.Category));

            var titleCol = new StackPanel { Spacing = 8 };
            var titleWrap = new CustomWidgetWrapPanel { HorizontalSpacing = 8, VerticalSpacing = 4 };
            titleWrap.Children.Add(new FontIcon
            {
                Glyph = spec.Glyph,
                FontSize = 16,
                Foreground = LookupBrush("AccentTextFillColorPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
            titleWrap.Children.Add(new TextBlock
            {
                Text = FormatConciseTitle(spec.GetTitle(isChinese)),
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });
            titleWrap.Children.Add(CreateScopedCodeBadge("@" + spec.Id));
            titleCol.Children.Add(titleWrap);

            titleCol.Children.Add(new TextBlock
            {
                Text = spec.GetSummary(isChinese),
                FontSize = 13,
                LineHeight = 20,
                Foreground = LookupBrush("TextFillColorSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap
            });

            if (BuildCapabilityActionButtons(spec.Id) is { } actionBar)
            {
                titleCol.Children.Add(actionBar);
            }

            detailStack.Children.Add(titleCol);

            if (spec.RequiredCapabilityIds.Count > 0)
            {
                var depRow = new CustomWidgetWrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
                depRow.Children.Add(new TextBlock
                {
                    Text = loc.T("CustomWidget.ApiDocs.AutoDepsLabel"),
                    FontSize = 11.5,
                    Foreground = LookupBrush("TextFillColorTertiaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                });
                foreach (string depId in spec.RequiredCapabilityIds)
                {
                    string capturedDep = depId;
                    var depPill = new Button
                    {
                        Height = 24,
                        Padding = new Thickness(8, 0, 8, 0),
                        CornerRadius = new CornerRadius(6),
                        FontSize = 11,
                        Content = "@" + depId
                    };
                    depPill.Click += (_, _) =>
                    {
                        selectedCapabilityId = capturedDep;
                        RenderNavAndDetail();
                    };
                    depRow.Children.Add(depPill);
                }
                detailStack.Children.Add(depRow);
            }

            AppendMembersTable(spec.Members);
            AppendSnippetBlock(spec.GoldenSnippetJson);
            detailScroller.ChangeView(0, 0, null, disableAnimation: true);
        }

        void RenderReferenceEntryDetail(ReferenceEntryDoc entry)
        {
            detailStack.Children.Clear();
            AppendDrawerBackHeader(entry.GetGroup(isChinese));

            var titleCol = new StackPanel { Spacing = 8 };
            var titleWrap = new CustomWidgetWrapPanel { HorizontalSpacing = 8, VerticalSpacing = 4 };
            titleWrap.Children.Add(new FontIcon
            {
                Glyph = entry.Glyph,
                FontSize = 16,
                Foreground = LookupBrush("AccentTextFillColorPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
            titleWrap.Children.Add(new TextBlock
            {
                Text = entry.GetTitle(isChinese),
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });
            titleWrap.Children.Add(CreateScopedCodeBadge(entry.Id));
            titleCol.Children.Add(titleWrap);

            titleCol.Children.Add(new TextBlock
            {
                Text = entry.GetSummary(isChinese),
                FontSize = 13,
                LineHeight = 20,
                Foreground = LookupBrush("TextFillColorSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap
            });

            if (!string.IsNullOrWhiteSpace(entry.LinkedCapabilityId) &&
                BuildCapabilityActionButtons(entry.LinkedCapabilityId!) is { } actionBar)
            {
                titleCol.Children.Add(actionBar);
            }

            detailStack.Children.Add(titleCol);
            AppendMembersTable(entry.Members);
            AppendSnippetBlock(entry.SnippetJson);
            detailScroller.ChangeView(0, 0, null, disableAnimation: true);
        }

        ListViewItem CreateNavListItem(
            string id,
            string? glyph,
            string title,
            string subtitle,
            bool showPinnedCheck)
        {
            var itemGrid = new Grid { ColumnSpacing = 10, Padding = new Thickness(4, 5, 4, 5) };
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (!string.IsNullOrWhiteSpace(glyph))
            {
                itemGrid.Children.Add(new FontIcon
                {
                    Glyph = glyph,
                    FontSize = 13.5,
                    Foreground = LookupBrush("TextFillColorSecondaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            var textCol = new StackPanel { Spacing = 1 };
            textCol.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            textCol.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 11,
                FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
                Foreground = LookupBrush("TextFillColorTertiaryBrush"),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(textCol, 1);
            itemGrid.Children.Add(textCol);

            if (showPinnedCheck)
            {
                var pinBadge = new FontIcon
                {
                    Glyph = "\uE73E",
                    FontSize = 11,
                    Foreground = LookupBrush("AccentTextFillColorPrimaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(pinBadge, 2);
                itemGrid.Children.Add(pinBadge);
            }

            var lvItem = new ListViewItem
            {
                Tag = id,
                MinHeight = 46,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = itemGrid
            };
            ToolTipService.SetToolTip(lvItem, $"{title} ({subtitle})");
            AutomationProperties.SetName(lvItem, title);
            return lvItem;
        }

        void SetSelectedEntryId(string id)
        {
            switch (activeTab)
            {
                case ReferenceSectionTab.Capabilities:
                    selectedCapabilityId = id;
                    break;
                case ReferenceSectionTab.Components:
                    selectedComponentId = id;
                    break;
                case ReferenceSectionTab.Actions:
                    selectedActionId = id;
                    break;
                case ReferenceSectionTab.Expressions:
                    selectedExpressionId = id;
                    break;
                case ReferenceSectionTab.PublicEndpoints:
                    selectedEndpointId = id;
                    break;
            }
        }

        FrameworkElement CreateDrawerDirectoryCard(
            string entryId,
            string glyph,
            string title,
            string codeBadge,
            string summary,
            string? pinCapabilityId)
        {
            bool hasQuickPin = onTogglePinCapability is not null && !string.IsNullOrWhiteSpace(pinCapabilityId);
            bool isPinned = hasQuickPin && (isCapabilityPinned?.Invoke(pinCapabilityId!) ?? false);

            var host = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var rowGrid = new Grid
            {
                ColumnSpacing = 12
            };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            rowGrid.Children.Add(new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(6),
                Background = LookupBrush("SubtleFillColorSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new FontIcon
                {
                    Glyph = glyph,
                    FontSize = 14,
                    Foreground = isPinned
                        ? LookupBrush("AccentTextFillColorPrimaryBrush")
                        : LookupBrush("TextFillColorSecondaryBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });

            var textCol = new StackPanel
            {
                Spacing = 3,
                VerticalAlignment = VerticalAlignment.Center
            };
            var titleLine = new CustomWidgetWrapPanel
            {
                HorizontalSpacing = 6,
                VerticalSpacing = 2
            };
            titleLine.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = LookupBrush("TextFillColorPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
            titleLine.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1, 5, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Background = LookupBrush("SubtleFillColorSecondaryBrush"),
                Child = new TextBlock
                {
                    Text = codeBadge,
                    FontSize = 10.5,
                    FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
                    Foreground = LookupBrush("TextFillColorTertiaryBrush")
                }
            });
            textCol.Children.Add(titleLine);

            textCol.Children.Add(new TextBlock
            {
                Text = summary,
                FontSize = 12,
                Foreground = LookupBrush("TextFillColorSecondaryBrush"),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(textCol, 1);
            rowGrid.Children.Add(textCol);

            var chevron = new FontIcon
            {
                Glyph = "\uE76C",
                FontSize = 11,
                Foreground = LookupBrush("TextFillColorTertiaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(hasQuickPin ? 34 : 0, 0, 0, 0)
            };
            Grid.SetColumn(chevron, 2);
            rowGrid.Children.Add(chevron);

            var mainCardBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(12, 10, 12, 10),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = isPinned
                    ? LookupBrush("AccentFillColorDefaultBrush")
                    : LookupBrush("CardStrokeColorDefaultBrush"),
                Background = LookupBrush("CardBackgroundFillColorDefaultBrush"),
                Content = rowGrid
            };
            AutomationProperties.SetName(mainCardBtn, $"{title} ({codeBadge})");
            mainCardBtn.Click += (_, _) =>
            {
                SetSelectedEntryId(entryId);
                isDetailOpenInDrawer = true;
                RenderNavAndDetail();
                FluentMotionHelper.PlayEntranceSlideAndFade(detailBorder, offsetX: 12f, offsetY: 0f, durationMs: 160);
            };
            host.Children.Add(mainCardBtn);

            if (hasQuickPin)
            {
                string capturedCapId = pinCapabilityId!;
                string pinTip = loc.T(isPinned
                    ? "CustomWidget.ApiDocs.PinnedButton"
                    : "CustomWidget.ApiDocs.PinToPromptButton");
                var quickPinBtn = new Button
                {
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    MinWidth = 0,
                    MinHeight = 0,
                    CornerRadius = new CornerRadius(6),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 28, 0),
                    Background = isPinned
                        ? LookupBrush("SubtleFillColorSecondaryBrush")
                        : new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(isPinned ? 1 : 0),
                    BorderBrush = isPinned
                        ? LookupBrush("AccentFillColorDefaultBrush")
                        : new SolidColorBrush(Colors.Transparent),
                    Content = new FontIcon
                    {
                        Glyph = isPinned ? "\uE73E" : "\uE710",
                        FontSize = 11.5,
                        Foreground = isPinned
                            ? LookupBrush("AccentTextFillColorPrimaryBrush")
                            : LookupBrush("TextFillColorSecondaryBrush")
                    }
                };
                Canvas.SetZIndex(quickPinBtn, 2);
                ToolTipService.SetToolTip(quickPinBtn, pinTip);
                AutomationProperties.SetName(quickPinBtn, $"{title} — {pinTip}");
                quickPinBtn.Click += (_, _) =>
                {
                    onTogglePinCapability!(capturedCapId);
                    RenderNavAndDetail();
                };
                host.Children.Add(quickPinBtn);
            }

            return host;
        }

        void RenderDrawerDirectory()
        {
            drawerDirectoryStack.Children.Clear();

            if (activeTab == ReferenceSectionTab.Capabilities)
            {
                IReadOnlyList<CustomWidgetCapabilitySpec> matches =
                    CustomWidgetCapabilityRegistry.Search(searchQuery);
                if (matches.Count == 0)
                {
                    drawerDirectoryStack.Children.Add(new TextBlock
                    {
                        Text = loc.T("CustomWidget.ApiDocs.EmptySearch"),
                        FontSize = 12.5,
                        Foreground = LookupBrush("TextFillColorTertiaryBrush"),
                        Margin = new Thickness(8, 16, 8, 16),
                        TextWrapping = TextWrapping.Wrap
                    });
                    return;
                }

                CustomWidgetCapabilityCategory[] catOrder =
                [
                    CustomWidgetCapabilityCategory.DataSource,
                    CustomWidgetCapabilityCategory.Component,
                    CustomWidgetCapabilityCategory.Action,
                    CustomWidgetCapabilityCategory.ConfigAndSecurity
                ];

                bool isFirstGroup = true;
                foreach (CustomWidgetCapabilityCategory cat in catOrder)
                {
                    List<CustomWidgetCapabilitySpec> group = matches.Where(c => c.Category == cat).ToList();
                    if (group.Count == 0)
                    {
                        continue;
                    }

                    drawerDirectoryStack.Children.Add(new TextBlock
                    {
                        Text = ResolveShortCategoryTag(cat),
                        FontSize = 11.5,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = LookupBrush("TextFillColorTertiaryBrush"),
                        Margin = new Thickness(4, isFirstGroup ? 2 : 10, 4, 2)
                    });
                    isFirstGroup = false;

                    foreach (CustomWidgetCapabilitySpec spec in group)
                    {
                        drawerDirectoryStack.Children.Add(CreateDrawerDirectoryCard(
                            entryId: spec.Id,
                            glyph: spec.Glyph,
                            title: FormatConciseTitle(spec.GetTitle(isChinese)),
                            codeBadge: "@" + spec.Id,
                            summary: spec.GetSummary(isChinese),
                            pinCapabilityId: spec.Id));
                    }
                }
                return;
            }

            IReadOnlyList<ReferenceEntryDoc> sourceEntries = activeTab switch
            {
                ReferenceSectionTab.Components => ComponentDocs,
                ReferenceSectionTab.Actions => ActionDocs,
                ReferenceSectionTab.Expressions => ExpressionDocs,
                _ => EndpointDocs
            };

            List<ReferenceEntryDoc> filtered = FilterReferenceEntries(sourceEntries, searchQuery);
            if (filtered.Count == 0)
            {
                drawerDirectoryStack.Children.Add(new TextBlock
                {
                    Text = loc.T("CustomWidget.ApiDocs.EmptySearch"),
                    FontSize = 12.5,
                    Foreground = LookupBrush("TextFillColorTertiaryBrush"),
                    Margin = new Thickness(8, 16, 8, 16),
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            bool firstRefGroup = true;
            foreach (IGrouping<string, ReferenceEntryDoc> grp in filtered.GroupBy(e => e.GetGroup(isChinese)))
            {
                if (!string.IsNullOrWhiteSpace(grp.Key))
                {
                    drawerDirectoryStack.Children.Add(new TextBlock
                    {
                        Text = grp.Key,
                        FontSize = 11.5,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = LookupBrush("TextFillColorTertiaryBrush"),
                        Margin = new Thickness(4, firstRefGroup ? 2 : 10, 4, 2)
                    });
                    firstRefGroup = false;
                }

                foreach (ReferenceEntryDoc entry in grp)
                {
                    drawerDirectoryStack.Children.Add(CreateDrawerDirectoryCard(
                        entryId: entry.Id,
                        glyph: entry.Glyph,
                        title: FormatConciseTitle(entry.GetTitle(isChinese)),
                        codeBadge: entry.Id,
                        summary: entry.GetSummary(isChinese),
                        pinCapabilityId: entry.LinkedCapabilityId));
                }
            }
        }

        void RenderActiveDetailOnly()
        {
            if (activeTab == ReferenceSectionTab.Capabilities)
            {
                RenderCapabilityDetail();
                return;
            }

            IReadOnlyList<ReferenceEntryDoc> sourceEntries = activeTab switch
            {
                ReferenceSectionTab.Components => ComponentDocs,
                ReferenceSectionTab.Actions => ActionDocs,
                ReferenceSectionTab.Expressions => ExpressionDocs,
                _ => EndpointDocs
            };
            string currentSelectedId = activeTab switch
            {
                ReferenceSectionTab.Components => selectedComponentId,
                ReferenceSectionTab.Actions => selectedActionId,
                ReferenceSectionTab.Expressions => selectedExpressionId,
                _ => selectedEndpointId
            };
            ReferenceEntryDoc? activeEntry = sourceEntries.FirstOrDefault(e =>
                string.Equals(e.Id, currentSelectedId, StringComparison.OrdinalIgnoreCase))
                ?? sourceEntries.FirstOrDefault();
            if (activeEntry is not null)
            {
                RenderReferenceEntryDetail(activeEntry);
            }
        }

        void RenderNavAndDetail()
        {
            if (isSideDrawerMode)
            {
                drawerDirectoryScroller.Visibility = isDetailOpenInDrawer ? Visibility.Collapsed : Visibility.Visible;
                detailBorder.Visibility = isDetailOpenInDrawer ? Visibility.Visible : Visibility.Collapsed;
                if (isDetailOpenInDrawer)
                {
                    RenderActiveDetailOnly();
                }
                else
                {
                    RenderDrawerDirectory();
                }
                return;
            }

            suppressNavSelection = true;
            try
            {
                navListView.Items.Clear();

                if (activeTab == ReferenceSectionTab.Capabilities)
                {
                    IReadOnlyList<CustomWidgetCapabilitySpec> matches =
                        CustomWidgetCapabilityRegistry.Search(searchQuery);
                    if (matches.Count == 0)
                    {
                        navEmptyHint.Visibility = Visibility.Visible;
                        detailStack.Children.Clear();
                        return;
                    }

                    navEmptyHint.Visibility = Visibility.Collapsed;
                    if (matches.All(m => !string.Equals(m.Id, selectedCapabilityId, StringComparison.OrdinalIgnoreCase)))
                    {
                        selectedCapabilityId = matches[0].Id;
                    }

                    ListViewItem? selectedLvItem = null;
                    foreach (CustomWidgetCapabilitySpec item in matches)
                    {
                        bool isSelected = string.Equals(item.Id, selectedCapabilityId, StringComparison.OrdinalIgnoreCase);
                        bool isPinned = isCapabilityPinned?.Invoke(item.Id) ?? false;
                        string catTag = ResolveShortCategoryTag(item.Category);
                        string sub = string.IsNullOrWhiteSpace(catTag) ? "@" + item.Id : $"@{item.Id} · {catTag}";

                        ListViewItem lvItem = CreateNavListItem(
                            id: item.Id,
                            glyph: item.Glyph,
                            title: FormatConciseTitle(item.GetTitle(isChinese)),
                            subtitle: sub,
                            showPinnedCheck: isPinned);
                        navListView.Items.Add(lvItem);
                        if (isSelected)
                        {
                            selectedLvItem = lvItem;
                        }
                    }

                    navListView.SelectedItem = selectedLvItem ?? navListView.Items.FirstOrDefault();
                    RenderCapabilityDetail();
                    return;
                }

                IReadOnlyList<ReferenceEntryDoc> sourceEntries = activeTab switch
                {
                    ReferenceSectionTab.Components => ComponentDocs,
                    ReferenceSectionTab.Actions => ActionDocs,
                    ReferenceSectionTab.Expressions => ExpressionDocs,
                    _ => EndpointDocs
                };

                List<ReferenceEntryDoc> filtered = FilterReferenceEntries(sourceEntries, searchQuery);
                if (filtered.Count == 0)
                {
                    navEmptyHint.Visibility = Visibility.Visible;
                    detailStack.Children.Clear();
                    return;
                }

                navEmptyHint.Visibility = Visibility.Collapsed;
                string currentSelectedId = activeTab switch
                {
                    ReferenceSectionTab.Components => selectedComponentId,
                    ReferenceSectionTab.Actions => selectedActionId,
                    ReferenceSectionTab.Expressions => selectedExpressionId,
                    _ => selectedEndpointId
                };

                if (filtered.All(e => !string.Equals(e.Id, currentSelectedId, StringComparison.OrdinalIgnoreCase)))
                {
                    currentSelectedId = filtered[0].Id;
                    SetSelectedEntryId(currentSelectedId);
                }

                ListViewItem? selectedEntryLvItem = null;
                foreach (ReferenceEntryDoc entry in filtered)
                {
                    bool isSelected = string.Equals(entry.Id, currentSelectedId, StringComparison.OrdinalIgnoreCase);
                    bool isPinned = !string.IsNullOrWhiteSpace(entry.LinkedCapabilityId) &&
                                    (isCapabilityPinned?.Invoke(entry.LinkedCapabilityId!) ?? false);
                    string grp = entry.GetGroup(isChinese);
                    string sub = string.IsNullOrWhiteSpace(grp) ? entry.Id : $"{entry.Id} · {grp}";

                    ListViewItem lvItem = CreateNavListItem(
                        id: entry.Id,
                        glyph: entry.Glyph,
                        title: FormatConciseTitle(entry.GetTitle(isChinese)),
                        subtitle: sub,
                        showPinnedCheck: isPinned);
                    navListView.Items.Add(lvItem);
                    if (isSelected)
                    {
                        selectedEntryLvItem = lvItem;
                    }
                }

                navListView.SelectedItem = selectedEntryLvItem ?? navListView.Items.FirstOrDefault();
                ReferenceEntryDoc activeEntry = filtered.First(e =>
                    string.Equals(e.Id, currentSelectedId, StringComparison.OrdinalIgnoreCase));
                RenderReferenceEntryDetail(activeEntry);
            }
            finally
            {
                suppressNavSelection = false;
            }
        }

        selectorBar.SelectionChanged += (_, _) =>
        {
            if (suppressTabSelection || selectorBar.SelectedItem?.Tag is not ReferenceSectionTab nextTab)
            {
                return;
            }

            if (activeTab == nextTab && !isDetailOpenInDrawer)
            {
                return;
            }

            activeTab = nextTab;
            isDetailOpenInDrawer = false;
            RenderNavAndDetail();
        };

        navListView.SelectionChanged += (_, _) =>
        {
            if (suppressNavSelection || navListView.SelectedItem is not ListViewItem { Tag: string id })
            {
                return;
            }

            SetSelectedEntryId(id);
            RenderActiveDetailOnly();
        };

        searchBox.TextChanged += (_, args) =>
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput ||
                args.Reason == AutoSuggestionBoxTextChangeReason.ProgrammaticChange)
            {
                string nextQuery = searchBox.Text ?? string.Empty;
                if (!string.Equals(searchQuery, nextQuery, StringComparison.Ordinal))
                {
                    searchQuery = nextQuery;
                    if (isSideDrawerMode)
                    {
                        isDetailOpenInDrawer = false;
                    }
                    RenderNavAndDetail();
                }
            }
        };

        root.ActualThemeChanged += (_, _) =>
        {
            ApplySurfaceBrushes();
            RenderNavAndDetail();
        };

        ApplySurfaceBrushes();
        RenderNavAndDetail();

        void NavigateToCapability(string? capabilityId)
        {
            if (!string.IsNullOrWhiteSpace(capabilityId) &&
                CustomWidgetCapabilityRegistry.GetById(capabilityId) is { } target)
            {
                selectedCapabilityId = target.Id;
                searchQuery = string.Empty;
                searchBox.Text = string.Empty;
                activeTab = ReferenceSectionTab.Capabilities;
                isDetailOpenInDrawer = isSideDrawerMode;
                suppressTabSelection = true;
                try
                {
                    foreach (SelectorBarItem sbi in selectorBar.Items)
                    {
                        sbi.IsSelected = sbi.Tag is ReferenceSectionTab t && t == ReferenceSectionTab.Capabilities;
                    }
                }
                finally
                {
                    suppressTabSelection = false;
                }
            }

            RenderNavAndDetail();
        }

        return new ApiReferenceViewHandle(
            RootElement: root,
            SelectCapability: NavigateToCapability,
            RefreshPinnedState: RenderNavAndDetail);
    }

    private static string FormatConciseTitle(string rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return string.Empty;
        }

        string trimmed = rawTitle.Trim();
        int parenIdx = trimmed.LastIndexOf(" (", StringComparison.Ordinal);
        if (parenIdx > 2 && trimmed.EndsWith(')'))
        {
            trimmed = trimmed[..parenIdx].TrimEnd();
        }

        return trimmed;
    }

    private static List<ReferenceEntryDoc> FilterReferenceEntries(
        IReadOnlyList<ReferenceEntryDoc> entries,
        string? rawQuery)
    {
        if (string.IsNullOrWhiteSpace(rawQuery))
        {
            return entries.ToList();
        }

        string q = rawQuery.Trim();
        return entries
            .Where(e =>
                e.Id.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                e.TitleZh.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                e.TitleEn.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                e.SummaryZh.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                e.SummaryEn.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                e.Members.Any(m =>
                    m.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    m.DescriptionZh.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    m.DescriptionEn.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private static StackPanel CreateIconText(string glyph, string text)
    {
        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        sp.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12 });
        sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    private static void CopyPlainText(string text)
    {
        try
        {
            var pkg = new DataPackage();
            pkg.SetText(text);
            DeskBoxClipboardWriteScope.MarkText(text);
            Clipboard.SetContent(pkg);
            Clipboard.Flush();
        }
        catch
        {
        }
    }

    private static Style? LookupStyle(string key) =>
        Application.Current.Resources.TryGetValue(key, out object? val) && val is Style style
            ? style
            : null;

    private static IReadOnlyList<ReferenceEntryDoc> BuildComponentDocs() =>
    [
        new(
            Id: "Stack · Grid · Tabs",
            Glyph: "\uE8A9",
            GroupZh: "布局与卡片",
            GroupEn: "Layout & Surfaces",
            TitleZh: "布局容器",
            TitleEn: "Layout Containers",
            SummaryZh: "线性、多列网格与多标签页容器；Grid 省略 rows 时自动折行排列子卡片。",
            SummaryEn: "Linear stacks, auto-flowing multi-column grids, cards, and multi-tab containers.",
            Members:
            [
                new("Stack / Wrap / Scroll / Expander", "Node", "纵横向排列、流式换行、纵向滚动区域与可折叠面板", "Linear stack, flow wrap, scroll viewport, and collapsible Expander"),
                new("Grid", "Node", "多列网格 (`columns: \"*,*\"` 或 `\"*,Auto\"`)，不写 `rows` 时子项自动换行", "Multi-column grid; auto-flows rows when `rows` is omitted"),
                new("Tabs", "Node", "分段多页切换 (`bind: \"state.tab\"`)，每页配置 `title`、`value` 与 `children`", "Segmented multi-page container bound to a state key"),
                new("Card / Divider / Spacer", "Node", "圆角分区卡片（最多嵌套 2 层）、细分隔线与弹性留白", "Rounded card surface (max 2 levels), divider line, and flexible spacer")
            ],
            SnippetJson: """
                {
                  "type": "Tabs",
                  "bind": "state.tab",
                  "children": [
                    {
                      "title": "概览",
                      "value": "overview",
                      "icon": "\uE9D9",
                      "children": [
                        {
                          "type": "Grid",
                          "columns": "*,*",
                          "spacing": 8,
                          "children": [
                            { "type": "Card", "tone": "subtle", "children": [{ "type": "Text", "text": "区块 A" }] },
                            { "type": "Card", "tone": "subtle", "children": [{ "type": "Text", "text": "区块 B" }] }
                          ]
                        }
                      ]
                    }
                  ]
                }
                """,
            LinkedCapabilityId: "visual-containers"),

        new(
            Id: "Text · Image · Badge",
            Glyph: "\uE8D2",
            GroupZh: "布局与卡片",
            GroupEn: "Layout & Surfaces",
            TitleZh: "文本、图片与徽标",
            TitleEn: "Text, Image & Badges",
            SummaryZh: "大号数值排版、圆角封面/网络图片、状态胶囊与左右对齐键值行。",
            SummaryEn: "Typography styles, rounded images, status badges, and aligned key-value rows.",
            Members:
            [
                new("Text", "Node", "支持 `caption`、`body`、`bodyStrong`、`title`、`hero`、`mono` 字号与语义色", "Typography node with semantic styles and theme-aware colors"),
                new("Image", "Node", "支持沙盒附件、`${$device.mediaCover}` 封面与 `https://` 图片，可设圆角", "Rounded image view for local files, media cover art, and HTTPS URLs"),
                new("Badge / KeyValueRow / EmptyState", "Node", "语义状态徽标、左右键值指标行与空状态引导图", "Status pill, aligned label-value row, and empty-state placeholder")
            ],
            SnippetJson: """
                {
                  "type": "Grid",
                  "columns": "Auto,*,Auto",
                  "spacing": 8,
                  "children": [
                    { "type": "Image", "column": 0, "src": "${$device.mediaCover}", "width": 40, "height": 40, "cornerRadius": 8 },
                    { "type": "Text", "column": 1, "text": "${$device.mediaSummary}", "style": "bodyStrong", "maxLines": 1 },
                    { "type": "Badge", "column": 2, "text": "${$device.mediaStatus}", "tone": "accent" }
                  ]
                }
                """,
            LinkedCapabilityId: "visual-containers"),

        new(
            Id: "Slider · Toggle · Input",
            Glyph: "\uE70F",
            GroupZh: "交互与表单",
            GroupEn: "Controls & Inputs",
            TitleZh: "输入、开关与滑块",
            TitleEn: "Inputs, Toggles & Sliders",
            SummaryZh: "即时生效的滑块、开关、分段选择器、文本输入框与快捷控制磁贴。",
            SummaryEn: "Live two-way sliders, toggles, segmented pickers, text inputs, and control tiles.",
            Members:
            [
                new("Slider", "Node", "支持绑定状态或硬件变量（如 `${$device.volume}`），拖动即时触发 `onChange` (`${$value}`)", "Live slider dispatching `onChange` with `${$value}`"),
                new("Toggle / CheckBox / Segmented / Select", "Node", "开关、复选框、胶囊分段切换与下拉选择框", "Switch, checkbox, segmented bar, and dropdown selector"),
                new("Input / TextArea / NumberBox", "Node", "单行输入（回车提交）、多行文本（Ctrl+Enter 提交）与数字框", "Single-line input, multi-line text area, and number box"),
                new("Button / IconButton / ControlTile", "Node", "标准按钮、方形图标按钮与 Win11 控制中心同款快捷磁贴", "Buttons, icon buttons, and Win11 Quick Settings tiles")
            ],
            SnippetJson: """
                {
                  "type": "Slider",
                  "title": "主音量",
                  "value": "${$device.volume}",
                  "min": 0,
                  "max": 100,
                  "step": 2,
                  "unit": "%",
                  "onChange": {
                    "type": "system.control",
                    "targetId": "volume.set",
                    "value": "${$value}"
                  }
                }
                """,
            LinkedCapabilityId: "forms-inputs"),

        new(
            Id: "RingGauge · Sparkline",
            Glyph: "\uE9D9",
            GroupZh: "图表与列表",
            GroupEn: "Charts & Lists",
            TitleZh: "图表与进度条",
            TitleEn: "Charts & Gauges",
            SummaryZh: "环形仪表、横向比例条、平滑趋势折线、打卡热力图与进度条。",
            SummaryEn: "Circular gauges, bar lists, trend sparklines, activity heatmaps, and progress bars.",
            Members:
            [
                new("RingGauge / ProgressBar / ProgressRow", "Node", "环形百分比仪表、线性进度条与带数值说明的复合进度行", "Circular KPI gauge, linear progress bar, and labeled progress row"),
                new("Sparkline / BarList / HeatmapGrid", "Node", "平滑折线面积图、排行榜比例条形图与多周打卡热力矩阵", "Trend sparkline, ranked proportion bars, and daily heatmap matrix")
            ],
            SnippetJson: """
                {
                  "type": "Grid",
                  "columns": "*,*",
                  "spacing": 8,
                  "children": [
                    { "type": "RingGauge", "progress": "${$sys.cpu.percent}", "title": "CPU", "unit": "%", "color": "accent" },
                    { "type": "RingGauge", "progress": "${$sys.memory.percent}", "title": "内存", "subtitle": "${$sys.memory.compactText}", "unit": "%", "color": "cyan" }
                  ]
                }
                """,
            LinkedCapabilityId: "micro-charts"),

        new(
            Id: "List · CalendarGrid",
            Glyph: "\uE8FD",
            GroupZh: "图表与列表",
            GroupEn: "Charts & Lists",
            TitleZh: "列表与专用卡片",
            TitleEn: "Lists & Domain Cards",
            SummaryZh: "带筛选与悬停操作的动态列表、月历网格、文件拖放区及番茄钟/倒数日成品卡。",
            SummaryEn: "Dynamic filtered lists, month calendar grid, file drop zone, and domain cards.",
            Members:
            [
                new("List", "Node", "绑定 `collections` 列表，支持条件过滤 `filter`、空状态与行悬停操作 `hoverActions`", "Filtered collection view with empty state and row hover actions"),
                new("CalendarGrid / DropZone / FileChip", "Node", "带农历节气的交互月历、文件拖放面板与可点击定位的文件胶囊", "Interactive lunar/solar month grid, file drop zone, and file chip"),
                new("PomodoroCard / CountdownList / StatCard / ProcessList", "Node", "开箱即用的番茄钟、公农历倒数日、硬件指标卡与进程列表", "Ready-made Pomodoro, Countdown, and hardware telemetry cards")
            ],
            SnippetJson: """
                {
                  "type": "CalendarGrid",
                  "bind": "state.selectedDate",
                  "collection": "events",
                  "dateField": "date"
                }
                """,
            LinkedCapabilityId: "calendar-grid")
    ];

    private static IReadOnlyList<ReferenceEntryDoc> BuildActionDocs() =>
    [
        new(
            Id: "state.* / collection.*",
            Glyph: "\uE8FD",
            GroupZh: "数据读写",
            GroupEn: "Data & State",
            TitleZh: "状态与列表读写",
            TitleEn: "State & List Actions",
            SummaryZh: "修改组件状态、增删改查本地持久化列表，支持 `guard` 条件判断与 `then` 连续动作。",
            SummaryEn: "Updates reactive state and persistent lists, with optional `guard` conditions and `then` chains.",
            Members:
            [
                new("state.set / state.toggle / state.increment", "Action", "写入状态值、反转布尔开关或累加计数器", "Sets a state value, toggles a boolean, or increments a number"),
                new("collection.prepend / collection.append", "Action", "向列表头部或尾部新增记录，自动生成唯一 `id` 并落盘保存", "Adds a record to a persistent collection with auto-generated `id`"),
                new("collection.update / collection.remove / collection.clear", "Action", "按 `targetId` (`${$item.id}`) 修改单条记录、删除单条或清空列表", "Updates a record by ID, deletes one item, or clears the list"),
                new("collection.exportMarkdown / clipboard.copy", "Action", "将列表导出为 Markdown 文本或复制任意内容到剪贴板", "Exports list as Markdown or copies text to clipboard")
            ],
            SnippetJson: """
                {
                  "type": "collection.prepend",
                  "guard": "${state.draft != ''}",
                  "collection": "tasks",
                  "item": { "title": "${state.draft}", "done": "false" },
                  "then": [
                    { "type": "state.set", "key": "draft", "value": "" }
                  ]
                }
                """,
            LinkedCapabilityId: "collections-lists"),

        new(
            Id: "system.control",
            Glyph: "\uE713",
            GroupZh: "系统与本地动作",
            GroupEn: "System & Local",
            TitleZh: "音量、亮度与开关",
            TitleEn: "Volume, Brightness & Toggles",
            SummaryZh: "调节音量与亮度、控制音乐播放、切换 Wi-Fi/蓝牙/常亮防休眠，或直达系统设置页。",
            SummaryEn: "Controls volume, brightness, media transport, Wi-Fi/Bluetooth/Keep-Awake, and Windows Settings.",
            Members:
            [
                new("volume.set / volume.muteToggle / audio.output / brightness.set", "targetId", "调节主音量、切换静音、选输出设备或调节显示器亮度", "Volume, mute, audio output, and display brightness commands"),
                new("media.playPause / media.previous / media.next", "targetId", "控制当前正在播放的桌面音乐或视频", "Global media playback transport commands"),
                new("radio.wifiToggle / radio.bluetoothToggle / power.keepAwakeToggle / power.lock", "targetId", "切换 Wi-Fi、蓝牙、屏幕常亮或立即锁屏", "Wi-Fi, Bluetooth, Keep-Awake, and workstation lock commands"),
                new("system.openSetting", "Action", "打开系统设置页（如 `wifi`, `bluetooth`, `sound`, `display`, `power`, `storage`, `taskmgr`）", "Opens a whitelisted Windows Settings page or Task Manager")
            ],
            SnippetJson: """
                {
                  "type": "system.control",
                  "targetId": "brightness.set",
                  "value": "${$value}"
                }
                """,
            LinkedCapabilityId: "display-and-audio"),

        new(
            Id: "text.transform / file.transform",
            Glyph: "\uE8AB",
            GroupZh: "系统与本地动作",
            GroupEn: "System & Local",
            TitleZh: "文本与文件转换",
            TitleEn: "Text & File Transform",
            SummaryZh: "纯本地完成 JSON 美化、Base64/URL 编解码、图片格式互转与 SHA256 校验，不改原文件。",
            SummaryEn: "Offline local text formatting/encoding and non-destructive image/CSV/checksum conversion.",
            Members:
            [
                new("text.transform", "Action", "支持 `jsonPretty`、`base64Encode`、`urlEncode`、`timestampToDate`、`uuid`、`sha256` 等 15 种算子", "15 local in-memory text formatting, encoding, and hash operations"),
                new("file.transform", "Action", "支持 `png`、`jpg`、`webp`、`ico`、`csvToJson`、`mdToHtml`、`sha256` 等本地转换", "Converts dropped/picked files and populates `$transform.*` tokens"),
                new("file.pick / file.open / file.reveal", "Action", "选择本地文件、默认程序打开或在资源管理器中定位高亮", "Picks a file, opens it, or reveals it in File Explorer")
            ],
            SnippetJson: """
                {
                  "type": "text.transform",
                  "format": "jsonPretty",
                  "text": "${state.input != '' ? state.input : $clipboard.text}",
                  "key": "output",
                  "mode": "copy"
                }
                """,
            LinkedCapabilityId: "clipboard-workspace"),

        new(
            Id: "launcher.open / ai.run",
            Glyph: "\uE756",
            GroupZh: "外部连接与通知",
            GroupEn: "Workflow & AI",
            TitleZh: "启动、AI 与通知",
            TitleEn: "Launcher, AI & Alerts",
            SummaryZh: "打开常用目录与应用协议、在组件内调用 AI 处理文本、刷新数据源或发送系统通知。",
            SummaryEn: "Opens folders/apps, runs structured in-widget AI tasks, refreshes feeds, and sends notifications.",
            Members:
            [
                new("launcher.open / url.open", "Action", "打开下载/文档目录、`vscode://` / `ms-screenclip:` 协议或浏览器网页", "Opens user folders, whitelisted app URIs, or HTTPS links"),
                new("ai.run", "Action", "执行单并发结构化 AI 提示词，自动将返回结果写入 `state` 或列表", "Runs structured LLM prompt and maps output into state or collections"),
                new("source.refresh / reminder.notify / toast.show", "Action", "立即刷新数据源、发送系统通知弹窗或显示组件内轻提示", "Refreshes data sources, sends system toast, or shows in-widget banner")
            ],
            SnippetJson: """
                {
                  "type": "launcher.open",
                  "targetId": "ms-screenclip:"
                }
                """,
            LinkedCapabilityId: "quick-launcher")
    ];

    private static IReadOnlyList<ReferenceEntryDoc> BuildExpressionDocs() =>
    [
        new(
            Id: "${...} · $value · tz()",
            Glyph: "\uE943",
            GroupZh: "表达式语法",
            GroupEn: "Syntax & Functions",
            TitleZh: "运算符与时区函数",
            TitleEn: "Operators & tz()",
            SummaryZh: "在字符串中用 `${...}` 插入动态数据，支持四则运算、条件判断、滑块实时值与全球时区转换。",
            SummaryEn: "Inline `${...}` expressions supporting math, conditionals, `${$value}`, and `tz()` formatting.",
            Members:
            [
                new("?? / ? : / == / != / && / ||", "Operator", "空值兜底 (`${state.a ?? '--'}`) 与三元条件判断 (`${state.done == 'true' ? '已完成' : '待办'}`)", "Null-coalescing, ternary conditional, and logical operators"),
                new("${$value} / ${$item.title} / ${$index}", "Context", "控件当前变更值、列表当前项字段与序号（从 0 开始，`${$rank}` 从 1 开始）", "Control payload value and list item context variables"),
                new("${tz('America/New_York', 'HH:mm')}", "Function", "按指定时区或 `UTC+8` 偏移格式化当前时间", "Formats live time in any IANA/Windows timezone or UTC offset"),
                new("${state.key} / ${$settings.key} / ${collections.items.count}", "Scope", "读取组件状态、右键用户设置项或列表条数统计", "Reads widget state, user settings, or collection counts")
            ],
            SnippetJson: """
                {
                  "type": "Text",
                  "text": "纽约 ${tz('America/New_York', 'HH:mm')} · 伦敦 ${tz('Europe/London', 'HH:mm')}"
                }
                """,
            LinkedCapabilityId: "world-clock"),

        new(
            Id: "$device.* · $sys.*",
            Glyph: "\uE9D9",
            GroupZh: "内置变量",
            GroupEn: "Built-In Tokens",
            TitleZh: "设备与硬件变量",
            TitleEn: "Device & Hardware Tokens",
            SummaryZh: "点击任意胶囊即可复制单个变量；配合 `deviceState`、`systemMetrics` 或 `storageDrives` 数据源使用。",
            SummaryEn: "Click any chip below to copy a token; pair with `deviceState`, `systemMetrics`, or `storageDrives`.",
            Members:
            [
                new("${$device.volume} / ${$device.muted} / ${$device.brightness} / ${$device.audioDeviceName}", "deviceState", "系统音量、静音状态、屏幕亮度与音频输出设备名", "Volume %, mute state, brightness %, and audio output device"),
                new("${$device.mediaTitle} / ${$device.mediaArtist} / ${$device.mediaToggleIcon} / ${$device.mediaCover}", "deviceState", "当前播放曲目、歌手、播放/暂停动态图标与封面图", "Active media title, artist, play/pause glyph, and cover art"),
                new("${$device.wifiEnabled} / ${$device.wifiStatus} / ${$device.bluetoothEnabled} / ${$device.keepAwake}", "deviceState", "Wi-Fi、蓝牙开关状态与屏幕常亮状态", "Wi-Fi, Bluetooth, and Keep-Awake status tokens"),
                new("${$sys.cpu.percent} / ${$sys.memory.percent} / ${$sys.gpu.percent} / ${$sys.net.downText} / ${$sys.net.upText}", "systemMetrics", "CPU、内存、显卡占用百分比与实时上下行网速", "CPU, RAM, GPU usage %, and live download/upload speeds"),
                new("${$drives.count} / ${$drives.summary} / ${$drives.primaryPercent}", "storageDrives", "就绪磁盘数量、容量摘要与主分区占用率", "Mounted drive count, capacity summary, and primary drive %")
            ],
            SnippetJson: """
                {
                  "sources": [
                    { "type": "deviceState", "intervalMs": 3000 },
                    { "type": "systemMetrics", "intervalMs": 2000 }
                  ]
                }
                """,
            LinkedCapabilityId: "device-state"),

        new(
            Id: "$weather.* · $calendar.*",
            Glyph: "\uE753",
            GroupZh: "内置变量",
            GroupEn: "Built-In Tokens",
            TitleZh: "天气、日历与工具变量",
            TitleEn: "Weather, Calendar & Tools",
            SummaryZh: "覆盖免密钥天气、农历节气、世界时钟、剪贴板识别、番茄钟、倒数日与 AI 账单的常用变量。",
            SummaryEn: "Tokens for weather, lunar/solar calendar, clock, clipboard, Pomodoro, countdowns, and AI usage.",
            Members:
            [
                new("${$weather.city} / ${$weather.tempText} / ${$weather.condition} / ${$weather.icon} / ${$weather.rangeText}", "weather", "城市、当前气温、天气现象、图标与全天温差", "City, current temp, condition, icon, and daily range"),
                new("${$calendar.date} / ${$calendar.weekday} / ${$calendar.lunar} / ${$calendar.festival}", "calendar", "公历日期、星期、农历月日与节气节日", "Gregorian date, weekday, lunar date, and solar term/festival"),
                new("${$clock.fullTime} / ${$clock.utcTime} / ${$clock.unixSeconds}", "clock", "本地时间、UTC 时间与秒级时间戳", "Local time, UTC time, and Unix timestamp"),
                new("${$clipboard.preview} / ${$clipboard.kindLabel} / ${$clipboard.length}", "clipboard", "剪贴板单行摘要、识别类型与字符长度", "Clipboard preview, detected kind, and character count"),
                new("${$pomodoro.remainingText} / ${$countdown.hero.days} / ${$ai.todayCost} / ${$drop.path}", "Domain", "番茄钟剩余时间、置顶纪念日天数、今日 AI 消耗与拖入文件路径", "Pomodoro timer, countdown days, today's AI cost, and dropped file path")
            ],
            SnippetJson: """
                {
                  "sources": [
                    { "type": "weather", "intervalMs": 900000 },
                    { "type": "calendar", "intervalMs": 60000 }
                  ]
                }
                """,
            LinkedCapabilityId: "weather")
    ];

    private static IReadOnlyList<ReferenceEntryDoc> BuildPublicEndpointDocs() =>
    [
        new(
            Id: "zhihu-daily",
            Glyph: "\uE774",
            GroupZh: "中文公开接口 (免密钥)",
            GroupEn: "CN Public Feeds (Zero-Key)",
            TitleZh: "知乎日报",
            TitleEn: "Zhihu Daily",
            SummaryZh: "免密钥公开 JSON 接口，稳定返回当日精选文章标题、链接与作者说明。",
            SummaryEn: "Zero-key public JSON endpoint returning daily curated stories, URLs, and author hints.",
            Members:
            [
                new("https://news-at.zhihu.com/api/4/news/latest", "URL", "GET 公开接口，推荐轮询间隔 `600000` (10 分钟)", "HTTPS GET endpoint; recommended intervalMs: 600000"),
                new("itemsPath: \"$.stories\"", "Mapping", "常用字段：`title` (标题)、`url` (链接)、`hint` (作者提示)", "Maps `title`, `url`, and `hint` fields")
            ],
            SnippetJson: """
                {
                  "collections": { "items": { "maxItems": 20 } },
                  "sources": [
                    {
                      "id": "zhihu",
                      "type": "httpJson",
                      "url": "https://news-at.zhihu.com/api/4/news/latest",
                      "intervalMs": 600000,
                      "targetCollection": "items",
                      "itemsPath": "$.stories",
                      "fieldMap": { "title": "title", "url": "url", "sub": "hint" }
                    }
                  ]
                }
                """,
            LinkedCapabilityId: "http-and-rss-feeds"),

        new(
            Id: "v2ex-hot",
            Glyph: "\uE8F2",
            GroupZh: "中文公开接口 (免密钥)",
            GroupEn: "CN Public Feeds (Zero-Key)",
            TitleZh: "V2EX 热门主题",
            TitleEn: "V2EX Hot Topics",
            SummaryZh: "返回 V2EX 社区当前回复量最高的热门讨论帖，根节点直接为 JSON 数组。",
            SummaryEn: "Returns top active V2EX developer discussions as a root JSON array.",
            Members:
            [
                new("https://www.v2ex.com/api/topics/hot.json", "URL", "GET 公开接口，推荐轮询间隔 `600000`", "Public HTTPS GET JSON array"),
                new("itemsPath: \"$\"", "Mapping", "常用字段：`title` (标题)、`url` (链接)、`replies` (回复数)", "Maps `title`, `url`, and `replies` count")
            ],
            SnippetJson: """
                {
                  "collections": { "topics": { "maxItems": 15 } },
                  "sources": [
                    {
                      "id": "v2ex",
                      "type": "httpJson",
                      "url": "https://www.v2ex.com/api/topics/hot.json",
                      "intervalMs": 600000,
                      "targetCollection": "topics",
                      "itemsPath": "$",
                      "fieldMap": { "title": "title", "url": "url", "replies": "replies" }
                    }
                  ]
                }
                """,
            LinkedCapabilityId: "http-and-rss-feeds"),

        new(
            Id: "cn-tech-rss",
            Glyph: "\uE8A1",
            GroupZh: "中文公开接口 (免密钥)",
            GroupEn: "CN Public Feeds (Zero-Key)",
            TitleZh: "中文科技 RSS",
            TitleEn: "CN Tech RSS Feeds",
            SummaryZh: "少数派、IT之家、机核与阮一峰周刊标准订阅地址，自动提取标题、摘要与链接。",
            SummaryEn: "Verified SSPai, ITHome, Gcores, and Ruanyifeng RSS/Atom feeds.",
            Members:
            [
                new("https://sspai.com/feed", "RSS", "少数派最新深度文章", "SSPai latest productivity & software articles"),
                new("https://www.ithome.com/rss/", "RSS", "IT之家实时科技数码快讯", "ITHome real-time tech news feed"),
                new("https://www.gcores.com/rss", "RSS", "机核网游戏与泛文化专题", "Gcores gaming & culture feed"),
                new("https://www.ruanyifeng.com/blog/atom.xml", "Atom", "阮一峰科技爱好者周刊", "Ruanyifeng Weekly Atom feed")
            ],
            SnippetJson: """
                {
                  "collections": { "articles": { "maxItems": 20 } },
                  "sources": [
                    {
                      "id": "sspai",
                      "type": "rss",
                      "url": "https://sspai.com/feed",
                      "intervalMs": 900000,
                      "targetCollection": "articles",
                      "maxItems": 15
                    }
                  ]
                }
                """,
            LinkedCapabilityId: "http-and-rss-feeds"),

        new(
            Id: "hackernews",
            Glyph: "\uE774",
            GroupZh: "全球开发与行情 (免密钥)",
            GroupEn: "Global Tech & Markets",
            TitleZh: "Hacker News 热榜",
            TitleEn: "Hacker News Top",
            SummaryZh: "Algolia 官方开放接口，稳定返回 HN 首页热榜标题、链接、得分与作者。",
            SummaryEn: "Official Algolia HN front-page JSON API with points, author, and URL.",
            Members:
            [
                new("https://hn.algolia.com/api/v1/search?tags=front_page", "URL", "官方 JSON 接口 (`itemsPath: \"$.hits\"`)", "Official Algolia HN front_page JSON endpoint"),
                new("https://hnrss.org/frontpage", "RSS", "备选纯 RSS 订阅流", "Alternative Hacker News RSS feed")
            ],
            SnippetJson: """
                {
                  "collections": { "stories": { "maxItems": 20 } },
                  "sources": [
                    {
                      "id": "hn",
                      "type": "httpJson",
                      "url": "https://hn.algolia.com/api/v1/search?tags=front_page",
                      "intervalMs": 600000,
                      "targetCollection": "stories",
                      "itemsPath": "$.hits",
                      "fieldMap": { "title": "title", "url": "url", "points": "points", "author": "author" }
                    }
                  ]
                }
                """,
            LinkedCapabilityId: "http-and-rss-feeds"),

        new(
            Id: "fx-crypto",
            Glyph: "\uEAFD",
            GroupZh: "全球开发与行情 (免密钥)",
            GroupEn: "Global Tech & Markets",
            TitleZh: "汇率与加密行情",
            TitleEn: "FX & Crypto Rates",
            SummaryZh: "免密钥查询美元兑人民币/日元/欧元实时汇率，以及 BTC/ETH 现货价格。",
            SummaryEn: "Zero-key live USD foreign exchange rates and BTC/ETH spot prices.",
            Members:
            [
                new("https://open.er-api.com/v6/latest/USD", "FX JSON", "国际汇率接口（`$.rates.CNY`、`$.rates.JPY`、`$.rates.EUR`）", "Zero-key USD exchange rates endpoint"),
                new("https://api.coingecko.com/api/v3/simple/price?ids=bitcoin,ethereum&vs_currencies=usd", "Crypto", "主流加密资产美元现价（`$.bitcoin.usd`、`$.ethereum.usd`）", "Zero-key BTC & ETH USD spot price endpoint")
            ],
            SnippetJson: """
                {
                  "state": { "cny": "--", "jpy": "--", "eur": "--" },
                  "sources": [
                    {
                      "id": "fx",
                      "type": "httpJson",
                      "url": "https://open.er-api.com/v6/latest/USD",
                      "intervalMs": 1800000,
                      "stateMap": {
                        "cny": "$.rates.CNY",
                        "jpy": "$.rates.JPY",
                        "eur": "$.rates.EUR"
                      }
                    }
                  ]
                }
                """,
            LinkedCapabilityId: "http-and-rss-feeds")
    ];
}
