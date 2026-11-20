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
        LocalizationService? loc,
        string? initialProviderId,
        bool expandAdvanced = false,
        bool hideThinkingOption = false)
    {
        string T(string k) => AiProviderConfigFormController.Translate(loc, k);
        string F(string k, params object[] a) => AiProviderConfigFormController.Format(loc, k, a);
        string culture = AiProviderConfigFormController.ResolveCultureName(loc);
        var form = new AiProviderConfigFormController(T, F, () => culture);

        using var activeOpCtsBox = new OpCancellationHolder();
        using var pasteProbeCtsBox = new OpCancellationHolder();
        bool syncingProviderFields = false;
        IReadOnlyList<string>? stagedFetchedModels = null;
        int lastPasswordLength = 0;
        string lastPasswordValue = string.Empty;
        Brush ResolveThemeBrush(string key) => ResolveThemeBrushFor(key, requestedTheme);

        var scrollContent = new StackPanel
        {
            Spacing = 12,
            MinWidth = 436,
            MaxWidth = 468
        };

        scrollContent.Children.Add(new TextBlock
        {
            Text = T("CustomWidget.Studio.KeyDialog.Description"),
            FontSize = 12,
            Foreground = ResolveThemeBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap
        });

        var providerCombo = new ComboBox
        {
            Header = T("CustomWidget.AiConfig.ProviderHeader"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        string selectedInitialId = form.PopulateProviderCombo(providerCombo, initialProviderId);
        CustomWidgetAiProviderPreset initialPreset = CustomWidgetAiGeneratorService.FindProviderPreset(selectedInitialId);
        scrollContent.Children.Add(providerCombo);

        var keySection = new StackPanel { Spacing = 4 };
        var keyHeaderRow = new Grid { ColumnSpacing = 8 };
        keyHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        keyHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var keyHeaderLabel = new TextBlock
        {
            Text = T("CustomWidget.AiConfig.ApiKeyHeader"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        keyHeaderRow.Children.Add(keyHeaderLabel);

        var getKeyLink = new HyperlinkButton
        {
            Padding = new Thickness(6, 2, 6, 2),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Content = T("CustomWidget.AiConfig.GetApiKeyLink")
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
        AutomationProperties.SetName(keyBox, T("CustomWidget.AiConfig.ApiKeyHeader"));
        keySection.Children.Add(keyBox);

        var localNoKeyBanner = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            Background = ResolveThemeBrush("SubtleFillColorSecondaryBrush"),
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = T("CustomWidget.AiConfig.LocalNoKeyHint"),
                FontSize = 12,
                Foreground = ResolveThemeBrush("TextFillColorSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap
            }
        };
        keySection.Children.Add(localNoKeyBanner);
        scrollContent.Children.Add(keySection);

        var modelSection = new StackPanel { Spacing = 6 };
        modelSection.Children.Add(new TextBlock
        {
            Text = T("CustomWidget.Studio.KeyDialog.ModelHeader"),
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
        AutomationProperties.SetName(modelCombo, T("CustomWidget.Studio.KeyDialog.ModelHeader"));
        modelRow.Children.Add(modelCombo);

        var fetchModelsBtn = new Button
        {
            Height = 32,
            Padding = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = CreateIconTextRow("\uE72C", T("CustomWidget.AiConfig.FetchModels"))
        };
        AutomationProperties.SetName(fetchModelsBtn, T("CustomWidget.AiConfig.FetchModels"));
        Grid.SetColumn(fetchModelsBtn, 1);
        modelRow.Children.Add(fetchModelsBtn);
        modelSection.Children.Add(modelRow);
        scrollContent.Children.Add(modelSection);

        var testRow = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 2, 0, 0) };
        testRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        testRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var testBtn = new Button
        {
            Height = 34,
            Padding = new Thickness(14, 0, 14, 0),
            CornerRadius = new CornerRadius(6),
            Content = CreateIconTextRow("\uE945", T("CustomWidget.AiConfig.TestConnection"))
        };
        AutomationProperties.SetName(testBtn, T("CustomWidget.AiConfig.TestConnection"));
        testRow.Children.Add(testBtn);

        var testHintText = new TextBlock
        {
            Text = T("CustomWidget.AiConfig.TestHint"),
            FontSize = 11.5,
            Foreground = ResolveThemeBrush("TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(testHintText, 1);
        testRow.Children.Add(testHintText);
        scrollContent.Children.Add(testRow);

        var statusInfoBar = new InfoBar { IsClosable = true, IsOpen = false };
        scrollContent.Children.Add(statusInfoBar);

        var advancedStack = new StackPanel { Spacing = 10 };
        var thinkingCombo = new ComboBox
        {
            Header = T("CustomWidget.AiConfig.ThinkingHeader"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        if (!hideThinkingOption)
        {
            advancedStack.Children.Add(thinkingCombo);
            advancedStack.Children.Add(new TextBlock
            {
                Text = T("CustomWidget.AiConfig.ThinkingHint"),
                FontSize = 11.5,
                Foreground = ResolveThemeBrush("TextFillColorSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, -4, 0, 0)
            });
        }

        var contextWindowCombo = new ComboBox
        {
            Header = T("CustomWidget.AiConfig.ContextWindowHeader"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var contextWindowHintText = new TextBlock
        {
            Text = T("CustomWidget.AiConfig.ContextWindowDesc"),
            FontSize = 11.5,
            Foreground = ResolveThemeBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, -4, 0, 0)
        };
        advancedStack.Children.Add(contextWindowCombo);
        advancedStack.Children.Add(contextWindowHintText);

        var endpointBox = new TextBox
        {
            Header = T("CustomWidget.AiConfig.EndpointOptionalHeader"),
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
                Text = T("CustomWidget.AiConfig.AdvancedExpanderHeader"),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold
            },
            Content = advancedStack
        };
        scrollContent.Children.Add(advancedExpander);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = requestedTheme,
            FlowDirection = CustomWidgetLocaleCatalog.IsRightToLeftCulture(culture)
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight,
            Title = T("CustomWidget.AiConfig.DialogTitle"),
            Content = new ScrollViewer
            {
                MaxHeight = 520,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = scrollContent
            },
            PrimaryButtonText = T("Common.Save"),
            CloseButtonText = T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (Application.Current?.Resources is { } res &&
            res.TryGetValue("DefaultContentDialogStyle", out object? styleObj) &&
            styleObj is Style dialogStyle)
        {
            dialog.Style = dialogStyle;
        }

        void ShowDialogStatus(InfoBarSeverity severity, string message)
        {
            statusInfoBar.Severity = severity;
            statusInfoBar.Title = string.Empty;
            statusInfoBar.Message = message;
            statusInfoBar.IsOpen = true;
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
            endpointResolvedText.Text = F("CustomWidget.AiConfig.ResolvedUrl", normalized);
            endpointResolvedText.Visibility = string.Equals(rawUrl.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Collapsed
                : Visibility.Visible;
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
                stagedFetchedModels = existing?.FetchedModels;
                bool isCustomProvider = string.Equals(preset.Id, CustomWidgetAiGeneratorService.CustomProviderId, StringComparison.OrdinalIgnoreCase);

                keyBox.Password = existing?.ApiKey ?? string.Empty;
                lastPasswordValue = keyBox.Password.Trim();
                lastPasswordLength = lastPasswordValue.Length;
                keyBox.Visibility = preset.RequiresApiKey ? Visibility.Visible : Visibility.Collapsed;
                localNoKeyBanner.Visibility = preset.RequiresApiKey ? Visibility.Collapsed : Visibility.Visible;
                keyHeaderLabel.Text = T(preset.RequiresApiKey ? "CustomWidget.AiConfig.ApiKeyHeader" : "CustomWidget.AiConfig.LocalEngineHeader");

                if (!string.IsNullOrWhiteSpace(preset.ConsoleUrl) &&
                    Uri.TryCreate(preset.ConsoleUrl, UriKind.Absolute, out Uri? consoleUri))
                {
                    getKeyLink.NavigateUri = consoleUri;
                    getKeyLink.Content = T(preset.RequiresApiKey ? "CustomWidget.AiConfig.GetApiKeyLink" : "CustomWidget.AiConfig.LocalDocsLink");
                    getKeyLink.Visibility = Visibility.Visible;
                }
                else
                {
                    getKeyLink.Visibility = Visibility.Collapsed;
                }

                ResolvedAiInvocation resolved = DeskBoxAiService.Shared.ResolveEffectiveSettings(preset.Id);
                IReadOnlyList<string> selectableModels = DeskBoxAiService.Shared.GetSelectableModels(preset.Id, stagedFetchedModels);
                AiProviderConfigFormController.PopulateModelCombo(modelCombo, preset, selectableModels, resolved.Model);
                form.PopulateThinkingCombo(thinkingCombo, resolved.ThinkingMode, detailedLabels: true);

                AiContextWindowMode contextMode = DeskBoxAiService.ParseContextWindowMode(existing?.ContextWindowMode);
                form.PopulateContextWindowCombo(contextWindowCombo, contextMode);
                contextWindowHintText.Text = form.BuildContextWindowDescription(resolved);

                endpointBox.Header = T(isCustomProvider ? "CustomWidget.Studio.KeyDialog.EndpointHeader" : "CustomWidget.AiConfig.EndpointOptionalHeader");
                endpointBox.PlaceholderText = form.ResolveEndpointPlaceholder(preset, existing);
                endpointBox.Text = existing?.CustomEndpoint ?? string.Empty;
                UpdateEndpointPreview();

                advancedExpander.IsExpanded = expandAdvanced ||
                                              isCustomProvider ||
                                              !string.IsNullOrWhiteSpace(existing?.CustomEndpoint) ||
                                              (!hideThinkingOption && resolved.ThinkingMode != AiThinkingMode.Auto) ||
                                              contextMode != AiContextWindowMode.Auto;

                bool isConfigured = DeskBoxAiService.IsProviderConfigured(preset, existing);
                dialog.PrimaryButtonText = T(preset.RequiresApiKey || isConfigured ? "Common.Save" : "CustomWidget.AiConfig.EnableLocalButton");
                dialog.SecondaryButtonText = isConfigured
                    ? T(preset.RequiresApiKey ? "CustomWidget.Studio.KeyDialog.ClearButton" : "CustomWidget.AiConfig.DisableLocalButton")
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
            if (!syncingProviderFields)
            {
                LoadProviderIntoForm(form.ResolveSelectedProviderId(providerCombo));
            }
        };

        modelCombo.SelectionChanged += (_, _) =>
        {
            if (syncingProviderFields || modelCombo.SelectedItem is not string selectedModel)
            {
                return;
            }

            CustomWidgetAiProviderPreset preset = CustomWidgetAiGeneratorService.FindProviderPreset(form.ResolveSelectedProviderId(providerCombo));
            IReadOnlyList<string> availableModels = stagedFetchedModels ?? DeskBoxAiService.Shared.GetSelectableModels(preset.Id);
            (_, string syncedThinkingWire) = DeskBoxAiService.SynchronizeModelAndThinkingSelection(
                preset,
                existingCustomModel: null,
                existingThinkingWire: AiProviderConfigFormController.ReadThinkingWire(thinkingCombo),
                newChosenModel: selectedModel,
                newThinkingWire: null,
                availableModels: availableModels);
            syncingProviderFields = true;
            try
            {
                modelCombo.Text = selectedModel;
                thinkingCombo.SelectedIndex = DeskBoxAiService.ParseThinkingMode(syncedThinkingWire) switch
                {
                    AiThinkingMode.Enabled => 1,
                    AiThinkingMode.Disabled => 2,
                    _ => 0
                };
            }
            finally
            {
                syncingProviderFields = false;
            }
        };

        thinkingCombo.SelectionChanged += (_, _) =>
        {
            if (syncingProviderFields || (thinkingCombo.SelectedItem as ComboBoxItem)?.Tag is not string selectedWire)
            {
                return;
            }

            CustomWidgetAiProviderPreset preset = CustomWidgetAiGeneratorService.FindProviderPreset(form.ResolveSelectedProviderId(providerCombo));
            IReadOnlyList<string> availableModels = stagedFetchedModels ?? DeskBoxAiService.Shared.GetSelectableModels(preset.Id);
            (string syncedCustomModel, _) = DeskBoxAiService.SynchronizeModelAndThinkingSelection(
                preset,
                existingCustomModel: AiProviderConfigFormController.ReadModelComboText(modelCombo, preset, stagedFetchedModels),
                existingThinkingWire: null,
                newChosenModel: null,
                newThinkingWire: selectedWire,
                availableModels: availableModels);
            string targetModel = string.IsNullOrWhiteSpace(syncedCustomModel)
                ? (availableModels.FirstOrDefault() ?? preset.DefaultModel)
                : syncedCustomModel;
            syncingProviderFields = true;
            try
            {
                AiProviderConfigFormController.PopulateModelCombo(modelCombo, preset, availableModels, targetModel);
            }
            finally
            {
                syncingProviderFields = false;
            }
        };

        endpointBox.TextChanged += (_, _) =>
        {
            if (!syncingProviderFields)
            {
                stagedFetchedModels = null;
            }
            UpdateEndpointPreview();
        };

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

            string providerId = form.ResolveSelectedProviderId(providerCombo);
            CustomWidgetAiProviderPreset preset = CustomWidgetAiGeneratorService.FindProviderPreset(providerId);
            if (isAutoPasteProbe &&
                string.Equals(preset.Id, CustomWidgetAiGeneratorService.CustomProviderId, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(endpointBox.Text))
            {
                return;
            }
            if (preset.RequiresApiKey && string.IsNullOrWhiteSpace(keyBox.Password))
            {
                if (!isAutoPasteProbe)
                {
                    ShowDialogStatus(InfoBarSeverity.Warning, T("CustomWidget.AiApi.Error.EnterApiKeyFirst"));
                }
                return;
            }

            string chosenModel = AiProviderConfigFormController.ReadModelComboText(modelCombo, preset, stagedFetchedModels);
            AiThinkingMode chosenThinking = hideThinkingOption
                ? DeskBoxAiService.ParseThinkingMode(CustomWidgetAiCredentialStore.Shared.Get(preset.Id)?.ThinkingMode)
                : CustomWidgetAiGeneratorService.ParseThinkingMode(AiProviderConfigFormController.ReadThinkingWire(thinkingCombo));

            CancellationToken token = activeOpCtsBox.Reset();
            isTestingConnection = true;
            testBtn.Content = CreateSpinnerRow(T("Common.Cancel"));
            ShowDialogStatus(InfoBarSeverity.Informational, T("CustomWidget.AiConfig.TestingInProgress"));

            try
            {
                AiConnectionTestResult testResult = await CustomWidgetAiGeneratorService.Shared.TestConnectionAsync(
                    providerId,
                    endpointOverride: endpointBox.Text,
                    apiKeyOverride: keyBox.Password,
                    modelOverride: chosenModel,
                    thinkingOverride: chosenThinking,
                    cancellationToken: token);

                if (isDialogClosed || token.IsCancellationRequested)
                {
                    if (!isDialogClosed)
                    {
                        statusInfoBar.IsOpen = false;
                    }
                    return;
                }

                if (testResult.Success)
                {
                    ShowDialogStatus(InfoBarSeverity.Success, form.FormatTestSuccess(testResult));
                }
                else
                {
                    ShowDialogStatus(InfoBarSeverity.Error, form.FormatTestFailure(testResult));
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
                    ShowDialogStatus(InfoBarSeverity.Error, ex.Message);
                }
            }
            finally
            {
                isTestingConnection = false;
                if (!isDialogClosed)
                {
                    testBtn.Content = CreateIconTextRow("\uE945", T("CustomWidget.AiConfig.TestConnection"));
                }
            }
        }

        keyBox.PasswordChanged += async (_, _) =>
        {
            if (syncingProviderFields || isDialogClosed)
            {
                return;
            }

            int prevLen = lastPasswordLength;
            string prevVal = lastPasswordValue;
            string newVal = keyBox.Password?.Trim() ?? string.Empty;
            int newLen = newVal.Length;
            lastPasswordLength = newLen;
            lastPasswordValue = newVal;

            bool isBulkPaste = newLen >= 18 &&
                               !string.Equals(prevVal, newVal, StringComparison.Ordinal) &&
                               (prevLen == 0 ||
                                newLen - prevLen >= 8 ||
                                (prevLen >= 10 && !newVal.StartsWith(prevVal[..Math.Min(prevVal.Length, Math.Max(1, newLen - 2))], StringComparison.Ordinal)));
            if (isBulkPaste)
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

            string providerId = form.ResolveSelectedProviderId(providerCombo);
            CustomWidgetAiProviderPreset preset = CustomWidgetAiGeneratorService.FindProviderPreset(providerId);
            CancellationToken token = activeOpCtsBox.Reset();
            isFetchingModels = true;
            fetchModelsBtn.Content = CreateSpinnerRow(T("Common.Cancel"));
            statusInfoBar.IsOpen = false;

            try
            {
                AiModelListResult listResult = await CustomWidgetAiGeneratorService.Shared.FetchAvailableModelsAsync(
                    providerId,
                    endpointOverride: endpointBox.Text,
                    apiKeyOverride: keyBox.Password,
                    cancellationToken: token,
                    persistDiscoveredModels: false);
                if (token.IsCancellationRequested || isDialogClosed)
                {
                    return;
                }

                if (listResult.Success && listResult.Models.Count > 0)
                {
                    stagedFetchedModels = CustomWidgetAiCredentialStore.NormalizeModelCatalog(listResult.Models) ?? listResult.Models;
                    string currentModel = AiProviderConfigFormController.ReadModelComboText(modelCombo, preset, stagedFetchedModels);
                    string chosen = stagedFetchedModels.FirstOrDefault(
                        m => string.Equals(m, currentModel, StringComparison.OrdinalIgnoreCase)) ?? stagedFetchedModels[0];
                    syncingProviderFields = true;
                    try
                    {
                        AiProviderConfigFormController.PopulateModelCombo(modelCombo, preset, stagedFetchedModels, chosen);
                    }
                    finally
                    {
                        syncingProviderFields = false;
                    }
                    if (!isDialogClosed && modelCombo.IsLoaded && modelCombo.XamlRoot is not null)
                    {
                        modelCombo.IsDropDownOpen = true;
                    }
                    ShowDialogStatus(InfoBarSeverity.Success, form.FormatFetchSuccess(stagedFetchedModels.Count));
                }
                else
                {
                    ShowDialogStatus(InfoBarSeverity.Warning, form.FormatFetchFailure(listResult));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (!isDialogClosed)
                {
                    ShowDialogStatus(InfoBarSeverity.Warning, ex.Message);
                }
            }
            finally
            {
                isFetchingModels = false;
                if (!isDialogClosed)
                {
                    fetchModelsBtn.Content = CreateIconTextRow("\uE72C", T("CustomWidget.AiConfig.FetchModels"));
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
        string finalProviderId = form.ResolveSelectedProviderId(providerCombo);
        CustomWidgetAiProviderPreset finalPreset = CustomWidgetAiGeneratorService.FindProviderPreset(finalProviderId);

        if (result == ContentDialogResult.Primary)
        {
            string rawThinkingWire = hideThinkingOption
                ? (CustomWidgetAiCredentialStore.Shared.Get(finalPreset.Id)?.ThinkingMode ?? "auto")
                : AiProviderConfigFormController.ReadThinkingWire(thinkingCombo);
            form.SaveProviderSnapshot(
                finalPreset.Id,
                keyBox.Password,
                endpointBox.Text,
                AiProviderConfigFormController.ReadModelComboText(modelCombo, finalPreset, stagedFetchedModels),
                rawThinkingWire,
                AiProviderConfigFormController.ReadContextWindowWire(contextWindowCombo),
                stagedFetchedModels);
            return finalPreset.Id;
        }

        if (result == ContentDialogResult.Secondary)
        {
            AiProviderConfigFormController.ClearOrDisableProvider(finalPreset.Id);
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
        ?? (Application.Current?.Resources is { } res && res.TryGetValue(key, out object? value) && value is Brush brush
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
