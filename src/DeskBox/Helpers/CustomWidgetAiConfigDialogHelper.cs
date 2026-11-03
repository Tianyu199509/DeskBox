using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Helpers;

/// <summary>
/// Shared Windows 11 Fluent API configuration, model selector, thinking-mode switch,
/// and live connectivity tester dialog used by both Settings and the Widget Workshop window.
/// </summary>
public static class CustomWidgetAiConfigDialogHelper
{
    public static async Task<string?> ShowAsync(
        XamlRoot xamlRoot,
        ElementTheme requestedTheme,
        LocalizationService loc,
        string? initialProviderId,
        bool expandAdvanced = false)
    {
        IReadOnlyList<CustomWidgetAiProviderPreset> providers =
            DeskBoxAiService.GetOrderedProvidersForCulture(loc.CurrentCultureName);
        string effectiveInitialId = string.IsNullOrWhiteSpace(initialProviderId)
            ? DeskBoxAiService.GetDefaultStarterProviderId(loc.CurrentCultureName)
            : initialProviderId;
        CustomWidgetAiProviderPreset initialPreset = CustomWidgetAiGeneratorService.FindProviderPreset(effectiveInitialId);

        using var activeOpCtsBox = new OpCancellationHolder();
        using var pasteProbeCtsBox = new OpCancellationHolder();
        bool syncingProviderFields = false;
        int lastPasswordLength = 0;
        Brush ResolveThemeBrush(string key) => ResolveThemeBrushFor(key, requestedTheme);

        var scrollContent = new StackPanel
        {
            Spacing = 12,
            MinWidth = 436,
            MaxWidth = 468
        };

        scrollContent.Children.Add(new TextBlock
        {
            Text = loc.T("CustomWidget.Studio.KeyDialog.Description"),
            FontSize = 12,
            Foreground = ResolveThemeBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap
        });

        // 1. Provider ComboBox (ordered for user's region/language)
        var providerCombo = new ComboBox
        {
            Header = loc.T("CustomWidget.AiConfig.ProviderHeader"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        IReadOnlyDictionary<string, AiProviderCredentialEntry> allSaved = CustomWidgetAiCredentialStore.Shared.GetAll();
        int initialIndex = 0;
        for (int i = 0; i < providers.Count; i++)
        {
            allSaved.TryGetValue(providers[i].Id, out AiProviderCredentialEntry? savedEntry);
            bool isConfigured = DeskBoxAiService.IsProviderConfigured(providers[i], savedEntry);
            string display = isConfigured
                ? $"{providers[i].DisplayName}  ✓"
                : providers[i].DisplayName;
            providerCombo.Items.Add(new ComboBoxItem
            {
                Content = display,
                Tag = providers[i].Id
            });
            if (string.Equals(providers[i].Id, initialPreset.Id, StringComparison.OrdinalIgnoreCase))
            {
                initialIndex = i;
            }
        }
        providerCombo.SelectedIndex = initialIndex;
        scrollContent.Children.Add(providerCombo);

        // 2. API Key Section (with 1-click official Get API Key link, or keyless local hint)
        var keySection = new StackPanel { Spacing = 4 };
        var keyHeaderRow = new Grid { ColumnSpacing = 8 };
        keyHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        keyHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var keyHeaderLabel = new TextBlock
        {
            Text = loc.T("CustomWidget.AiConfig.ApiKeyHeader"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        keyHeaderRow.Children.Add(keyHeaderLabel);

        var getKeyLink = new HyperlinkButton
        {
            Padding = new Thickness(6, 2, 6, 2),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Content = loc.T("CustomWidget.AiConfig.GetApiKeyLink")
        };
        Grid.SetColumn(getKeyLink, 1);
        keyHeaderRow.Children.Add(getKeyLink);
        keySection.Children.Add(keyHeaderRow);

        var keyBox = new PasswordBox
        {
            PlaceholderText = "sk-…",
            PasswordRevealMode = PasswordRevealMode.Peek,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FlowDirection = FlowDirection.LeftToRight
        };
        AutomationProperties.SetName(keyBox, loc.T("CustomWidget.AiConfig.ApiKeyHeader"));
        keySection.Children.Add(keyBox);

        var localNoKeyBanner = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            Background = ResolveThemeBrush("SubtleFillColorSecondaryBrush"),
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = loc.T("CustomWidget.AiConfig.LocalNoKeyHint"),
                FontSize = 12,
                Foreground = ResolveThemeBrush("TextFillColorSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap
            }
        };
        keySection.Children.Add(localNoKeyBanner);
        scrollContent.Children.Add(keySection);

        // 3. Model ComboBox (Editable) + Fetch Models button
        var modelSection = new StackPanel { Spacing = 6 };
        modelSection.Children.Add(new TextBlock
        {
            Text = loc.T("CustomWidget.Studio.KeyDialog.ModelHeader"),
            FontSize = 13
        });

        var modelRow = new Grid { ColumnSpacing = 8 };
        modelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        modelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var modelCombo = new ComboBox
        {
            IsEditable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = initialPreset.DefaultModel,
            FlowDirection = FlowDirection.LeftToRight
        };
        AutomationProperties.SetName(modelCombo, loc.T("CustomWidget.Studio.KeyDialog.ModelHeader"));
        modelRow.Children.Add(modelCombo);

        var fetchModelsBtn = new Button
        {
            Height = 32,
            Padding = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = CreateIconTextRow("\uE72C", loc.T("CustomWidget.AiConfig.FetchModels"))
        };
        AutomationProperties.SetName(fetchModelsBtn, loc.T("CustomWidget.AiConfig.FetchModels"));
        Grid.SetColumn(fetchModelsBtn, 1);
        modelRow.Children.Add(fetchModelsBtn);
        modelSection.Children.Add(modelRow);
        scrollContent.Children.Add(modelSection);

        // 4. Live Connectivity Test Bar + InfoBar (placed prominently above collapsed Advanced section)
        var testRow = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 2, 0, 0) };
        testRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        testRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var testBtn = new Button
        {
            Height = 34,
            Padding = new Thickness(14, 0, 14, 0),
            CornerRadius = new CornerRadius(6),
            Content = CreateIconTextRow("\uE945", loc.T("CustomWidget.AiConfig.TestConnection"))
        };
        AutomationProperties.SetName(testBtn, loc.T("CustomWidget.AiConfig.TestConnection"));
        testRow.Children.Add(testBtn);

        var testHintText = new TextBlock
        {
            Text = loc.T("CustomWidget.AiConfig.TestHint"),
            FontSize = 11.5,
            Foreground = ResolveThemeBrush("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(testHintText, 1);
        testRow.Children.Add(testHintText);
        scrollContent.Children.Add(testRow);

        var statusInfoBar = new InfoBar
        {
            IsClosable = true,
            IsOpen = false
        };
        scrollContent.Children.Add(statusInfoBar);

        // 5. Progressive Disclosure: Collapsible Advanced Section (Thinking Mode & Custom Endpoint)
        var advancedStack = new StackPanel { Spacing = 10 };

        var thinkingCombo = new ComboBox
        {
            Header = loc.T("CustomWidget.AiConfig.ThinkingHeader"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        thinkingCombo.Items.Add(new ComboBoxItem { Content = loc.T("CustomWidget.AiConfig.ThinkingAuto"), Tag = "auto" });
        thinkingCombo.Items.Add(new ComboBoxItem { Content = loc.T("CustomWidget.AiConfig.ThinkingEnabled"), Tag = "enabled" });
        thinkingCombo.Items.Add(new ComboBoxItem { Content = loc.T("CustomWidget.AiConfig.ThinkingDisabled"), Tag = "disabled" });
        thinkingCombo.SelectedIndex = 0;

        var thinkingHintText = new TextBlock
        {
            Text = loc.T("CustomWidget.AiConfig.ThinkingHint"),
            FontSize = 11.5,
            Foreground = ResolveThemeBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, -4, 0, 0)
        };
        advancedStack.Children.Add(thinkingCombo);
        advancedStack.Children.Add(thinkingHintText);

        var endpointBox = new TextBox
        {
            Header = loc.T("CustomWidget.AiConfig.EndpointOptionalHeader"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FlowDirection = FlowDirection.LeftToRight
        };
        var endpointResolvedText = new TextBlock
        {
            FontSize = 11.5,
            Foreground = ResolveThemeBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, -4, 0, 0)
        };
        advancedStack.Children.Add(endpointBox);
        advancedStack.Children.Add(endpointResolvedText);

        var advancedExpander = new Expander
        {
            IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Header = new TextBlock
            {
                Text = loc.T("CustomWidget.AiConfig.AdvancedExpanderHeader"),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold
            },
            Content = advancedStack
        };
        scrollContent.Children.Add(advancedExpander);

        var scroller = new ScrollViewer
        {
            MaxHeight = 520,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = scrollContent
        };

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = requestedTheme,
            FlowDirection = CustomWidgetLocaleCatalog.IsRightToLeftCulture(loc.CurrentCultureName)
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight,
            Title = loc.T("CustomWidget.AiConfig.DialogTitle"),
            Content = scroller,
            PrimaryButtonText = loc.T("Common.Save"),
            CloseButtonText = loc.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (Application.Current.Resources.TryGetValue("DefaultContentDialogStyle", out object? styleObj) &&
            styleObj is Style dialogStyle)
        {
            dialog.Style = dialogStyle;
        }

        string ResolveSelectedProviderId() =>
            (providerCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? initialPreset.Id;

        string ReadSelectedModelText(CustomWidgetAiProviderPreset preset)
        {
            string typed = modelCombo.Text?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(typed))
            {
                return typed;
            }

            if (modelCombo.SelectedItem is string s && !string.IsNullOrWhiteSpace(s))
            {
                return s.Trim();
            }

            return preset.DefaultModel;
        }

        AiThinkingMode ReadSelectedThinkingMode()
        {
            string? tag = (thinkingCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            return CustomWidgetAiGeneratorService.ParseThinkingMode(tag);
        }

        void UpdateEndpointPreview()
        {
            string rawUrl = endpointBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(rawUrl))
            {
                endpointResolvedText.Visibility = Visibility.Collapsed;
                return;
            }

            string normalized = CustomWidgetAiGeneratorService.NormalizeChatCompletionsEndpoint(rawUrl);
            endpointResolvedText.Text = loc.Format("CustomWidget.AiConfig.ResolvedUrl", normalized);
            endpointResolvedText.Visibility = string.Equals(rawUrl.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        void PopulateModelItems(IReadOnlyList<string> models, string activeModel)
        {
            modelCombo.Items.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(activeModel) && seen.Add(activeModel))
            {
                modelCombo.Items.Add(activeModel);
            }

            foreach (string m in models)
            {
                if (!string.IsNullOrWhiteSpace(m) && seen.Add(m))
                {
                    modelCombo.Items.Add(m);
                }
            }

            modelCombo.SelectedItem = activeModel;
            modelCombo.Text = activeModel;
        }

        void LoadProviderIntoForm(string providerId)
        {
            syncingProviderFields = true;
            try
            {
                activeOpCtsBox.CancelCurrent();
                pasteProbeCtsBox.CancelCurrent();
                statusInfoBar.IsOpen = false;

                CustomWidgetAiProviderPreset preset = CustomWidgetAiGeneratorService.FindProviderPreset(providerId);
                AiProviderCredentialEntry? existing = CustomWidgetAiCredentialStore.Shared.Get(preset.Id);
                bool isCustomProvider = string.Equals(
                    preset.Id,
                    CustomWidgetAiGeneratorService.CustomProviderId,
                    StringComparison.OrdinalIgnoreCase);

                keyBox.Password = existing?.ApiKey ?? string.Empty;
                lastPasswordLength = keyBox.Password.Length;

                keyBox.Visibility = preset.RequiresApiKey ? Visibility.Visible : Visibility.Collapsed;
                localNoKeyBanner.Visibility = preset.RequiresApiKey ? Visibility.Collapsed : Visibility.Visible;
                keyHeaderLabel.Text = preset.RequiresApiKey
                    ? loc.T("CustomWidget.AiConfig.ApiKeyHeader")
                    : loc.T("CustomWidget.AiConfig.LocalEngineHeader");

                if (!string.IsNullOrWhiteSpace(preset.ConsoleUrl) &&
                    Uri.TryCreate(preset.ConsoleUrl, UriKind.Absolute, out Uri? consoleUri))
                {
                    getKeyLink.NavigateUri = consoleUri;
                    getKeyLink.Content = loc.T(preset.RequiresApiKey
                        ? "CustomWidget.AiConfig.GetApiKeyLink"
                        : "CustomWidget.AiConfig.LocalDocsLink");
                    getKeyLink.Visibility = Visibility.Visible;
                }
                else
                {
                    getKeyLink.Visibility = Visibility.Collapsed;
                }

                string savedModel = !string.IsNullOrWhiteSpace(existing?.CustomModel)
                    ? existing!.CustomModel.Trim()
                    : preset.DefaultModel;
                modelCombo.PlaceholderText = preset.DefaultModel;
                PopulateModelItems(preset.Models, savedModel);

                AiThinkingMode mode = CustomWidgetAiGeneratorService.ParseThinkingMode(existing?.ThinkingMode);
                thinkingCombo.SelectedIndex = mode switch
                {
                    AiThinkingMode.Enabled => 1,
                    AiThinkingMode.Disabled => 2,
                    _ => 0
                };

                bool isGlobalDefault = string.Equals(existing?.Region, "global", StringComparison.OrdinalIgnoreCase) ||
                                       (string.IsNullOrWhiteSpace(existing?.Region) &&
                                        !DeskBoxAiService.IsSimplifiedChineseRegion(loc.CurrentCultureName));
                string defaultEndpointPlaceholder = isGlobalDefault && !string.IsNullOrWhiteSpace(preset.GlobalDefaultEndpoint)
                    ? preset.GlobalDefaultEndpoint!
                    : preset.DefaultEndpoint;

                endpointBox.Header = loc.T(isCustomProvider
                    ? "CustomWidget.Studio.KeyDialog.EndpointHeader"
                    : "CustomWidget.AiConfig.EndpointOptionalHeader");
                endpointBox.PlaceholderText = defaultEndpointPlaceholder;
                endpointBox.Text = existing?.CustomEndpoint ?? string.Empty;
                UpdateEndpointPreview();

                advancedExpander.IsExpanded = expandAdvanced ||
                                              isCustomProvider ||
                                              !string.IsNullOrWhiteSpace(existing?.CustomEndpoint) ||
                                              mode != AiThinkingMode.Auto;

                bool isConfigured = DeskBoxAiService.IsProviderConfigured(preset, existing);
                dialog.PrimaryButtonText = loc.T(preset.RequiresApiKey || isConfigured
                    ? "Common.Save"
                    : "CustomWidget.AiConfig.EnableLocalButton");
                dialog.SecondaryButtonText = isConfigured
                    ? loc.T(preset.RequiresApiKey
                        ? "CustomWidget.Studio.KeyDialog.ClearButton"
                        : "CustomWidget.AiConfig.DisableLocalButton")
                    : string.Empty;
            }
            finally
            {
                syncingProviderFields = false;
            }
        }

        LoadProviderIntoForm(initialPreset.Id);

        providerCombo.SelectionChanged += (_, _) =>
        {
            if (syncingProviderFields)
            {
                return;
            }

            LoadProviderIntoForm(ResolveSelectedProviderId());
        };

        endpointBox.TextChanged += (_, _) => UpdateEndpointPreview();

        bool isDialogClosed = false;
        bool isTestingConnection = false;

        async Task RunConnectionTestCoreAsync(bool isAutoPasteProbe)
        {
            if (isDialogClosed)
            {
                return;
            }

            if (isTestingConnection)
            {
                if (!isAutoPasteProbe)
                {
                    activeOpCtsBox.CancelCurrent();
                }
                return;
            }

            string providerId = ResolveSelectedProviderId();
            CustomWidgetAiProviderPreset preset = CustomWidgetAiGeneratorService.FindProviderPreset(providerId);
            if (preset.RequiresApiKey && string.IsNullOrWhiteSpace(keyBox.Password))
            {
                if (!isAutoPasteProbe)
                {
                    statusInfoBar.Severity = InfoBarSeverity.Warning;
                    statusInfoBar.Title = string.Empty;
                    statusInfoBar.Message = loc.T("CustomWidget.AiApi.Error.EnterApiKeyFirst");
                    statusInfoBar.IsOpen = true;
                }
                return;
            }

            string chosenModel = ReadSelectedModelText(preset);
            AiThinkingMode chosenThinking = ReadSelectedThinkingMode();

            CancellationToken token = activeOpCtsBox.Reset();
            isTestingConnection = true;
            testBtn.Content = CreateSpinnerRow(loc.T("Common.Cancel"));
            statusInfoBar.Severity = InfoBarSeverity.Informational;
            statusInfoBar.Title = string.Empty;
            statusInfoBar.Message = loc.T("CustomWidget.AiConfig.TestingInProgress");
            statusInfoBar.IsOpen = true;

            try
            {
                AiConnectionTestResult testResult = await CustomWidgetAiGeneratorService.Shared.TestConnectionAsync(
                    providerId,
                    endpointOverride: endpointBox.Text,
                    apiKeyOverride: keyBox.Password,
                    modelOverride: chosenModel,
                    thinkingOverride: chosenThinking,
                    cancellationToken: token);

                if (isDialogClosed)
                {
                    return;
                }

                if (token.IsCancellationRequested)
                {
                    statusInfoBar.IsOpen = false;
                    return;
                }

                if (testResult.Success)
                {
                    statusInfoBar.Severity = InfoBarSeverity.Success;
                    statusInfoBar.Title = string.Empty;
                    statusInfoBar.Message = loc.Format(
                        "CustomWidget.AiConfig.TestSuccess",
                        testResult.LatencyMs,
                        testResult.EffectiveModel);
                    statusInfoBar.IsOpen = true;
                }
                else
                {
                    statusInfoBar.Severity = InfoBarSeverity.Error;
                    statusInfoBar.Title = string.Empty;
                    statusInfoBar.Message = testResult.ErrorMessage ?? loc.T("CustomWidget.AiConfig.TestFailed");
                    statusInfoBar.IsOpen = true;
                }
            }
            catch (OperationCanceledException)
            {
                if (!isDialogClosed)
                {
                    statusInfoBar.IsOpen = false;
                }
            }
            catch (Exception ex)
            {
                if (!isDialogClosed)
                {
                    statusInfoBar.Severity = InfoBarSeverity.Error;
                    statusInfoBar.Title = string.Empty;
                    statusInfoBar.Message = ex.Message;
                    statusInfoBar.IsOpen = true;
                }
            }
            finally
            {
                isTestingConnection = false;
                if (!isDialogClosed)
                {
                    testBtn.Content = CreateIconTextRow("\uE945", loc.T("CustomWidget.AiConfig.TestConnection"));
                }
            }
        }

        // Auto-probe connectivity right after user pastes an API key
        keyBox.PasswordChanged += async (_, _) =>
        {
            if (syncingProviderFields || isDialogClosed)
            {
                return;
            }

            int prevLen = lastPasswordLength;
            int newLen = keyBox.Password?.Trim().Length ?? 0;
            lastPasswordLength = newLen;

            if (prevLen == 0 && newLen >= 18)
            {
                CancellationToken debounceToken = pasteProbeCtsBox.Reset();
                try
                {
                    await Task.Delay(450, debounceToken);
                    if (!debounceToken.IsCancellationRequested && !isDialogClosed)
                    {
                        await RunConnectionTestCoreAsync(isAutoPasteProbe: true);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }
        };

        bool isFetchingModels = false;
        fetchModelsBtn.Click += async (_, _) =>
        {
            if (isDialogClosed)
            {
                return;
            }

            if (isFetchingModels)
            {
                activeOpCtsBox.CancelCurrent();
                return;
            }

            string providerId = ResolveSelectedProviderId();
            CustomWidgetAiProviderPreset preset = CustomWidgetAiGeneratorService.FindProviderPreset(providerId);
            CancellationToken token = activeOpCtsBox.Reset();
            isFetchingModels = true;
            fetchModelsBtn.Content = CreateSpinnerRow(loc.T("Common.Cancel"));
            statusInfoBar.IsOpen = false;

            try
            {
                AiModelListResult listResult = await CustomWidgetAiGeneratorService.Shared.FetchAvailableModelsAsync(
                    providerId,
                    endpointOverride: endpointBox.Text,
                    apiKeyOverride: keyBox.Password,
                    cancellationToken: token);

                if (token.IsCancellationRequested || isDialogClosed)
                {
                    return;
                }

                if (listResult.Success && listResult.Models.Count > 0)
                {
                    string currentModel = ReadSelectedModelText(preset);
                    string chosen = listResult.Models.Contains(currentModel, StringComparer.OrdinalIgnoreCase)
                        ? currentModel
                        : listResult.Models[0];
                    PopulateModelItems(listResult.Models, chosen);
                    if (!isDialogClosed && modelCombo.IsLoaded && modelCombo.XamlRoot is not null)
                    {
                        modelCombo.IsDropDownOpen = true;
                    }

                    statusInfoBar.Severity = InfoBarSeverity.Success;
                    statusInfoBar.Title = string.Empty;
                    statusInfoBar.Message = loc.Format("CustomWidget.AiConfig.FetchModelsSuccess", listResult.Models.Count);
                    statusInfoBar.IsOpen = true;
                }
                else
                {
                    statusInfoBar.Severity = InfoBarSeverity.Warning;
                    statusInfoBar.Title = string.Empty;
                    statusInfoBar.Message = listResult.ErrorMessage ?? loc.T("CustomWidget.AiConfig.FetchModelsFailed");
                    statusInfoBar.IsOpen = true;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (!isDialogClosed)
                {
                    statusInfoBar.Severity = InfoBarSeverity.Warning;
                    statusInfoBar.Title = string.Empty;
                    statusInfoBar.Message = ex.Message;
                    statusInfoBar.IsOpen = true;
                }
            }
            finally
            {
                isFetchingModels = false;
                if (!isDialogClosed)
                {
                    fetchModelsBtn.Content = CreateIconTextRow("\uE72C", loc.T("CustomWidget.AiConfig.FetchModels"));
                }
            }
        };

        testBtn.Click += async (_, _) => await RunConnectionTestCoreAsync(isAutoPasteProbe: false);

        dialog.Closing += (_, _) =>
        {
            isDialogClosed = true;
            pasteProbeCtsBox.CancelCurrent();
            activeOpCtsBox.CancelCurrent();
        };
        dialog.Closed += (_, _) =>
        {
            isDialogClosed = true;
            pasteProbeCtsBox.CancelCurrent();
            activeOpCtsBox.CancelCurrent();
        };

        ContentDialogResult result = await dialog.ShowAsync();
        string finalProviderId = ResolveSelectedProviderId();
        CustomWidgetAiProviderPreset finalPreset = CustomWidgetAiGeneratorService.FindProviderPreset(finalProviderId);
        AiProviderCredentialEntry? prev = CustomWidgetAiCredentialStore.Shared.Get(finalPreset.Id);
        string defaultRegion = DeskBoxAiService.IsSimplifiedChineseRegion(loc.CurrentCultureName) ? "cn" : "global";

        if (result == ContentDialogResult.Primary)
        {
            string trimmedKey = keyBox.Password.Trim();
            string customEndpoint = endpointBox.Text.Trim();
            string chosenModel = ReadSelectedModelText(finalPreset);
            string customModel = string.Equals(chosenModel, finalPreset.DefaultModel, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : chosenModel;
            string thinkingWire = CustomWidgetAiGeneratorService.ToThinkingWireValue(ReadSelectedThinkingMode());
            bool enabledState = finalPreset.RequiresApiKey ? (prev?.Enabled ?? true) : true;

            CustomWidgetAiCredentialStore.Shared.Upsert(new AiProviderCredentialEntry(
                ProviderId: finalPreset.Id,
                ApiKey: trimmedKey,
                Region: prev?.Region ?? defaultRegion,
                PlanTier: prev?.PlanTier ?? "pro",
                Enabled: enabledState,
                CustomEndpoint: customEndpoint,
                CustomModel: customModel,
                ThinkingMode: thinkingWire));

            if (!finalPreset.RequiresApiKey || !string.IsNullOrWhiteSpace(trimmedKey))
            {
                CustomWidgetStudioPreferencesStore.Shared.SetPreferredAiProviderId(finalPreset.Id);
            }

            return finalPreset.Id;
        }

        if (result == ContentDialogResult.Secondary)
        {
            if (finalPreset.RequiresApiKey)
            {
                CustomWidgetAiCredentialStore.Shared.RemoveKey(finalPreset.Id);
            }
            else if (prev is not null)
            {
                CustomWidgetAiCredentialStore.Shared.Upsert(prev with { Enabled = false, ApiKey = string.Empty });
            }
            return finalPreset.Id;
        }

        return null;
    }

    private static StackPanel CreateIconTextRow(string glyph, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13 });
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private static StackPanel CreateSpinnerRow(string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new ProgressRing { IsActive = true, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private static Brush ResolveThemeBrushFor(string key, ElementTheme requestedTheme) =>
        NeutralInteractionBrush.ResolveThemedResource(key, requestedTheme)
        ?? (Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Colors.Gray));

    private sealed class OpCancellationHolder : IDisposable
    {
        private CancellationTokenSource? _cts;

        public CancellationToken Reset()
        {
            CancelCurrent();
            _cts = new CancellationTokenSource();
            return _cts.Token;
        }

        public void CancelCurrent()
        {
            if (_cts is null)
            {
                return;
            }

            try
            {
                _cts.Cancel();
                _cts.Dispose();
            }
            catch
            {
            }

            _cts = null;
        }

        public void Dispose() => CancelCurrent();
    }
}
