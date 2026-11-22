using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class CustomWidgetNativeRenderer
{
    internal static bool NeedsGlobalAiSetupBanner(CustomWidgetViewModel viewModel) =>
        viewModel.Manifest.Capabilities.Any(static c => string.Equals(c, "ai.run", StringComparison.OrdinalIgnoreCase)) &&
        !viewModel.AiServiceInternal.HasAnyConfiguredProvider();

    private FrameworkElement BuildSecretSetupBanner(
        IReadOnlyList<CustomWidgetSettingDefinition> missingSecrets,
        string? cultureName)
    {
        string bannerCulture = cultureName ?? _viewModel.ActiveCultureName;
        string firstLabel = missingSecrets[0].Label;
        string promptText = missingSecrets.Count == 1
            ? LocalizationService.FormatForCulture(bannerCulture, "CustomWidget.Secret.BannerRequiredFormat", firstLabel)
            : LocalizationService.FormatForCulture(bannerCulture, "CustomWidget.Secret.BannerRequiredCountFormat", missingSecrets.Count);

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var keyIcon = new FontIcon
        {
            Glyph = "\uE8D7",
            FontSize = 13,
            Foreground = ResolveSemanticBrush("warning"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(keyIcon, 0);
        row.Children.Add(keyIcon);

        var tb = new TextBlock
        {
            Text = promptText,
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("default"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(tb, 1);
        row.Children.Add(tb);

        var configBtn = new Button
        {
            Content = LocalizationService.TranslateForCulture(bannerCulture, "CustomWidget.Secret.BannerConfigure"),
            FontSize = CaptionFontSize,
            Padding = new Thickness(10, 3, 10, 3),
            MinHeight = 24,
            VerticalAlignment = VerticalAlignment.Center
        };
        StyleAccentCommitButton(configBtn);
        configBtn.CornerRadius = CornerSmall;
        configBtn.Click += (_, _) =>
        {
            ShowSecretSettingsPopoverCore(configBtn, _viewModel, this);
        };
        Grid.SetColumn(configBtn, 2);
        row.Children.Add(configBtn);

        return new Border
        {
            Margin = new Thickness(10, 8, 10, 0),
            Padding = new Thickness(10, 6, 8, 6),
            CornerRadius = CornerMedium,
            Background = GetOrCreateSolidBrush(
                IsDark ? Color.FromArgb(36, 245, 158, 11) : Color.FromArgb(26, 245, 158, 11)),
            BorderThickness = new Thickness(1),
            BorderBrush = GetOrCreateSolidBrush(
                IsDark ? Color.FromArgb(90, 251, 191, 36) : Color.FromArgb(80, 217, 119, 6)),
            Child = row
        };
    }

    private FrameworkElement BuildGlobalAiSetupBanner(string? cultureName)
    {
        string bannerCulture = cultureName ?? _viewModel.ActiveCultureName;
        string aiHeader = LocalizationService.TranslateForCulture(bannerCulture, "Settings.AiSettings.EntryCard.Header");
        string promptText = LocalizationService.FormatForCulture(
            bannerCulture,
            "CustomWidget.Secret.BannerRequiredFormat",
            aiHeader);

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var aiIcon = new FontIcon
        {
            Glyph = "\uE945",
            FontSize = 13,
            Foreground = ResolveSemanticBrush("warning"),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(aiIcon, 0);
        row.Children.Add(aiIcon);

        var tb = new TextBlock
        {
            Text = promptText,
            FontSize = CaptionFontSize,
            Foreground = ResolveForegroundBrush("default"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(tb, 1);
        row.Children.Add(tb);

        var configBtn = new Button
        {
            Content = LocalizationService.TranslateForCulture(bannerCulture, "CustomWidget.Workshop.Composer.QuickSetupApi"),
            FontSize = CaptionFontSize,
            Padding = new Thickness(10, 3, 10, 3),
            MinHeight = 24,
            VerticalAlignment = VerticalAlignment.Center
        };
        StyleAccentCommitButton(configBtn);
        configBtn.CornerRadius = CornerSmall;
        configBtn.Click += (_, _) =>
        {
            SettingsNavigationRequested?.Invoke("AiSettings");
        };
        Grid.SetColumn(configBtn, 2);
        row.Children.Add(configBtn);

        return new Border
        {
            Padding = new Thickness(10, 6, 8, 6),
            CornerRadius = CornerMedium,
            Background = GetOrCreateSolidBrush(
                IsDark ? Color.FromArgb(36, 245, 158, 11) : Color.FromArgb(26, 245, 158, 11)),
            BorderThickness = new Thickness(1),
            BorderBrush = GetOrCreateSolidBrush(
                IsDark ? Color.FromArgb(90, 251, 191, 36) : Color.FromArgb(80, 217, 119, 6)),
            Child = row
        };
    }

    public static void ShowSecretSettingsPopover(
        FrameworkElement anchor,
        CustomWidgetViewModel viewModel) =>
        ShowSecretSettingsPopoverCore(anchor, viewModel, ownerRenderer: null);

    private static void ShowSecretSettingsPopoverCore(
        FrameworkElement anchor,
        CustomWidgetViewModel viewModel,
        CustomWidgetNativeRenderer? ownerRenderer)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(viewModel);

        IReadOnlyList<CustomWidgetSettingDefinition> secretDefs = viewModel.Manifest
            .ResolveLocalizedSettings(viewModel.ActiveCultureName)
            .Where(static d => string.Equals(d.Type, "secret", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (secretDefs.Count == 0)
        {
            return;
        }

        IntPtr ownerHwnd = DeskBox.Views.WidgetEditorPopoverWindow.ResolveOwnerWindowHandle(anchor);
        string logicalKey = $"custom-secrets:{viewModel.Manifest.Id}";
        if (DeskBox.Views.WidgetEditorPopoverWindow.TryConsumeToggleOrCloseExisting(
                ownerHwnd,
                logicalKey,
                anchor))
        {
            return;
        }

        string culture = viewModel.ActiveCultureName;
        string title = LocalizationService.TranslateForCulture(culture, "CustomWidget.Secret.DialogTitle");
        var popover = new DeskBox.Views.WidgetEditorPopoverWindow(ownerHwnd, title);
        try
        {
            var root = new StackPanel
            {
                Width = 310,
                Spacing = 10
            };

            var headerGrid = new Grid { ColumnSpacing = 8 };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            Button closeBtn = DeskBox.Views.WidgetEditorPopoverWindow.CreateHeaderCloseButton(
                LocalizationService.TranslateForCulture(culture, "Common.Close"),
                () => popover.ClosePopover(restoreOwnerForeground: true),
                anchor);
            Grid.SetColumn(closeBtn, 1);
            headerGrid.Children.Add(closeBtn);
            root.Children.Add(headerGrid);

            root.Children.Add(new TextBlock
            {
                Text = LocalizationService.TranslateForCulture(culture, "CustomWidget.Secret.DialogDpapiNote"),
                FontSize = 12,
                Opacity = 0.78,
                TextWrapping = TextWrapping.Wrap
            });

            var inputs = new List<(string Key, PasswordBox Box, bool HadExisting)>(secretDefs.Count);
            PasswordBox? firstBox = null;
            foreach (CustomWidgetSettingDefinition def in secretDefs)
            {
                viewModel.Secrets.TryGetValue(def.Key, out string? existing);
                bool hasExisting = !string.IsNullOrWhiteSpace(existing);

                var fieldPanel = new StackPanel { Spacing = 4 };
                fieldPanel.Children.Add(new TextBlock
                {
                    Text = def.Label,
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold
                });

                var pwdBox = new PasswordBox
                {
                    PlaceholderText = hasExisting
                        ? LocalizationService.FormatForCulture(culture, "CustomWidget.Secret.FieldConfiguredFormat", CustomWidgetSecretStore.MaskSecret(existing))
                        : LocalizationService.TranslateForCulture(culture, "CustomWidget.Secret.FieldPlaceholder"),
                    CornerRadius = new CornerRadius(6),
                    FlowDirection = FlowDirection.LeftToRight
                };
                firstBox ??= pwdBox;
                fieldPanel.Children.Add(pwdBox);
                root.Children.Add(fieldPanel);
                inputs.Add((def.Key, pwdBox, hasExisting));
            }

            async Task SaveAndCloseAsync()
            {
                popover.ClosePopover(restoreOwnerForeground: true);
                foreach ((string key, PasswordBox box, bool hadExisting) in inputs)
                {
                    string typed = box.Password.Trim();
                    if (!string.IsNullOrEmpty(typed) || !hadExisting)
                    {
                        await viewModel.SetSecretValueAsync(key, typed);
                    }
                }
            }

            foreach ((_, PasswordBox box, _) in inputs)
            {
                box.KeyDown += (_, ke) =>
                {
                    if (ke.Key == Windows.System.VirtualKey.Enter)
                    {
                        ke.Handled = true;
                        FireAndForgetSafeUi(SaveAndCloseAsync);
                    }
                };
            }

            var footerGrid = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 4, 0, 0) };
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (inputs.Any(static x => x.HadExisting))
            {
                var clearBtn = new Button
                {
                    Content = LocalizationService.TranslateForCulture(culture, "CustomWidget.Secret.DialogClearAll"),
                    MinHeight = 30,
                    Padding = new Thickness(10, 0, 10, 0),
                    FontSize = 12
                };
                clearBtn.Click += (_, _) => FireAndForgetSafeUi(async () =>
                {
                    popover.ClosePopover(restoreOwnerForeground: true);
                    foreach ((string key, _, _) in inputs)
                    {
                        await viewModel.SetSecretValueAsync(key, string.Empty);
                    }
                });
                footerGrid.Children.Add(clearBtn);
            }

            var rightPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var cancelBtn = new Button
            {
                Content = LocalizationService.TranslateForCulture(culture, "Common.Cancel"),
                MinHeight = 30,
                Padding = new Thickness(12, 0, 12, 0),
                FontSize = 12
            };
            cancelBtn.Click += (_, _) => popover.ClosePopover(restoreOwnerForeground: true);
            rightPanel.Children.Add(cancelBtn);

            var saveBtn = new Button
            {
                Content = LocalizationService.TranslateForCulture(culture, "Common.Save"),
                MinHeight = 30,
                Padding = new Thickness(14, 0, 14, 0),
                FontSize = 12
            };
            if (Application.Current?.Resources.TryGetValue("AccentButtonStyle", out object? accentObj) == true &&
                accentObj is Style accentStyle)
            {
                saveBtn.Style = accentStyle;
            }
            saveBtn.Click += (_, _) => FireAndForgetSafeUi(SaveAndCloseAsync);
            rightPanel.Children.Add(saveBtn);

            Grid.SetColumn(rightPanel, 2);
            footerGrid.Children.Add(rightPanel);
            root.Children.Add(footerGrid);

            if (ownerRenderer is not null)
            {
                ownerRenderer.ShowTrackedPopoverAt(
                    popover,
                    anchor,
                    root,
                    DeskBox.Views.WidgetPopoverPlacement.Bottom,
                    minWidthDip: 342,
                    maxWidthDip: 356,
                    initialFocusTarget: firstBox,
                    logScope: "CustomWidgetSecrets",
                    logicalKey: logicalKey);
            }
            else
            {
                popover.ShowAnchored(
                    anchor,
                    root,
                    DeskBox.Views.WidgetPopoverPlacement.Bottom,
                    minWidthDip: 342,
                    maxWidthDip: 356,
                    initialFocusTarget: firstBox,
                    triggerElement: anchor,
                    logScope: "CustomWidgetSecrets",
                    logicalKey: logicalKey);
            }
        }
        catch
        {
            try { popover.ClosePopover(restoreOwnerForeground: false); } catch { }
            throw;
        }
    }

    private static bool IsInternationalAiRegion(string? region) =>
        string.Equals(region, "global", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(region, "intl", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(region, "international", StringComparison.OrdinalIgnoreCase);

    private void ShowAiCredentialsFlyout(FrameworkElement anchor, string? initialProviderId)
    {
        IntPtr ownerHwnd = DeskBox.Views.WidgetEditorPopoverWindow.ResolveOwnerWindowHandle(anchor);
        const string logicalKey = "ai-credentials";
        if (DeskBox.Views.WidgetEditorPopoverWindow.TryConsumeToggleOrCloseExisting(ownerHwnd, logicalKey, anchor))
        {
            return;
        }

        string flyoutCulture = _viewModel.ActiveCultureName;
        var catalog = CustomWidgetAiMonitorEngine.GetCatalog(flyoutCulture);

        string activeTabId = string.IsNullOrWhiteSpace(initialProviderId)
            ? CustomWidgetAiMonitorEngine.ProviderClaude
            : initialProviderId;

        string flyoutTitle = Tr("CustomWidget.Renderer.Ai.FlyoutTitle", flyoutCulture);
        var popover = new DeskBox.Views.WidgetEditorPopoverWindow(
            ownerHwnd,
            flyoutTitle);
        try
        {

        var root = new StackPanel
        {
            Width = 308,
            Spacing = 12
        };

        var headerGrid = new Grid { ColumnSpacing = 8 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headerTb = new TextBlock
        {
            Text = flyoutTitle,
            FontSize = SubtitleFontSize,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ResolveForegroundBrush("default")
        };
        headerGrid.Children.Add(headerTb);

        Button closeBtn = DeskBox.Views.WidgetEditorPopoverWindow.CreateHeaderCloseButton(
            Tr("Common.Close", flyoutCulture),
            () => popover.ClosePopover(restoreOwnerForeground: true),
            _themeScope);
        Grid.SetColumn(closeBtn, 1);
        headerGrid.Children.Add(closeBtn);
        root.Children.Add(headerGrid);

        var tabGrid = new Grid
        {
            RowSpacing = 3,
            ColumnSpacing = 3
        };
        for (int c = 0; c < 3; c++)
        {
            tabGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        int tabRowCount = Math.Max(1, (catalog.Count + 2) / 3);
        for (int r = 0; r < tabRowCount; r++)
        {
            tabGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var tabButtons = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
        var bodyHost = new StackPanel { Spacing = 10 };
        PasswordBox? activePasswordBox = null;

        void RebuildProviderBody()
        {
            activePasswordBox = null;
            foreach ((string id, Button btn) in tabButtons)
            {
                ApplySegmentedItemState(btn, string.Equals(id, activeTabId, StringComparison.OrdinalIgnoreCase));
            }

            bodyHost.Children.Clear();
            var meta = catalog.FirstOrDefault(
                c => string.Equals(c.Id, activeTabId, StringComparison.OrdinalIgnoreCase));
            AiProviderCredentialEntry? existing = CustomWidgetAiMonitorSource.Shared.GetEffectiveCredential(activeTabId);

            if (meta.IsLocalCli)
            {
                bool isEnabled = existing?.Enabled ?? true;
                string selectedTier = existing?.PlanTier ?? "pro";
                var localSum = CustomWidgetAiMonitorSource.Shared.Latest.LocalSummary;
                int detectedFiles = activeTabId.Equals(CustomWidgetAiMonitorEngine.ProviderClaude, StringComparison.OrdinalIgnoreCase)
                    ? localSum.DetectedClaudeFiles
                    : activeTabId.Equals(CustomWidgetAiMonitorEngine.ProviderGeminiCli, StringComparison.OrdinalIgnoreCase)
                        ? localSum.DetectedGeminiFiles
                        : localSum.DetectedCodexFiles;

                string folderHint = activeTabId.Equals(CustomWidgetAiMonitorEngine.ProviderClaude, StringComparison.OrdinalIgnoreCase)
                    ? @"~/.claude/projects"
                    : activeTabId.Equals(CustomWidgetAiMonitorEngine.ProviderGeminiCli, StringComparison.OrdinalIgnoreCase)
                        ? @"~/.gemini/tmp"
                        : @"~/.codex/sessions";

                var statusBox = new Border
                {
                    CornerRadius = CornerSmall,
                    Padding = new Thickness(10, 8, 10, 8),
                    Background = ResolveCardBackground("subtle"),
                    BorderBrush = DividerBrush,
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = detectedFiles > 0
                            ? TrFormat("CustomWidget.Renderer.Ai.LocalCliDetectedFormat", flyoutCulture, detectedFiles, folderHint)
                            : TrFormat("CustomWidget.Renderer.Ai.LocalCliMissingFormat", flyoutCulture, folderHint, meta.Name),
                        FontSize = CaptionFontSize,
                        Foreground = ResolveForegroundBrush("secondary"),
                        TextWrapping = TextWrapping.Wrap
                    }
                };
                bodyHost.Children.Add(statusBox);

                if (activeTabId.Equals(CustomWidgetAiMonitorEngine.ProviderClaude, StringComparison.OrdinalIgnoreCase))
                {
                    bodyHost.Children.Add(new TextBlock
                    {
                        Text = Tr("CustomWidget.Renderer.Ai.PlanTierCalibration", flyoutCulture),
                        FontSize = CaptionFontSize,
                        Foreground = ResolveForegroundBrush("muted")
                    });

                    var tierRow = new Grid { ColumnSpacing = 2 };
                    (string Val, string Label)[] tiers =
                    [
                        ("pro", "Pro"),
                        ("max5x", "Max 5x"),
                        ("max20x", "Max 20x")
                    ];
                    var tierBtns = new List<(string Val, Button Btn)>();
                    for (int i = 0; i < tiers.Length; i++)
                    {
                        tierRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        string tv = tiers[i].Val;
                        var tb = new Button
                        {
                            Content = tiers[i].Label,
                            Height = 28,
                            MinHeight = 28,
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            CornerRadius = CornerSmall,
                            FontSize = CaptionFontSize
                        };
                        ApplySegmentedItemState(tb, string.Equals(selectedTier, tv, StringComparison.OrdinalIgnoreCase));
                        tb.Click += (_, _) => FireAndForgetSafeUi(async () =>
                        {
                            selectedTier = tv;
                            foreach (var pair in tierBtns)
                            {
                                ApplySegmentedItemState(pair.Btn, pair.Val == selectedTier);
                            }
                            AiProviderCredentialEntry? latestExisting = CustomWidgetAiMonitorSource.Shared.GetEffectiveCredential(activeTabId);
                            AiProviderCredentialEntry updated = latestExisting is not null
                                ? latestExisting with { PlanTier = selectedTier, Enabled = isEnabled }
                                : new AiProviderCredentialEntry(activeTabId, string.Empty, "cn", selectedTier, isEnabled);
                            await _viewModel.SaveAndTestAiCredentialAsync(updated);
                        });
                        tierBtns.Add((tv, tb));
                        Grid.SetColumn(tb, i);
                        tierRow.Children.Add(tb);
                    }

                    bodyHost.Children.Add(new Border
                    {
                        CornerRadius = CornerMedium,
                        Padding = new Thickness(2),
                        Background = ResolveCardBackground("subtle"),
                        BorderBrush = DividerBrush,
                        BorderThickness = new Thickness(1),
                        Child = tierRow
                    });
                }

                var toggleVisBtn = new Button
                {
                    Content = isEnabled
                        ? Tr("CustomWidget.Renderer.Ai.HideProviderButton", flyoutCulture)
                        : Tr("CustomWidget.Renderer.Ai.ShowProviderButton", flyoutCulture),
                    Height = 30,
                    MinHeight = 30,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    FontSize = CaptionFontSize
                };
                StyleStandardCommandButton(toggleVisBtn, CornerMedium);
                toggleVisBtn.Click += (_, _) => FireAndForgetSafeUi(async () =>
                {
                    isEnabled = !isEnabled;
                    await CustomWidgetAiMonitorSource.Shared.SetProviderMonitorEnabledAsync(activeTabId, isEnabled);
                    RebuildProviderBody();
                });
                bodyHost.Children.Add(toggleVisBtn);
                popover.RefreshBoundsFromContent();
                return;
            }

            string selectedRegion = IsInternationalAiRegion(existing?.Region) ? "global" : "cn";
            bool hasRegionChoice = activeTabId.Equals(CustomWidgetAiMonitorEngine.ProviderGlm, StringComparison.OrdinalIgnoreCase) ||
                                   activeTabId.Equals(CustomWidgetAiMonitorEngine.ProviderMiniMax, StringComparison.OrdinalIgnoreCase) ||
                                   activeTabId.Equals(CustomWidgetAiMonitorEngine.ProviderSiliconFlow, StringComparison.OrdinalIgnoreCase) ||
                                   activeTabId.Equals(CustomWidgetAiMonitorEngine.ProviderKimi, StringComparison.OrdinalIgnoreCase);

            if (hasRegionChoice)
            {
                var regGrid = new Grid { ColumnSpacing = 2 };
                regGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                regGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                (string cnKey, string intlKey) = activeTabId.ToLowerInvariant() switch
                {
                    CustomWidgetAiMonitorEngine.ProviderGlm => ("CustomWidget.Renderer.Ai.RegionGlmCn", "CustomWidget.Renderer.Ai.RegionGlmIntl"),
                    CustomWidgetAiMonitorEngine.ProviderMiniMax => ("CustomWidget.Renderer.Ai.RegionMiniMaxCn", "CustomWidget.Renderer.Ai.RegionMiniMaxIntl"),
                    CustomWidgetAiMonitorEngine.ProviderSiliconFlow => ("CustomWidget.Renderer.Ai.RegionSiliconFlowCn", "CustomWidget.Renderer.Ai.RegionSiliconFlowIntl"),
                    _ => ("CustomWidget.Renderer.Ai.RegionKimiCn", "CustomWidget.Renderer.Ai.RegionKimiIntl")
                };
                string cnLabel = Tr(cnKey, flyoutCulture);
                string intlLabel = Tr(intlKey, flyoutCulture);

                var cnBtn = new Button
                {
                    Content = cnLabel,
                    Height = 28,
                    MinHeight = 28,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    CornerRadius = CornerSmall,
                    FontSize = CaptionFontSize
                };
                var intlBtn = new Button
                {
                    Content = intlLabel,
                    Height = 28,
                    MinHeight = 28,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    CornerRadius = CornerSmall,
                    FontSize = CaptionFontSize
                };
                bool initialIntl = IsInternationalAiRegion(selectedRegion);
                ApplySegmentedItemState(cnBtn, !initialIntl);
                ApplySegmentedItemState(intlBtn, initialIntl);

                cnBtn.Click += (_, _) =>
                {
                    selectedRegion = "cn";
                    ApplySegmentedItemState(cnBtn, true);
                    ApplySegmentedItemState(intlBtn, false);
                };
                intlBtn.Click += (_, _) =>
                {
                    selectedRegion = "global";
                    ApplySegmentedItemState(cnBtn, false);
                    ApplySegmentedItemState(intlBtn, true);
                };
                Grid.SetColumn(cnBtn, 0);
                Grid.SetColumn(intlBtn, 1);
                regGrid.Children.Add(cnBtn);
                regGrid.Children.Add(intlBtn);

                bodyHost.Children.Add(new Border
                {
                    CornerRadius = CornerMedium,
                    Padding = new Thickness(2),
                    Background = ResolveCardBackground("subtle"),
                    BorderBrush = DividerBrush,
                    BorderThickness = new Thickness(1),
                    Child = regGrid
                });
            }

            TextBox? endpointBox = null;
            if (activeTabId.Equals(CustomWidgetAiMonitorEngine.ProviderOneApi, StringComparison.OrdinalIgnoreCase))
            {
                bodyHost.Children.Add(new TextBlock
                {
                    Text = Tr("CustomWidget.Renderer.Ai.GatewayEndpointLabel", flyoutCulture),
                    FontSize = CaptionFontSize,
                    Foreground = ResolveForegroundBrush("muted")
                });
                endpointBox = new TextBox
                {
                    Text = existing?.CustomEndpoint ?? string.Empty,
                    PlaceholderText = "https://api.example.com",
                    MaxLength = 256,
                    CornerRadius = CornerSmall,
                    FontSize = BodyFontSize,
                    FlowDirection = FlowDirection.LeftToRight
                };
                bodyHost.Children.Add(endpointBox);
            }

            string masked = CustomWidgetAiCredentialStore.MaskApiKey(existing?.ApiKey);
            var keyStatusTb = new TextBlock
            {
                Text = string.IsNullOrEmpty(masked)
                    ? TrFormat("CustomWidget.Renderer.Ai.PasteKeyHintFormat", flyoutCulture, meta.Name)
                    : TrFormat("CustomWidget.Renderer.Ai.EncryptedSavedKeyFormat", flyoutCulture, masked),
                FontSize = CaptionFontSize,
                Foreground = ResolveForegroundBrush("muted"),
                TextWrapping = TextWrapping.Wrap
            };
            bodyHost.Children.Add(keyStatusTb);

            var pwdBox = new PasswordBox
            {
                PlaceholderText = string.IsNullOrEmpty(masked)
                    ? "sk-..."
                    : Tr("CustomWidget.Renderer.Ai.OverwriteKeyPlaceholder", flyoutCulture),
                MaxLength = 256,
                CornerRadius = CornerSmall,
                FontSize = BodyFontSize,
                FlowDirection = FlowDirection.LeftToRight
            };
            activePasswordBox = pwdBox;
            bodyHost.Children.Add(pwdBox);

            var feedbackTb = new TextBlock
            {
                FontSize = CaptionFontSize,
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed
            };
            bodyHost.Children.Add(feedbackTb);

            var actionGrid = new Grid { ColumnSpacing = 8 };
            actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (!string.IsNullOrEmpty(masked))
            {
                actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }

            var saveBtn = new Button
            {
                Content = Tr("CustomWidget.Renderer.Ai.VerifyAndSaveButton", flyoutCulture),
                Height = 32,
                MinHeight = 32,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FontSize = CaptionFontSize
            };
            StyleAccentCommitButton(saveBtn);

            string boundTabId = activeTabId;

            async Task CommitApiKeySaveAsync()
            {
                if (!saveBtn.IsEnabled)
                {
                    return;
                }

                string typed = pwdBox.Password.Trim();
                string effectiveKey = !string.IsNullOrEmpty(typed) ? typed : (existing?.ApiKey ?? string.Empty);
                if (string.IsNullOrEmpty(effectiveKey))
                {
                    feedbackTb.Text = Tr("CustomWidget.Renderer.Ai.EmptyKeyError", flyoutCulture);
                    feedbackTb.Foreground = ResolveForegroundBrush("danger");
                    feedbackTb.Visibility = Visibility.Visible;
                    popover.RefreshBoundsFromContent();
                    return;
                }

                saveBtn.IsEnabled = false;
                saveBtn.Content = Tr("CustomWidget.Renderer.Ai.VerifyingButton", flyoutCulture);
                try
                {
                    AiProviderCredentialEntry? latestExisting = CustomWidgetAiMonitorSource.Shared.GetEffectiveCredential(boundTabId);
                    string customEndpoint = endpointBox is not null
                        ? endpointBox.Text.Trim()
                        : (latestExisting?.CustomEndpoint ?? string.Empty);
                    AiProviderCredentialEntry updated = latestExisting is not null
                        ? latestExisting with { ApiKey = effectiveKey, Region = selectedRegion, CustomEndpoint = customEndpoint, Enabled = true }
                        : new AiProviderCredentialEntry(boundTabId, effectiveKey, selectedRegion, "pro", Enabled: true, CustomEndpoint: customEndpoint);
                    var (ok, msg) = await _viewModel.SaveAndTestAiCredentialAsync(updated);
                    if (!string.Equals(activeTabId, boundTabId, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    feedbackTb.Text = msg;
                    feedbackTb.Foreground = ResolveForegroundBrush(ok ? "emerald" : "warning");
                    feedbackTb.Visibility = Visibility.Visible;
                    if (ok)
                    {
                        pwdBox.Password = string.Empty;
                        existing = CustomWidgetAiMonitorSource.Shared.GetEffectiveCredential(boundTabId);
                        RebuildProviderBody();
                        return;
                    }
                }
                finally
                {
                    saveBtn.IsEnabled = true;
                    saveBtn.Content = Tr("CustomWidget.Renderer.Ai.VerifyAndSaveButton", flyoutCulture);
                }

                popover.RefreshBoundsFromContent();
            }

            saveBtn.Click += (_, _) => FireAndForgetSafeUi(CommitApiKeySaveAsync);
            pwdBox.KeyDown += (_, ke) =>
            {
                if (ke.Key == Windows.System.VirtualKey.Enter &&
                    !DeskBox.Views.WidgetEditorPopoverWindow.IsImeCompositionKeyEvent(ke))
                {
                    ke.Handled = true;
                    FireAndForgetSafeUi(CommitApiKeySaveAsync);
                }
            };
            Grid.SetColumn(saveBtn, 0);
            actionGrid.Children.Add(saveBtn);

            if (!string.IsNullOrEmpty(masked))
            {
                var clearBtn = new Button
                {
                    Content = Tr("CustomWidget.Renderer.Ai.ClearButton", flyoutCulture),
                    Height = 32,
                    MinHeight = 32,
                    Padding = new Thickness(12, 0, 12, 0),
                    FontSize = CaptionFontSize
                };
                StyleStandardCommandButton(clearBtn, CornerMedium);
                clearBtn.Click += (_, _) => FireAndForgetSafeUi(async () =>
                {
                    await _viewModel.RemoveAiCredentialAsync(boundTabId);
                    if (string.Equals(activeTabId, boundTabId, StringComparison.OrdinalIgnoreCase))
                    {
                        RebuildProviderBody();
                    }
                });
                Grid.SetColumn(clearBtn, 1);
                actionGrid.Children.Add(clearBtn);
            }

            bodyHost.Children.Add(actionGrid);

            if (!string.IsNullOrEmpty(masked) && existing is not null)
            {
                bool isCloudEnabled = existing.Enabled;
                var toggleCloudVisBtn = new Button
                {
                    Content = isCloudEnabled
                        ? Tr("CustomWidget.Renderer.Ai.HideProviderButton", flyoutCulture)
                        : Tr("CustomWidget.Renderer.Ai.ShowProviderButton", flyoutCulture),
                    Height = 30,
                    MinHeight = 30,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    FontSize = CaptionFontSize
                };
                StyleStandardCommandButton(toggleCloudVisBtn, CornerMedium);
                toggleCloudVisBtn.Click += (_, _) => FireAndForgetSafeUi(async () =>
                {
                    toggleCloudVisBtn.IsEnabled = false;
                    try
                    {
                        await CustomWidgetAiMonitorSource.Shared.SetProviderMonitorEnabledAsync(boundTabId, !isCloudEnabled);
                    }
                    finally
                    {
                        toggleCloudVisBtn.IsEnabled = true;
                    }
                    if (string.Equals(activeTabId, boundTabId, StringComparison.OrdinalIgnoreCase))
                    {
                        RebuildProviderBody();
                    }
                });
                bodyHost.Children.Add(toggleCloudVisBtn);
            }

            popover.RefreshBoundsFromContent();
        }

        for (int i = 0; i < catalog.Count; i++)
        {
            var cat = catalog[i];
            var tabBtn = new Button
            {
                Content = cat.ShortName,
                Height = 28,
                MinHeight = 28,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(4, 0, 4, 0),
                CornerRadius = CornerSmall,
                FontSize = CaptionFontSize
            };
            string cid = cat.Id;
            tabBtn.Click += (_, _) =>
            {
                activeTabId = cid;
                RebuildProviderBody();
                if (activePasswordBox is PasswordBox targetPwdBox)
                {
                    DeskBox.Platform.UiDispatch.PostAfterCallback(() => targetPwdBox.Focus(FocusState.Programmatic));
                }
            };
            tabButtons[cid] = tabBtn;
            Grid.SetRow(tabBtn, i / 3);
            Grid.SetColumn(tabBtn, i % 3);
            tabGrid.Children.Add(tabBtn);
        }

        root.Children.Add(new Border
        {
            CornerRadius = CornerMedium,
            Padding = new Thickness(3),
            Background = ResolveCardBackground("subtle"),
            BorderBrush = DividerBrush,
            BorderThickness = new Thickness(1),
            Child = tabGrid
        });
        root.Children.Add(bodyHost);
        RebuildProviderBody();

        ShowTrackedPopoverAt(
            popover,
            anchor,
            root,
            DeskBox.Views.WidgetPopoverPlacement.Bottom,
            minWidthDip: 326,
            maxWidthDip: 348,
            initialFocusTarget: activePasswordBox,
            logScope: "AiCredentialsEditor",
            logicalKey: logicalKey);
        }
        catch
        {
            try { popover.ClosePopover(restoreOwnerForeground: false); } catch { }
            throw;
        }
    }
}
