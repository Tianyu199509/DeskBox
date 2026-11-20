using DeskBox.Controls.WidgetContents;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Helpers;

/// <summary>
/// Unified Fluent 2 Custom Widget &amp; API Reference Manual driven directly from
/// <see cref="CustomWidgetCapabilityRegistry.All"/> (Single Source of Truth),
/// with one shared responsive master-detail view for both modal and Studio side-drawer modes.
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

    private sealed record UnifiedReferenceEntry(
        string Id,
        string Glyph,
        string GroupLabel,
        string Title,
        string Summary,
        string? CodeBadge,
        IReadOnlyList<string> RequiredCapabilityIds,
        IReadOnlyList<CustomWidgetApiMemberDoc> Members,
        string SnippetJson,
        string? LinkedCapabilityId,
        bool IsPublicEndpoint = false);

    public sealed record ApiReferenceViewHandle(
        FrameworkElement RootElement,
        Action<string?> SelectCapability,
        Action RefreshPinnedState);

    public static async Task ShowAsync(
        XamlRoot xamlRoot,
        ElementTheme theme,
        LocalizationService? loc,
        string? initialCapabilityId = null,
        Func<string, bool>? isCapabilityPinned = null,
        Action<string>? onTogglePinCapability = null)
    {
        if (xamlRoot is null)
        {
            return;
        }

        string T(string k) => AiProviderConfigFormController.Translate(loc, k);
        ApiReferenceViewHandle handle = CreateReferenceView(
            loc,
            initialCapabilityId,
            isCapabilityPinned,
            onTogglePinCapability,
            onInsertIntoPrompt: null,
            onRequestClose: null,
            isSideDrawerMode: false,
            requestedTheme: theme);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = theme,
            Title = T("CustomWidget.ApiDocs.DialogTitle"),
            Content = handle.RootElement,
            CloseButtonText = T("Common.Close"),
            DefaultButton = ContentDialogButton.None
        };
        if (Application.Current?.Resources is { } res &&
            res.TryGetValue("DefaultContentDialogStyle", out object? dStyle) &&
            dStyle is Style dialogStyle)
        {
            dialog.Style = dialogStyle;
        }
        dialog.Resources["ContentDialogMaxWidth"] = 1000d;

        await dialog.ShowAsync();
    }

    public static ApiReferenceViewHandle CreateReferenceView(
        LocalizationService? loc,
        string? initialCapabilityId = null,
        Func<string, bool>? isCapabilityPinned = null,
        Action<string>? onTogglePinCapability = null,
        Action<string>? onInsertIntoPrompt = null,
        Action? onRequestClose = null,
        bool isSideDrawerMode = false,
        ElementTheme requestedTheme = ElementTheme.Default)
    {
        string culture = AiProviderConfigFormController.ResolveCultureName(loc);
        string T(string k) => AiProviderConfigFormController.Translate(loc, k);
        string F(string k, params object[] a) => AiProviderConfigFormController.Format(loc, k, a);

        ReferenceSectionTab activeTab = ReferenceSectionTab.Capabilities;
        string searchQuery = string.Empty;
        bool suppressTabSelection = false;
        bool suppressNavSelection = false;

        CustomWidgetCapabilitySpec? initialMatchedCap = CustomWidgetCapabilityRegistry.GetById(initialCapabilityId);
        bool isDetailOpenInDrawer = isSideDrawerMode && initialMatchedCap is not null;
        string selectedId = initialMatchedCap?.Id ?? CustomWidgetCapabilityRegistry.All[0].Id;

        var root = new Grid
        {
            RequestedTheme = requestedTheme,
            RowSpacing = isSideDrawerMode ? 10 : 12,
            Padding = isSideDrawerMode ? new Thickness(16, 12, 16, 14) : new Thickness(0)
        };
        if (!isSideDrawerMode)
        {
            root.Width = 900;
            root.MaxWidth = 940;
        }

        Brush LookupBrush(string key) =>
            NeutralInteractionBrush.ResolveThemedResource(key, root) ?? new SolidColorBrush(Colors.Transparent);

        string ResolveShortCategoryTag(CustomWidgetCapabilityCategory cat) => cat switch
        {
            CustomWidgetCapabilityCategory.DataSource => T("CustomWidget.ApiDocs.Category.Sources"),
            CustomWidgetCapabilityCategory.Component => T("CustomWidget.ApiDocs.Category.Components"),
            CustomWidgetCapabilityCategory.Action => T("CustomWidget.ApiDocs.Category.Actions"),
            CustomWidgetCapabilityCategory.ConfigAndSecurity => T("CustomWidget.ApiDocs.Category.Security"),
            _ => string.Empty
        };

        UnifiedReferenceEntry MapSpecToEntry(CustomWidgetCapabilitySpec spec) => new(
            Id: spec.Id,
            Glyph: spec.Glyph,
            GroupLabel: ResolveShortCategoryTag(spec.Category),
            Title: FormatConciseTitle(spec.GetTitle(culture)),
            Summary: spec.GetSummary(culture),
            CodeBadge: "@" + spec.Id,
            RequiredCapabilityIds: spec.RequiredCapabilityIds,
            Members: spec.Members,
            SnippetJson: spec.GoldenSnippetJson,
            LinkedCapabilityId: spec.Id);

        IReadOnlyList<UnifiedReferenceEntry> curatedEndpoints = BuildCuratedEndpointEntries(culture);

        List<UnifiedReferenceEntry> ResolveEntriesForActiveTab()
        {
            IEnumerable<UnifiedReferenceEntry> source = activeTab switch
            {
                ReferenceSectionTab.Components => CustomWidgetCapabilityRegistry.All
                    .Where(static s => s.Category == CustomWidgetCapabilityCategory.Component)
                    .Select(MapSpecToEntry),
                ReferenceSectionTab.Actions => CustomWidgetCapabilityRegistry.All
                    .Where(static s => s.Category == CustomWidgetCapabilityCategory.Action)
                    .Select(MapSpecToEntry),
                ReferenceSectionTab.Expressions => CustomWidgetCapabilityRegistry.All
                    .Where(static s => s.Category is CustomWidgetCapabilityCategory.DataSource or CustomWidgetCapabilityCategory.ConfigAndSecurity)
                    .Select(MapSpecToEntry),
                ReferenceSectionTab.PublicEndpoints => curatedEndpoints,
                _ => (string.IsNullOrWhiteSpace(searchQuery)
                        ? CustomWidgetCapabilityRegistry.All
                        : CustomWidgetCapabilityRegistry.Search(searchQuery))
                    .Select(MapSpecToEntry)
            };

            if (activeTab == ReferenceSectionTab.Capabilities || string.IsNullOrWhiteSpace(searchQuery))
            {
                return source.ToList();
            }

            string q = searchQuery.Trim();
            return source.Where(e =>
                e.Id.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                e.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                e.Summary.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                e.Members.Any(m =>
                    m.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    ResolveMemberDocText(e.LinkedCapabilityId, m, culture).Contains(q, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        string copyMdLabel = T(isSideDrawerMode ? "CustomWidget.ApiDocs.CopyMarkdownShort" : "CustomWidget.ApiDocs.CopyMarkdown");
        var copyMdBtn = new Button
        {
            Height = isSideDrawerMode ? 28 : 32,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 0, 10, 0),
            FontSize = 12,
            Content = CreateIconText("\uE8C8", copyMdLabel)
        };
        ToolTipService.SetToolTip(copyMdBtn, T("CustomWidget.ApiDocs.CopyMarkdown"));
        copyMdBtn.Click += async (_, _) =>
        {
            McpQuickConnectPanelBuilder.CopyTextToClipboard(CustomWidgetCapabilityRegistry.GenerateApiReferenceMarkdown(culture));
            copyMdBtn.Content = CreateIconText("\uE73E", T("CustomWidget.Native.Dialog.CopiedButton"));
            await Task.Delay(1500);
            if (copyMdBtn.IsLoaded)
            {
                copyMdBtn.Content = CreateIconText("\uE8C8", copyMdLabel);
            }
        };

        var selectorBar = new SelectorBar
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        (ReferenceSectionTab Tab, string Glyph, string Label)[] sectionTabs =
        [
            (ReferenceSectionTab.Capabilities, "\uE74C", T("CustomWidget.ApiDocs.Tab.Capabilities")),
            (ReferenceSectionTab.Components, "\uE8A9", T("CustomWidget.ApiDocs.Tab.Components")),
            (ReferenceSectionTab.Actions, "\uE945", T("CustomWidget.ApiDocs.Tab.Actions")),
            (ReferenceSectionTab.Expressions, "\uE943", T("CustomWidget.ApiDocs.Tab.Expressions")),
            (ReferenceSectionTab.PublicEndpoints, "\uE774", T("CustomWidget.ApiDocs.Tab.Endpoints"))
        ];
        suppressTabSelection = true;
        foreach ((ReferenceSectionTab tab, string glyph, string label) in sectionTabs)
        {
            selectorBar.Items.Add(new SelectorBarItem
            {
                Tag = tab,
                Text = label,
                Icon = new FontIcon { Glyph = glyph, FontSize = 13 },
                IsSelected = tab == activeTab
            });
        }
        suppressTabSelection = false;

        var selectorScroller = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = selectorBar
        };

        int nextRow = 0;
        if (isSideDrawerMode)
        {
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var drawerHeader = new Grid { ColumnSpacing = 8 };
            drawerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            drawerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            drawerHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(new FontIcon { Glyph = "\uE8A1", FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            titleStack.Children.Add(new TextBlock { Text = T("CustomWidget.ApiDocs.Button"), FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
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
                ToolTipService.SetToolTip(closeBtn, T("Common.Close"));
                AutomationProperties.SetName(closeBtn, T("Common.Close"));
                closeBtn.Click += (_, _) => onRequestClose();
                Grid.SetColumn(closeBtn, 2);
                drawerHeader.Children.Add(closeBtn);
            }

            Grid.SetRow(drawerHeader, nextRow++);
            root.Children.Add(drawerHeader);

            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(selectorScroller, nextRow++);
            root.Children.Add(selectorScroller);
        }
        else
        {
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var tabHeaderRow = new Grid { ColumnSpacing = 8 };
            tabHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tabHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tabHeaderRow.Children.Add(selectorScroller);
            Grid.SetColumn(copyMdBtn, 1);
            tabHeaderRow.Children.Add(copyMdBtn);
            Grid.SetRow(tabHeaderRow, nextRow++);
            root.Children.Add(tabHeaderRow);
        }

        // Search Row
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var searchBox = new AutoSuggestBox
        {
            PlaceholderText = T("CustomWidget.ApiDocs.SearchPlaceholder"),
            QueryIcon = new SymbolIcon(Symbol.Find),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Grid.SetRow(searchBox, nextRow++);
        root.Children.Add(searchBox);

        // Unified Responsive Body Grid
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = isSideDrawerMode ? new GridLength(1, GridUnitType.Star) : new GridLength(500)
        });
        var bodyGrid = new Grid { ColumnSpacing = isSideDrawerMode ? 0 : 14 };
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = isSideDrawerMode ? new GridLength(1, GridUnitType.Star) : new GridLength(272)
        });
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        Grid.SetRow(bodyGrid, nextRow);
        root.Children.Add(bodyGrid);

        var navListView = new ListView
        {
            SelectionMode = isSideDrawerMode ? ListViewSelectionMode.None : ListViewSelectionMode.Single,
            IsItemClickEnabled = isSideDrawerMode,
            Padding = new Thickness(0, 0, 2, 0)
        };
        var navBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(isSideDrawerMode ? 0 : 1),
            BorderBrush = LookupBrush("DividerStrokeColorDefaultBrush"),
            Background = isSideDrawerMode ? null : LookupBrush("CardBackgroundFillColorDefaultBrush"),
            Padding = isSideDrawerMode ? new Thickness(0) : new Thickness(4),
            Child = navListView
        };
        bodyGrid.Children.Add(navBorder);

        var detailStack = new StackPanel { Spacing = 12, Padding = new Thickness(isSideDrawerMode ? 2 : 14) };
        var detailScroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = detailStack
        };
        var detailBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(isSideDrawerMode ? 0 : 1),
            BorderBrush = LookupBrush("DividerStrokeColorDefaultBrush"),
            Background = isSideDrawerMode ? null : LookupBrush("CardBackgroundFillColorDefaultBrush"),
            Child = detailScroller
        };
        Grid.SetColumn(detailBorder, isSideDrawerMode ? 0 : 1);
        bodyGrid.Children.Add(detailBorder);

        Border CreateScopedCodeBadge(string text) => new()
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = LookupBrush("SubtleFillColorSecondaryBrush"),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
                Foreground = LookupBrush("AccentTextFillColorPrimaryBrush")
            }
        };

        Button CreateCopyableTokenChip(string token)
        {
            var label = new TextBlock
            {
                Text = token,
                FontSize = 11.5,
                FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace")
            };
            var chip = new Button
            {
                Height = 26,
                MinHeight = 26,
                Padding = new Thickness(8, 0, 8, 0),
                CornerRadius = new CornerRadius(5),
                Content = label
            };
            ToolTipService.SetToolTip(chip, T("Common.Copy") + ": " + token);
            chip.Click += async (_, _) =>
            {
                McpQuickConnectPanelBuilder.CopyTextToClipboard(token);
                label.Text = "✓ " + token;
                await Task.Delay(1100);
                if (chip.IsLoaded)
                {
                    label.Text = token;
                }
            };
            return chip;
        }

        void RenderNavAndDetail()
        {
            List<UnifiedReferenceEntry> entries = ResolveEntriesForActiveTab();
            if (entries.Count > 0 && !entries.Any(e => string.Equals(e.Id, selectedId, StringComparison.OrdinalIgnoreCase)))
            {
                selectedId = entries[0].Id;
            }

            if (isSideDrawerMode)
            {
                searchBox.Visibility = isDetailOpenInDrawer ? Visibility.Collapsed : Visibility.Visible;
                navBorder.Visibility = isDetailOpenInDrawer ? Visibility.Collapsed : Visibility.Visible;
                detailBorder.Visibility = isDetailOpenInDrawer ? Visibility.Visible : Visibility.Collapsed;
            }

            suppressNavSelection = true;
            try
            {
                navListView.Items.Clear();
                ListViewItem? matchedNavItem = null;
                foreach (UnifiedReferenceEntry entry in entries)
                {
                    bool pinned = !string.IsNullOrWhiteSpace(entry.LinkedCapabilityId) &&
                                  (isCapabilityPinned?.Invoke(entry.LinkedCapabilityId!) ?? false);
                    var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(4, 6, 4, 6) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    row.Children.Add(new FontIcon
                    {
                        Glyph = entry.Glyph,
                        FontSize = 14,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, 2, 0, 0),
                        Foreground = LookupBrush("AccentTextFillColorPrimaryBrush")
                    });

                    var textCol = new StackPanel { Spacing = 2 };
                    textCol.Children.Add(new TextBlock
                    {
                        Text = entry.Title,
                        FontSize = 12.5,
                        FontWeight = FontWeights.SemiBold,
                        MaxLines = 1,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
                    textCol.Children.Add(new TextBlock
                    {
                        Text = entry.Summary,
                        FontSize = 11,
                        Foreground = LookupBrush("TextFillColorSecondaryBrush"),
                        MaxLines = isSideDrawerMode ? 2 : 1,
                        TextWrapping = isSideDrawerMode ? TextWrapping.Wrap : TextWrapping.NoWrap,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
                    Grid.SetColumn(textCol, 1);
                    row.Children.Add(textCol);

                    if (pinned)
                    {
                        var pinIcon = new FontIcon
                        {
                            Glyph = "\uE73E",
                            FontSize = 11,
                            Foreground = LookupBrush("AccentTextFillColorPrimaryBrush"),
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        Grid.SetColumn(pinIcon, 2);
                        row.Children.Add(pinIcon);
                    }

                    var lvi = new ListViewItem
                    {
                        Tag = entry.Id,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Content = row
                    };
                    navListView.Items.Add(lvi);
                    if (string.Equals(entry.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedNavItem = lvi;
                    }
                }

                if (!isSideDrawerMode && matchedNavItem is not null)
                {
                    navListView.SelectedItem = matchedNavItem;
                }
            }
            finally
            {
                suppressNavSelection = false;
            }

            detailStack.Children.Clear();
            UnifiedReferenceEntry? selected = entries.FirstOrDefault(e => string.Equals(e.Id, selectedId, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                detailStack.Children.Add(new TextBlock
                {
                    Text = T("CustomWidget.ApiDocs.EmptySearch"),
                    Foreground = LookupBrush("TextFillColorSecondaryBrush")
                });
                return;
            }

            if (isSideDrawerMode)
            {
                var backBtn = new Button
                {
                    Height = 28,
                    Padding = new Thickness(8, 0, 10, 0),
                    CornerRadius = new CornerRadius(6),
                    Content = CreateIconText("\uE72B", T("CustomWidget.ApiDocs.BackToList"))
                };
                backBtn.Click += (_, _) =>
                {
                    isDetailOpenInDrawer = false;
                    RenderNavAndDetail();
                };
                detailStack.Children.Add(backBtn);
            }

            var headerWrap = new CustomWidgetWrapPanel { HorizontalSpacing = 8, VerticalSpacing = 6 };
            headerWrap.Children.Add(new FontIcon { Glyph = selected.Glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
            headerWrap.Children.Add(new TextBlock
            {
                Text = selected.Title,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            if (!string.IsNullOrWhiteSpace(selected.CodeBadge))
            {
                headerWrap.Children.Add(CreateScopedCodeBadge(selected.CodeBadge!));
            }
            if (!string.IsNullOrWhiteSpace(selected.GroupLabel))
            {
                headerWrap.Children.Add(CreateScopedCodeBadge(selected.GroupLabel));
            }
            detailStack.Children.Add(headerWrap);

            detailStack.Children.Add(new TextBlock
            {
                Text = selected.Summary,
                FontSize = 12.5,
                LineHeight = 19,
                Foreground = LookupBrush("TextFillColorSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap
            });

            if (!string.IsNullOrWhiteSpace(selected.LinkedCapabilityId) &&
                (onTogglePinCapability is not null || onInsertIntoPrompt is not null))
            {
                string capId = selected.LinkedCapabilityId!;
                var actionWrap = new CustomWidgetWrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
                if (onInsertIntoPrompt is not null)
                {
                    string insertLabel = selected.IsPublicEndpoint
                        ? selected.Title + " · @" + capId
                        : F("CustomWidget.ApiDocs.InsertIdButton", capId);
                    var insertBtn = new Button
                    {
                        Height = 28,
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(10, 0, 10, 0),
                        FontSize = 12,
                        Content = CreateIconText("\uE8A7", insertLabel)
                    };
                    ToolTipService.SetToolTip(insertBtn, T("CustomWidget.ApiDocs.InsertIdTooltip"));
                    insertBtn.Click += (_, _) =>
                    {
                        string payload = selected.IsPublicEndpoint
                            ? BuildEndpointInsertText(selected, capId, culture)
                            : "@" + capId;
                        onInsertIntoPrompt(payload);
                    };
                    actionWrap.Children.Add(insertBtn);
                }

                if (onTogglePinCapability is not null)
                {
                    bool pinned = isCapabilityPinned?.Invoke(capId) ?? false;
                    var pinBtn = new Button
                    {
                        Height = 28,
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(10, 0, 10, 0),
                        FontSize = 12,
                        Style = !pinned && !isSideDrawerMode ? LookupStyle("AccentButtonStyle") : null,
                        Content = CreateIconText(
                            pinned ? "\uE73E" : "\uE710",
                            T(pinned ? "CustomWidget.ApiDocs.PinnedButton" : "CustomWidget.ApiDocs.PinToPromptButton"))
                    };
                    pinBtn.Click += (_, _) =>
                    {
                        onTogglePinCapability(capId);
                        RenderNavAndDetail();
                    };
                    actionWrap.Children.Add(pinBtn);
                }

                detailStack.Children.Add(actionWrap);
            }

            if (selected.RequiredCapabilityIds.Count > 0)
            {
                var depsRow = new CustomWidgetWrapPanel { HorizontalSpacing = 6, VerticalSpacing = 4 };
                depsRow.Children.Add(new TextBlock
                {
                    Text = T("CustomWidget.ApiDocs.AutoDepsLabel"),
                    FontSize = 12,
                    Foreground = LookupBrush("TextFillColorSecondaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                });
                foreach (string depId in selected.RequiredCapabilityIds)
                {
                    string capturedDep = depId;
                    var depBtn = new Button
                    {
                        Height = 24,
                        MinHeight = 24,
                        Padding = new Thickness(8, 0, 8, 0),
                        CornerRadius = new CornerRadius(4),
                        FontSize = 11,
                        Content = "@" + depId
                    };
                    depBtn.Click += (_, _) => NavigateToCapability(capturedDep);
                    depsRow.Children.Add(depBtn);
                }
                detailStack.Children.Add(depsRow);
            }

            if (selected.Members.Count > 0)
            {
                detailStack.Children.Add(new TextBlock
                {
                    Text = T("CustomWidget.ApiDocs.MembersHeader"),
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = LookupBrush("TextFillColorSecondaryBrush")
                });

                var membersList = new StackPanel { Spacing = 8 };
                foreach (CustomWidgetApiMemberDoc member in selected.Members)
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
                        : [member.Name];
                    var chipsWrap = new CustomWidgetWrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
                    foreach (string token in splitTokens)
                    {
                        chipsWrap.Children.Add(CreateCopyableTokenChip(token));
                    }
                    cardRows.Children.Add(chipsWrap);
                    cardRows.Children.Add(new TextBlock
                    {
                        Text = ResolveMemberDocText(selected.LinkedCapabilityId, member, culture),
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

            if (!string.IsNullOrWhiteSpace(selected.SnippetJson))
            {
                string snippetText = CustomWidgetCapabilityRegistry.LocalizeSnippetSample(selected.SnippetJson.Trim(), culture);
                string copySnippetLabel = T("CustomWidget.ApiDocs.CopySnippet");
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
                    McpQuickConnectPanelBuilder.CopyTextToClipboard(snippetText);
                    copySnippetBtn.Content = CreateIconText("\uE73E", T("CustomWidget.Native.Dialog.CopiedButton"));
                    await Task.Delay(1400);
                    if (copySnippetBtn.IsLoaded)
                    {
                        copySnippetBtn.Content = CreateIconText("\uE8C8", copySnippetLabel);
                    }
                };

                var snippetHeader = new Grid { ColumnSpacing = 8 };
                snippetHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                snippetHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                snippetHeader.Children.Add(new TextBlock
                {
                    Text = T("CustomWidget.ApiDocs.GoldenSnippetHeader"),
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                });
                Grid.SetColumn(copySnippetBtn, 1);
                snippetHeader.Children.Add(copySnippetBtn);

                detailStack.Children.Add(new Expander
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
                });
            }

            detailScroller.ChangeView(null, 0, null, disableAnimation: true);
        }

        void NavigateToCapability(string? capabilityId)
        {
            if (!string.IsNullOrWhiteSpace(capabilityId) &&
                CustomWidgetCapabilityRegistry.GetById(capabilityId) is { } target)
            {
                activeTab = ReferenceSectionTab.Capabilities;
                selectedId = target.Id;
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

        selectorBar.SelectionChanged += (_, _) =>
        {
            if (suppressTabSelection || selectorBar.SelectedItem?.Tag is not ReferenceSectionTab nextTab)
            {
                return;
            }
            activeTab = nextTab;
            isDetailOpenInDrawer = false;
            RenderNavAndDetail();
        };

        searchBox.TextChanged += (sender, args) =>
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                searchQuery = sender.Text ?? string.Empty;
                isDetailOpenInDrawer = false;
                RenderNavAndDetail();
            }
        };

        navListView.SelectionChanged += (_, _) =>
        {
            if (!suppressNavSelection && !isSideDrawerMode && navListView.SelectedItem is ListViewItem { Tag: string id })
            {
                selectedId = id;
                RenderNavAndDetail();
            }
        };

        navListView.ItemClick += (_, e) =>
        {
            if (isSideDrawerMode &&
                navListView.ContainerFromItem(e.ClickedItem) is ListViewItem { Tag: string id })
            {
                selectedId = id;
                isDetailOpenInDrawer = true;
                RenderNavAndDetail();
            }
            else if (isSideDrawerMode && e.ClickedItem is ListViewItem { Tag: string directId })
            {
                selectedId = directId;
                isDetailOpenInDrawer = true;
                RenderNavAndDetail();
            }
        };

        RenderNavAndDetail();
        return new ApiReferenceViewHandle(root, NavigateToCapability, RenderNavAndDetail);
    }

    private static string ResolveMemberDocText(string? capabilityId, CustomWidgetApiMemberDoc member, string cultureName)
    {
        string slug = CustomWidgetCapabilityRegistry.SlugifyKey(member.Name);
        if (!string.IsNullOrWhiteSpace(capabilityId))
        {
            string capKey = $"CustomWidget.Cap.{capabilityId}.Member.{slug}.Desc";
            string capResolved = LocalizationService.TranslateForCulture(cultureName, capKey);
            if (capResolved != capKey)
            {
                return capResolved;
            }
        }

        string docKey = $"CustomWidget.ApiDocs.Member.{slug}.Desc";
        string docResolved = LocalizationService.TranslateForCulture(cultureName, docKey);
        if (docResolved != docKey)
        {
            return docResolved;
        }

        return CustomWidgetRemotePromptService.IsChineseCulture(cultureName)
            ? member.DescriptionZh
            : member.DescriptionEn;
    }

    private static string BuildEndpointInsertText(UnifiedReferenceEntry endpoint, string capabilityId, string culture)
    {
        string? primaryUrl = endpoint.Members
            .Select(static m => m.Name)
            .FirstOrDefault(static n => n.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(primaryUrl))
        {
            return "@" + capabilityId;
        }

        return CustomWidgetRemotePromptService.IsChineseCulture(culture)
            ? $"接入「{endpoint.Title}」（{primaryUrl}） @{capabilityId}"
            : $"Integrate {endpoint.Title} ({primaryUrl}) @{capabilityId}";
    }

    private static IReadOnlyList<UnifiedReferenceEntry> BuildCuratedEndpointEntries(string culture)
    {
        static UnifiedReferenceEntry Make(
            string id,
            string glyph,
            string culture,
            CustomWidgetApiMemberDoc[] members,
            string snippet)
        {
            string prefix = $"CustomWidget.ApiDocs.EndpointDocs.{id}";
            string group = LocalizationService.TranslateForCulture(culture, prefix + ".Group");
            string title = LocalizationService.TranslateForCulture(culture, prefix + ".Title");
            string summary = LocalizationService.TranslateForCulture(culture, prefix + ".Summary");
            return new UnifiedReferenceEntry(
                id,
                glyph,
                group == prefix + ".Group" ? string.Empty : group,
                title == prefix + ".Title" ? id : title,
                summary == prefix + ".Summary" ? string.Empty : summary,
                CodeBadge: id,
                RequiredCapabilityIds: [],
                Members: members,
                SnippetJson: snippet,
                LinkedCapabilityId: "http-and-rss-feeds",
                IsPublicEndpoint: true);
        }

        return
        [
            Make("zhihu-daily", "\uE774", culture,
                [new("https://news-at.zhihu.com/api/4/news/latest", "URL", "GET 600000ms · itemsPath: $.stories", "HTTPS GET · itemsPath: $.stories")],
                """{"collections":{"items":{"maxItems":20}},"sources":[{"id":"zhihu","type":"httpJson","url":"https://news-at.zhihu.com/api/4/news/latest","intervalMs":600000,"targetCollection":"items","itemsPath":"$.stories","fieldMap":{"title":"title","url":"url","sub":"hint"}}]}"""),
            Make("v2ex-hot", "\uE8F2", culture,
                [new("https://www.v2ex.com/api/topics/hot.json", "URL", "GET 600000ms · itemsPath: $", "HTTPS GET · itemsPath: $")],
                """{"collections":{"topics":{"maxItems":15}},"sources":[{"id":"v2ex","type":"httpJson","url":"https://www.v2ex.com/api/topics/hot.json","intervalMs":600000,"targetCollection":"topics","itemsPath":"$","fieldMap":{"title":"title","url":"url","replies":"replies"}}]}"""),
            Make("cn-tech-rss", "\uE8A1", culture,
                [
                    new("https://sspai.com/feed", "RSS", "少数派 RSS", "SSPai RSS"),
                    new("https://www.ithome.com/rss/", "RSS", "IT之家 RSS", "ITHome RSS"),
                    new("https://www.ruanyifeng.com/blog/atom.xml", "Atom", "阮一峰周刊 Atom", "Ruanyifeng Weekly Atom")
                ],
                """{"collections":{"articles":{"maxItems":20}},"sources":[{"id":"sspai","type":"rss","url":"https://sspai.com/feed","intervalMs":900000,"targetCollection":"articles","maxItems":15}]}"""),
            Make("hackernews", "\uE774", culture,
                [new("https://hn.algolia.com/api/v1/search?tags=front_page", "URL", "itemsPath: $.hits", "Official Algolia HN front_page JSON")],
                """{"collections":{"stories":{"maxItems":20}},"sources":[{"id":"hn","type":"httpJson","url":"https://hn.algolia.com/api/v1/search?tags=front_page","intervalMs":600000,"targetCollection":"stories","itemsPath":"$.hits","fieldMap":{"title":"title","url":"url","points":"points","author":"author"}}]}"""),
            Make("fx-crypto", "\uE82D", culture,
                [
                    new("https://open.er-api.com/v6/latest/USD", "FX", "$.rates.CNY / $.rates.EUR / $.rates.JPY", "Zero-key USD exchange rates"),
                    new("https://api.coingecko.com/api/v3/simple/price?ids=bitcoin,ethereum&vs_currencies=usd", "Crypto", "$.bitcoin.usd / $.ethereum.usd", "Zero-key BTC & ETH USD spot prices")
                ],
                """{"state":{"cny":"--","jpy":"--","eur":"--"},"sources":[{"id":"fx","type":"httpJson","url":"https://open.er-api.com/v6/latest/USD","intervalMs":1800000,"stateMap":{"cny":"$.rates.CNY","jpy":"$.rates.JPY","eur":"$.rates.EUR"}}]}""")
        ];
    }

    private static string FormatConciseTitle(string rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
        {
            return string.Empty;
        }

        string trimmed = rawTitle.Trim();
        int parenIdx = trimmed.LastIndexOf(" (", StringComparison.Ordinal);
        return parenIdx > 2 && trimmed.EndsWith(')') ? trimmed[..parenIdx].TrimEnd() : trimmed;
    }

    private static StackPanel CreateIconText(string glyph, string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12 });
        sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    private static Style? LookupStyle(string key) =>
        Application.Current?.Resources is { } res && res.TryGetValue(key, out object? val) && val is Style style
            ? style
            : null;
}
