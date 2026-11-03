using DeskBox.Helpers;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Controls;

public sealed partial class MarkdownDocumentView
{
    private readonly List<(Grid Host, Button CopyButton, PointerEventHandler Enter, PointerEventHandler Exit, RoutedEventHandler Click)> _inlineCodeContainers = [];

    private void ReleaseEnhancedCodeElements()
    {
        foreach (var entry in _inlineCodeContainers)
        {
            entry.Host.PointerEntered -= entry.Enter;
            entry.Host.PointerExited -= entry.Exit;
            entry.CopyButton.Click -= entry.Click;
        }

        _inlineCodeContainers.Clear();
    }

    private UIElement CreateEnhancedCodeContainer(Border codeSurface, string rawCode)
    {
        if (string.IsNullOrWhiteSpace(rawCode))
        {
            return codeSurface;
        }

        // Reserve subtle top-right clearance so the hover copy button never occludes short single-line snippets.
        codeSurface.Padding = new Thickness(9, 7, 30, 7);

        var icon = new FontIcon
        {
            Glyph = "\uE8C8",
            FontSize = 11,
            IsTextScaleFactorEnabled = false
        };

        var copyButton = new Button
        {
            Width = 24,
            Height = 24,
            MinWidth = 24,
            MinHeight = 24,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 4, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(0),
            Background = BrushResource("SubtleFillColorSecondaryBrush"),
            Foreground = _contentForeground,
            Opacity = 0,
            IsHitTestVisible = false,
            IsTabStop = false,
            Content = icon
        };
        ToolTipService.SetToolTip(copyButton, "Copy code");
        AutomationProperties.SetName(copyButton, "Copy code");

        var host = new Grid
        {
            Width = codeSurface.Width,
            MaxWidth = codeSurface.MaxWidth,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        host.Children.Add(codeSurface);
        host.Children.Add(copyButton);

        PointerEventHandler onEnter = (_, _) =>
        {
            copyButton.Opacity = 0.92;
            copyButton.IsHitTestVisible = true;
        };
        PointerEventHandler onExit = (_, _) =>
        {
            copyButton.Opacity = 0;
            copyButton.IsHitTestVisible = false;
        };
        RoutedEventHandler onClick = (_, _) =>
        {
            CopyCodeSnippetToClipboard(rawCode, icon);
        };

        host.PointerEntered += onEnter;
        host.PointerExited += onExit;
        copyButton.Click += onClick;
        _inlineCodeContainers.Add((host, copyButton, onEnter, onExit, onClick));
        return host;
    }

    private void CopyCodeSnippetToClipboard(string rawCode, FontIcon icon)
    {
        try
        {
            DeskBoxClipboardWriteScope.MarkText(rawCode);
            var pkg = new DataPackage();
            pkg.SetText(rawCode);
            Clipboard.SetContent(pkg);

            icon.Glyph = "\uE73E";
            _ = ResetCopyIconGlyphAsync(icon);
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[MarkdownDocumentView] Copy code block failed: {ex.Message}");
        }
    }

    private async Task ResetCopyIconGlyphAsync(FontIcon icon)
    {
        await Task.Delay(1200);
        if (!_visualHandlersReleased)
        {
            icon.Glyph = "\uE8C8";
        }
    }

    private void AppendPlainTextWithAutoLinks(InlineCollection destination, string? text)
    {
        string value = text ?? string.Empty;
        string[] lines = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            if (lineIndex > 0)
            {
                destination.Add(new LineBreak());
            }

            string line = lines[lineIndex];
            if (line.Length == 0)
            {
                continue;
            }

            AppendLineWithAutoLinks(destination, line);
        }
    }

    private void AppendLineWithAutoLinks(InlineCollection destination, string line)
    {
        int cursor = 0;
        while (cursor < line.Length)
        {
            int nextHttp = FindNextHttpUrlStart(line, cursor);
            if (nextHttp < 0)
            {
                destination.Add(new Run { Text = line[cursor..] });
                break;
            }

            if (nextHttp > cursor)
            {
                destination.Add(new Run { Text = line[cursor..nextHttp] });
            }

            int urlEnd = ScanUrlEnd(line, nextHttp);
            string candidate = line[nextHttp..urlEnd];
            if (MarkdownDocumentService.IsAllowedLink(candidate) &&
                Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
            {
                Hyperlink link = CreateHyperlink();
                link.NavigateUri = uri;
                link.Inlines.Add(new Run { Text = candidate });
                destination.Add(link);
                cursor = urlEnd;
            }
            else
            {
                int advance = Math.Min(line.Length, nextHttp + 4);
                destination.Add(new Run { Text = line[nextHttp..advance] });
                cursor = advance;
            }
        }
    }

    internal static int FindNextHttpUrlStart(string text, int startIndex)
    {
        int idx = startIndex;
        while (idx < text.Length)
        {
            int httpIdx = text.IndexOf("http", idx, StringComparison.OrdinalIgnoreCase);
            if (httpIdx < 0)
            {
                return -1;
            }

            ReadOnlySpan<char> tail = text.AsSpan(httpIdx);
            if (tail.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                tail.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                return httpIdx;
            }

            idx = httpIdx + 4;
        }

        return -1;
    }

    internal static int ScanUrlEnd(string text, int urlStart)
    {
        int end = urlStart;
        int openParens = 0;
        while (end < text.Length)
        {
            char ch = text[end];
            if (char.IsWhiteSpace(ch) || ch is '<' or '>' or '"' or '\'' or '`' or '，' or '。' or '；' or '！' or '？' or '、' or '）' or '】' or '》')
            {
                break;
            }

            if (ch == '(')
            {
                openParens++;
            }
            else if (ch == ')')
            {
                if (openParens <= 0)
                {
                    break;
                }

                openParens--;
            }

            end++;
        }

        while (end > urlStart && text[end - 1] is '.' or ',' or ';' or ':' or '!' or '?' or ']')
        {
            end--;
        }

        return end;
    }
}
