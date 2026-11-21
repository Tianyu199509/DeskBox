using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DeskBox.Services;

public sealed record MarkdownParseResult(
    string Source,
    MarkdownDocument Document,
    bool WasTruncated);

/// <summary>
/// Host-independent Markdown parsing and source operations. Raw Markdown is
/// always the source of truth; HTML is disabled and unsafe navigation schemes
/// never reach the native XAML presenter.
/// </summary>
public sealed partial class MarkdownDocumentService
{
    public const int MaxCharacters = 256 * 1024;

    private static readonly MarkdownPipeline s_pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseCitations()
        .DisableHtml()
        .Build();

    public MarkdownParseResult Parse(string? markdown)
    {
        string source = NormalizeSource(markdown);
        bool wasTruncated = source.Length > MaxCharacters;
        if (wasTruncated)
        {
            source = source[..MaxCharacters];
        }

        return new MarkdownParseResult(
            source,
            Markdown.Parse(source, s_pipeline),
            wasTruncated);
    }

    public string ToPlainText(string? markdown)
    {
        MarkdownParseResult result = Parse(markdown);
        var builder = new StringBuilder(result.Source.Length);
        AppendContainer(result.Document, builder);
        string decoded = WebUtility.HtmlDecode(builder.ToString());
        decoded = HorizontalWhitespaceRegex().Replace(decoded, " ");
        decoded = BlankLinesRegex().Replace(decoded, "\n");
        return decoded.Trim();
    }

    public string ToSafeHtml(string? markdown)
    {
        string source = NormalizeSource(markdown);
        if (source.Length > MaxCharacters)
        {
            source = source[..MaxCharacters];
        }

        return Markdown.ToHtml(source, s_pipeline);
    }

    public bool TryToggleTask(string? markdown, int taskIndex, out string updatedSource)
    {
        string source = NormalizeSource(markdown);
        updatedSource = source;
        if (taskIndex < 0 || source.Length == 0)
        {
            return false;
        }

        MarkdownParseResult result = Parse(source);
        TaskList[] tasks = result.Document.Descendants<TaskList>()
            .OrderBy(task => task.Span.Start)
            .ToArray();
        if (taskIndex >= tasks.Length)
        {
            return false;
        }

        SourceSpan span = tasks[taskIndex].Span;
        if (span.Length != 3 || span.Start < 0 || span.End >= source.Length ||
            source[span.Start] != '[' || source[span.End] != ']')
        {
            return false;
        }

        int markerIndex = span.Start + 1;
        char replacement = char.ToLowerInvariant(source[markerIndex]) == 'x'
            ? ' '
            : 'x';
        updatedSource = source[..markerIndex] + replacement + source[(markerIndex + 1)..];
        return true;
    }

    public static bool IsAllowedLink(string? value)
    {
        if (TryGetAttachmentId(value, out _))
        {
            return true;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
               (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsRemoteImage(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
         uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    public static bool TryGetAttachmentId(string? source, out string? attachmentId)
    {
        attachmentId = null;
        if (source?.StartsWith("attachment:", StringComparison.OrdinalIgnoreCase) == true)
        {
            attachmentId = source["attachment:".Length..].Trim('/');
            return !string.IsNullOrWhiteSpace(attachmentId);
        }

        const string deskBoxPrefix = "deskbox-attachment://";
        if (source?.StartsWith(deskBoxPrefix, StringComparison.OrdinalIgnoreCase) == true)
        {
            attachmentId = source[deskBoxPrefix.Length..].Trim('/');
            return !string.IsNullOrWhiteSpace(attachmentId);
        }

        return false;
    }

    private static string NormalizeSource(string? markdown) =>
        (markdown ?? string.Empty).Replace("\0", string.Empty, StringComparison.Ordinal);

    private static void AppendContainer(ContainerBlock container, StringBuilder builder)
    {
        foreach (Block block in container)
        {
            if (block is LeafBlock leaf)
            {
                AppendInline(leaf.Inline, builder);
                if (leaf is CodeBlock code)
                {
                    AppendSeparated(builder, code.Lines.ToString());
                }
            }
            else if (block is ContainerBlock nested)
            {
                AppendContainer(nested, builder);
            }

            if (builder.Length > 0 && builder[^1] != '\n')
            {
                builder.AppendLine();
            }
        }
    }

    private static void AppendInline(ContainerInline? inline, StringBuilder builder)
    {
        if (inline is null)
        {
            return;
        }

        for (Inline? current = inline.FirstChild; current is not null; current = current.NextSibling)
        {
            switch (current)
            {
                case TaskList task:
                    builder.Append(task.Checked ? "\u2611 " : "\u2610 ");
                    break;
                case LiteralInline literal:
                    builder.Append(literal.Content.AsSpan());
                    break;
                case CodeInline code:
                    builder.Append(code.Content);
                    break;
                case LineBreakInline:
                    builder.AppendLine();
                    break;
                case ContainerInline nested:
                    AppendInline(nested, builder);
                    break;
            }
        }
    }

    private static void AppendSeparated(StringBuilder builder, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]))
        {
            builder.Append(' ');
        }

        builder.Append(value);
    }

    [GeneratedRegex(@"[^\S\r\n]+")]
    private static partial Regex HorizontalWhitespaceRegex();

    [GeneratedRegex(@"\n{2,}")]
    private static partial Regex BlankLinesRegex();
}
