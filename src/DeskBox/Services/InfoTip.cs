using CommunityToolkit.WinUI.Controls;
using CommunityToolkit.WinUI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Services;

/// <summary>
/// Attaches a click-to-open explanation flyout to a small accent badge.
/// Anchors attach directly to SettingsCard/SettingsExpander elements via
/// TitleKey/BodyKey: the header pipeline (Localized.SetLocalizedHeader)
/// composes "header text + ? badge" so the badge sits beside the card
/// title; the button itself is manufactured in code (CreateAnchorButton).
/// Long explanatory copy (2-5 sentences) lives behind ONE shared Flyout
/// instead of per-card InfoBars: a Flyout is a popup layer inside the
/// current window (no extra HWND or page per topic), and its text is
/// resolved from localization keys at open time, so deferred-created
/// settings sections only pay for the tiny anchor button.
/// TeachingTip was rejected for this role: opening one pins the whole page
/// in memory (microsoft-ui-xaml #2849) and page-level theme resources fail
/// to resolve in late-created trees (#10095) — the same pitfall the notice
/// InfoBars already work around with code-behind brush pinning.
/// </summary>
/// <remarks>
/// Body copy is Markdown-lite: blank-line/newline-separated paragraphs and
/// **bold** spans. Paragraphs render as separate TextBlocks with block line
/// height (~1.5x font) so long CJK explanations stay readable; bold marks
/// the lead term of each point, matching Microsoft's help-copy style.
/// </remarks>
public static class InfoTip
{
    public static readonly DependencyProperty TitleKeyProperty =
        DependencyProperty.RegisterAttached(
            "TitleKey",
            typeof(string),
            typeof(InfoTip),
            new PropertyMetadata(null, OnKeyChanged));

    public static readonly DependencyProperty BodyKeyProperty =
        DependencyProperty.RegisterAttached(
            "BodyKey",
            typeof(string),
            typeof(InfoTip),
            new PropertyMetadata(null, OnKeyChanged));

    public static string? GetTitleKey(DependencyObject obj)
    {
        return (string?)obj.GetValue(TitleKeyProperty);
    }

    public static void SetTitleKey(DependencyObject obj, string? value)
    {
        obj.SetValue(TitleKeyProperty, value);
    }

    public static string? GetBodyKey(DependencyObject obj)
    {
        return (string?)obj.GetValue(BodyKeyProperty);
    }

    public static void SetBodyKey(DependencyObject obj, string? value)
    {
        obj.SetValue(BodyKeyProperty, value);
    }

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Button button)
        {
            // Legacy direct anchor (button in XAML content area).
            // Idempotent wiring: both keys route through the same handler.
            button.Click -= OnInfoTipButtonClicked;
            button.Click += OnInfoTipButtonClicked;

            string helpLabel = Localized.T("Common.Help");
            AutomationProperties.SetName(button, helpLabel);
            ToolTipService.SetToolTip(button, helpLabel);
            return;
        }

        // Card-side anchor: XAML parses attributes in document order, so the
        // TitleKey attach fires before BodyKey is set (and possibly before
        // the Localized pipeline applies the header). Wrapping eagerly would
        // bake a partially-attached state into the badge. Defer the wrap to
        // after the parse batch, when all keys are present.
        if (d is SettingsCard or SettingsExpander)
        {
            if (d is FrameworkElement { DispatcherQueue: { } queue } element)
            {
                queue.TryEnqueue(() =>
                {
                    if (GetTitleKey(element) is null && GetBodyKey(element) is null)
                    {
                        return;
                    }

                    string? current = (element as SettingsCard)?.Header as string
                        ?? (element as SettingsExpander)?.Header as string;
                    if (current is null)
                    {
                        return;
                    }

                    object? composite = TryCreateHeaderContent(element, current);
                    if (composite is null)
                    {
                        return;
                    }

                    if (element is SettingsCard card)
                    {
                        card.Header = composite;
                    }
                    else if (element is SettingsExpander expander)
                    {
                        expander.Header = composite;
                    }
                });
            }
        }
    }

    /// <summary>
    /// Builds the "header text + ? badge" composite used as a card header
    /// when InfoTip keys are attached to the card. Returns null when the
    /// card carries no InfoTip keys, letting the caller keep plain text.
    /// </summary>
    public static object? TryCreateHeaderContent(DependencyObject card, string headerText)
    {
        string? titleKey = GetTitleKey(card);
        string? bodyKey = GetBodyKey(card);
        if (string.IsNullOrWhiteSpace(titleKey) && string.IsNullOrWhiteSpace(bodyKey))
        {
            return null;
        }

        var header = new TextBlock
        {
            Text = headerText,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (card is FrameworkElement fe && fe.TryFindResource("SettingsCardHeaderTextBlockStyle") is Style headerStyle)
        {
            header.Style = headerStyle;
        }

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };
        panel.Children.Add(header);
        panel.Children.Add(CreateAnchorButton(titleKey, bodyKey));
        return panel;
    }

    /// <summary>
    /// Manufactures the quiet "?" anchor badge. Kept in code (not XAML)
    /// so card-side anchors need no per-site button markup. The Segoe
    /// Fluent "?" glyph (E897) sits in the style's translucent neutral
    /// circle — bare glyph anchors were rejected, but inside the tinted
    /// badge it renders pixel-hinted and native at this size.
    /// </summary>
    private static Button CreateAnchorButton(string? titleKey, string? bodyKey)
    {
        var button = new Button();
        if (button.TryFindResource("InfoTipButtonStyle") is Style style)
        {
            button.Style = style;
        }

        // The click handler resolves keys from the button itself, so the
        // card-side keys are mirrored onto the manufactured anchor.
        if (!string.IsNullOrWhiteSpace(titleKey))
        {
            SetTitleKey(button, titleKey);
        }
        if (!string.IsNullOrWhiteSpace(bodyKey))
        {
            SetBodyKey(button, bodyKey);
        }

        button.Content = new FontIcon
        {
            Glyph = "\uE897",
            FontSize = 9
        };

        // Click wiring happens in OnKeyChanged when the mirrored keys are
        // set above (idempotent -=/+=), so no explicit subscription here.
        string helpLabel = Localized.T("Common.Help");
        AutomationProperties.SetName(button, helpLabel);
        ToolTipService.SetToolTip(button, helpLabel);
        return button;
    }

    private static void OnInfoTipButtonClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        string? titleKey = GetTitleKey(button);
        string? bodyKey = GetBodyKey(button);
        if (string.IsNullOrWhiteSpace(titleKey) && string.IsNullOrWhiteSpace(bodyKey))
        {
            return;
        }

        Flyout flyout = EnsureSharedFlyout();
        s_sharedTitleBlock!.Text = string.IsNullOrWhiteSpace(titleKey)
            ? string.Empty
            : Localized.T(titleKey!);
        s_sharedTitleBlock.Visibility = string.IsNullOrEmpty(s_sharedTitleBlock.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
        FillBody(s_sharedBodyHost!, string.IsNullOrWhiteSpace(bodyKey) ? string.Empty : Localized.T(bodyKey!));

        // Reset per show: anchors can live in different windows/roots, and a
        // stale XamlRoot from a closed settings window would break ShowAt.
        flyout.XamlRoot = button.XamlRoot;
        flyout.ShowAt(button);
    }

    private static Flyout? s_sharedFlyout;
    private static TextBlock? s_sharedTitleBlock;
    private static StackPanel? s_sharedBodyHost;

    private static Flyout EnsureSharedFlyout()
    {
        if (s_sharedFlyout is { } existing)
        {
            return existing;
        }

        var title = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            LineHeight = 22,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Margin = new Thickness(0, 0, 0, 4),
            TextWrapping = TextWrapping.Wrap
        };
        var bodyHost = new StackPanel
        {
            Spacing = 10
        };
        var panel = new StackPanel
        {
            MaxWidth = 420,
            MinWidth = 320,
            Spacing = 12,
            Children = { title, bodyHost }
        };
        s_sharedTitleBlock = title;
        s_sharedBodyHost = bodyHost;
        s_sharedFlyout = new Flyout
        {
            Content = panel,
            Placement = FlyoutPlacementMode.Bottom
        };
        return s_sharedFlyout;
    }

    /// <summary>
    /// Renders the body: one TextBlock per newline-separated paragraph
    /// (blank lines collapse), with **bold** spans converted to bold runs.
    /// </summary>
    private static void FillBody(StackPanel host, string body)
    {
        host.Children.Clear();
        foreach (string paragraph in body.Replace("\r\n", "\n").Split('\n'))
        {
            string trimmed = paragraph.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var block = new TextBlock
            {
                FontSize = 13,
                LineHeight = 20,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Opacity = 0.95,
                TextWrapping = TextWrapping.Wrap
            };
            AppendMarkdownLiteInlines(block, trimmed);
            host.Children.Add(block);
        }
    }

    private static void AppendMarkdownLiteInlines(TextBlock block, string text)
    {
        // Split on ** pairs: even segments are plain runs, odd ones bold.
        string[] segments = text.Split("**");
        for (int index = 0; index < segments.Length; index++)
        {
            if (segments[index].Length == 0)
            {
                continue;
            }

            Run run = new() { Text = segments[index] };
            if (index % 2 == 1)
            {
                run.FontWeight = FontWeights.SemiBold;
            }

            block.Inlines.Add(run);
        }
    }
}
