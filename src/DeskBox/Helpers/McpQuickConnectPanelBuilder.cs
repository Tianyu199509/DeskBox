using DeskBox.Controls;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace DeskBox.Helpers;

/// <summary>
/// Shared MCP Quick-Connect configuration panel (1-click Cursor/VS Code deep links,
/// auto-setup prompt, client snippet selector, <c>--mcp</c> command line, live pipe self-test,
/// and 8-tool reference flyout) shared between <c>AiSettingsSection</c> and <c>StudioChannelOnboardingController</c>.
/// </summary>
internal static class McpQuickConnectPanelBuilder
{
    private static readonly (McpConfigFormat Format, string LabelKey)[] Formats =
    [
        (McpConfigFormat.StandardJson, "CustomWidget.AgentHub.Format.StandardJson"),
        (McpConfigFormat.VsCodeJson, "CustomWidget.AgentHub.Format.VsCodeJson"),
        (McpConfigFormat.CodexToml, "CustomWidget.AgentHub.Format.CodexToml"),
        (McpConfigFormat.OpenCodeJson, "CustomWidget.AgentHub.Format.OpenCodeJson"),
        (McpConfigFormat.DeepSeekHarnessYaml, "CustomWidget.AgentHub.Format.DeepSeekHarnessYaml"),
        (McpConfigFormat.CliRegistration, "CustomWidget.AgentHub.Format.CliRegistration")
    ];

    internal static void CopyTextToClipboard(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            DeskBoxClipboardWriteScope.MarkText(text);
            Clipboard.SetContent(package);
            try
            {
                Clipboard.Flush();
            }
            catch (Exception ex)
            {
                App.Log($"[McpQuickConnect] Clipboard flush failed: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            App.Log($"[McpQuickConnect] Clipboard copy failed: {ex.Message}");
        }
    }

    internal static async Task FlashCopiedButtonAsync(Button button, object originalContent, string copiedLabel)
    {
        button.Content = "✓ " + copiedLabel;
        await Task.Delay(1600);
        if (button.IsLoaded)
        {
            button.Content = originalContent;
        }
    }

    internal static void PopulatePanel(
        StackPanel container,
        ElementTheme theme,
        Func<string, string> translate,
        Func<string, object[], string> format,
        string cultureName,
        McpConfigFormat initialFormat,
        Action<McpConfigFormat>? onFormatSelected,
        bool includeAutoSetupPromptCard,
        Action<bool, string>? onSelfTestBanner,
        CancellationToken cancellationToken)
    {
        var monoFont = new FontFamily("Cascadia Code, Consolas");

        if (includeAutoSetupPromptCard)
        {
            var autoCard = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12, 14, 12),
                BorderThickness = new Thickness(1),
                Background = ResolveBrush(theme, "CardBackgroundFillColorDefaultBrush", Color.FromArgb(0x14, 0x80, 0x80, 0x80)),
                BorderBrush = ResolveBrush(theme, "CardStrokeColorDefaultBrush", Color.FromArgb(0x28, 0x80, 0x80, 0x80))
            };
            var autoStack = new StackPanel { Spacing = 8 };
            autoStack.Children.Add(new TextBlock
            {
                Text = translate("CustomWidget.Workshop.AgentMcp.AutoMethodTitle"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            autoStack.Children.Add(new TextBlock
            {
                Text = translate("CustomWidget.Workshop.AgentMcp.AutoMethodDesc"),
                FontSize = 12,
                Foreground = ResolveBrush(theme, "TextFillColorSecondaryBrush", Colors.Gray),
                TextWrapping = TextWrapping.Wrap
            });

            string autoLabel = translate("CustomWidget.AgentHub.Playbook.CopyCreate");
            var copyAutoBtn = new Button
            {
                Height = 34,
                Padding = new Thickness(14, 0, 14, 0),
                CornerRadius = new CornerRadius(6),
                Style = LookupStyle("AccentButtonStyle"),
                Content = autoLabel
            };
            copyAutoBtn.Click += async (_, _) =>
            {
                string prompt = CustomWidgetAgentConnectorService.BuildMcpSetupPrompt(cultureName);
                CopyTextToClipboard(prompt);
                onSelfTestBanner?.Invoke(true, translate("CustomWidget.Workshop.AgentMcp.AutoPromptCopiedHint"));
                await FlashCopiedButtonAsync(copyAutoBtn, autoLabel, translate("CustomWidget.Native.Dialog.CopiedButton"));
            };
            autoStack.Children.Add(copyAutoBtn);
            autoCard.Child = autoStack;
            container.Children.Add(autoCard);
        }

        // 1-Click IDE Deep Links + --mcp Command Row
        var quickIdeBar = new WrapFlowPanel { HorizontalSpacing = 8, VerticalSpacing = 6 };
        var cursorBtn = new Button
        {
            CornerRadius = new CornerRadius(6),
            Content = translate("CustomWidget.Workshop.Composer.InstallCursorMcp")
        };
        cursorBtn.Click += async (_, _) =>
        {
            if (Uri.TryCreate(CustomWidgetAgentConnectorService.BuildCursorMcpDeepLink(), UriKind.Absolute, out Uri? uri))
            {
                _ = await Windows.System.Launcher.LaunchUriAsync(uri);
            }
        };
        quickIdeBar.Children.Add(cursorBtn);

        var vsCodeBtn = new Button
        {
            CornerRadius = new CornerRadius(6),
            Content = translate("CustomWidget.Workshop.Composer.InstallVsCodeMcp")
        };
        vsCodeBtn.Click += async (_, _) =>
        {
            if (Uri.TryCreate(CustomWidgetAgentConnectorService.BuildVsCodeMcpDeepLink(), UriKind.Absolute, out Uri? uri))
            {
                _ = await Windows.System.Launcher.LaunchUriAsync(uri);
            }
        };
        quickIdeBar.Children.Add(vsCodeBtn);
        container.Children.Add(quickIdeBar);

        string cmdLine = CustomWidgetAgentConnectorService.FormatMcpCommandLine();
        TextBox cmdBox = CreateMonospaceReadOnlyBox(cmdLine, monoFont, minHeight: 36, maxHeight: 56);
        string copyCfgText = translate("CustomWidget.AgentHub.CopyMcpConfig");
        var copyCmdBtn = new Button
        {
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center,
            Content = copyCfgText
        };
        copyCmdBtn.Click += async (_, _) =>
        {
            CopyTextToClipboard(cmdLine);
            await FlashCopiedButtonAsync(copyCmdBtn, copyCfgText, translate("CustomWidget.Native.Dialog.CopiedButton"));
        };
        container.Children.Add(CreateInlineHeaderWithAction(cmdBox, copyCmdBtn));

        var formatCombo = new ComboBox
        {
            MinWidth = 260,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        int selectedIdx = 0;
        for (int i = 0; i < Formats.Length; i++)
        {
            formatCombo.Items.Add(new ComboBoxItem
            {
                Content = translate(Formats[i].LabelKey),
                Tag = Formats[i].Format
            });
            if (Formats[i].Format == initialFormat)
            {
                selectedIdx = i;
            }
        }
        formatCombo.SelectedIndex = selectedIdx;

        string currentSnippet = CustomWidgetAgentConnectorService.BuildMcpSnippet(Formats[selectedIdx].Format);
        TextBox snippetBox = CreateMonospaceReadOnlyBox(currentSnippet, monoFont, minHeight: 88, maxHeight: 164);

        formatCombo.SelectionChanged += (_, _) =>
        {
            if ((formatCombo.SelectedItem as ComboBoxItem)?.Tag is McpConfigFormat fmt)
            {
                onFormatSelected?.Invoke(fmt);
                currentSnippet = CustomWidgetAgentConnectorService.BuildMcpSnippet(fmt);
                snippetBox.Text = currentSnippet;
            }
        };

        var copySnippetBtn = new Button
        {
            CornerRadius = new CornerRadius(6),
            Content = copyCfgText
        };
        copySnippetBtn.Click += async (_, _) =>
        {
            CopyTextToClipboard(currentSnippet);
            onSelfTestBanner?.Invoke(true, translate("CustomWidget.AgentHub.CopyMcpSuccessHint"));
            await FlashCopiedButtonAsync(copySnippetBtn, copyCfgText, translate("CustomWidget.Native.Dialog.CopiedButton"));
        };

        container.Children.Add(CreateInlineHeaderWithAction(formatCombo, copySnippetBtn));
        container.Children.Add(snippetBox);

        var diagToolbar = new WrapFlowPanel { HorizontalSpacing = 8, VerticalSpacing = 6 };
        var selfTestBtn = new Button
        {
            CornerRadius = new CornerRadius(6),
            Content = translate("CustomWidget.AgentHub.SelfTestButton")
        };
        var diagStatusText = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ResolveBrush(theme, "TextFillColorSecondaryBrush", Colors.Gray)
        };
        selfTestBtn.Click += async (_, _) =>
        {
            selfTestBtn.IsEnabled = false;
            try
            {
                AgentBridgeSelfTestResult res = await CustomWidgetAgentConnectorService.RunBridgeSelfTestAsync(
                    cancellationToken: cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                bool healthy = res.McpProtocolOk && res.PipeOnline;
                diagStatusText.Foreground = healthy
                    ? ResolveBrush(theme, "SystemFillColorSuccessBrush", Color.FromArgb(0xFF, 0x10, 0x7C, 0x10))
                    : ResolveBrush(theme, "SystemFillColorCautionBrush", Color.FromArgb(0xFF, 0x9D, 0x5D, 0x00));
                diagStatusText.Text = healthy
                    ? format("CustomWidget.AgentHub.BridgeReady", [res.McpToolCount])
                    : (res.ErrorMessage ?? translate("CustomWidget.AgentHub.BridgeSubtitleOffline"));

                onSelfTestBanner?.Invoke(
                    healthy,
                    healthy
                        ? format("CustomWidget.AgentHub.SelfTestPassed", [res.McpToolCount, res.ElapsedMs])
                        : (res.ErrorMessage ?? translate("CustomWidget.AgentHub.BridgeSubtitleOffline")));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                diagStatusText.Foreground = ResolveBrush(theme, "SystemFillColorCriticalBrush", Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C));
                diagStatusText.Text = ex.Message;
            }
            finally
            {
                selfTestBtn.IsEnabled = true;
            }
        };
        diagToolbar.Children.Add(selfTestBtn);

        IReadOnlyList<DeskBoxAgentCliEntry.McpToolSummary> tools = DeskBoxAgentCliEntry.GetMcpToolSummaries();
        diagToolbar.Children.Add(new Button
        {
            CornerRadius = new CornerRadius(6),
            Content = format("CustomWidget.AgentHub.McpToolsExpanderHeader", [tools.Count]),
            Flyout = BuildMcpToolsFlyout(tools, theme, translate, monoFont)
        });
        diagToolbar.Children.Add(diagStatusText);
        container.Children.Add(diagToolbar);
    }

    private static Flyout BuildMcpToolsFlyout(
        IReadOnlyList<DeskBoxAgentCliEntry.McpToolSummary> tools,
        ElementTheme theme,
        Func<string, string> translate,
        FontFamily monoFont)
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        flyout.Opening += (_, _) =>
        {
            if (flyout.Content is not null)
            {
                return;
            }

            Brush secondaryFg = ResolveBrush(theme, "TextFillColorSecondaryBrush", Colors.Gray);
            var stack = new StackPanel { Spacing = 8, MaxWidth = 380, RequestedTheme = theme };
            foreach (DeskBoxAgentCliEntry.McpToolSummary tool in tools)
            {
                var itemStack = new StackPanel { Spacing = 2 };
                itemStack.Children.Add(new TextBlock
                {
                    Text = tool.Name,
                    FontFamily = monoFont,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    IsTextSelectionEnabled = true
                });
                itemStack.Children.Add(new TextBlock
                {
                    Text = translate(tool.DescriptionKey),
                    FontSize = 11.5,
                    Foreground = secondaryFg,
                    TextWrapping = TextWrapping.Wrap
                });
                stack.Children.Add(itemStack);
            }

            flyout.Content = new ScrollViewer
            {
                MaxHeight = 340,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = stack
            };
        };
        return flyout;
    }

    private static TextBox CreateMonospaceReadOnlyBox(string text, FontFamily monoFont, double minHeight, double maxHeight)
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = monoFont,
            FlowDirection = FlowDirection.LeftToRight,
            FontSize = 11.5,
            MinHeight = minHeight,
            MaxHeight = maxHeight,
            CornerRadius = new CornerRadius(6)
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        box.Text = text;
        return box;
    }

    private static Grid CreateInlineHeaderWithAction(FrameworkElement leftControl, FrameworkElement rightControl)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(leftControl, 0);
        grid.Children.Add(leftControl);
        Grid.SetColumn(rightControl, 1);
        grid.Children.Add(rightControl);
        return grid;
    }

    private static Style? LookupStyle(string key) =>
        Application.Current?.Resources is { } res && res.TryGetValue(key, out object? val) && val is Style style
            ? style
            : null;

    private static Brush ResolveBrush(ElementTheme theme, string key, Color fallback) =>
        NeutralInteractionBrush.ResolveThemedResource(key, theme) ?? new SolidColorBrush(fallback);
}
