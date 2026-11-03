using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Helpers;

/// <summary>
/// Provides Markdown list and checklist continuation and Undo-preserving newline insertion
/// for WinUI <see cref="TextBox"/> editors without replacing <see cref="TextBox.Text"/>.
/// </summary>
internal static partial class MarkdownListContinuationHelper
{
    public static bool TryContinueListOnEnter(TextBox textBox)
    {
        ArgumentNullException.ThrowIfNull(textBox);

        string text = textBox.Text ?? string.Empty;
        int selectionStart = Math.Clamp(textBox.SelectionStart, 0, text.Length);
        int selectionLength = Math.Clamp(textBox.SelectionLength, 0, text.Length - selectionStart);
        string newline = DetectEditorNewline(text);

        if (!TryComputeContinuationEdit(
                text,
                selectionStart,
                selectionLength,
                newline,
                out int replaceStart,
                out int replaceLength,
                out string replacement,
                out int caretIndex))
        {
            return false;
        }

        ApplyRangeReplacementPreservingUndo(
            textBox,
            replaceStart,
            replaceLength,
            replacement,
            caretIndex);
        return true;
    }

    public static void InsertNewlinePreservingUndo(TextBox textBox, bool continueMarkdownList = false)
    {
        ArgumentNullException.ThrowIfNull(textBox);

        if (continueMarkdownList && TryContinueListOnEnter(textBox))
        {
            return;
        }

        string text = textBox.Text ?? string.Empty;
        int selectionStart = Math.Clamp(textBox.SelectionStart, 0, text.Length);
        int selectionLength = Math.Clamp(textBox.SelectionLength, 0, text.Length - selectionStart);
        string newline = DetectEditorNewline(text);

        ApplyRangeReplacementPreservingUndo(
            textBox,
            selectionStart,
            selectionLength,
            newline,
            selectionStart + newline.Length);
    }

    public static bool TryComputeContinuationEdit(
        string text,
        int selectionStart,
        int selectionLength,
        string newline,
        out int replaceStart,
        out int replaceLength,
        out string replacement,
        out int caretIndex)
    {
        replaceStart = 0;
        replaceLength = 0;
        replacement = string.Empty;
        caretIndex = selectionStart;

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        selectionStart = Math.Clamp(selectionStart, 0, text.Length);
        selectionLength = Math.Clamp(selectionLength, 0, text.Length - selectionStart);
        if (selectionLength > 0)
        {
            return false;
        }

        int lineStart = FindLineStart(text, selectionStart);
        int lineEnd = FindLineEnd(text, selectionStart);
        string lineBeforeCaret = text[lineStart..selectionStart];
        string lineAfterCaret = text[selectionStart..lineEnd];

        Match match = ListLinePrefixRegex().Match(lineBeforeCaret);
        if (!match.Success)
        {
            return false;
        }

        string contentBeforeCaret = match.Groups["content"].Value;
        if (string.IsNullOrWhiteSpace(contentBeforeCaret) &&
            string.IsNullOrWhiteSpace(lineAfterCaret))
        {
            // Pressing Enter on an empty list/task prefix line removes the empty
            // prefix cleanly so the caret stays on a plain line and exits the list.
            replaceStart = lineStart;
            replaceLength = lineEnd - lineStart;
            replacement = string.Empty;
            caretIndex = lineStart;
            return true;
        }

        string indent = match.Groups["indent"].Value;
        string marker = match.Groups["marker"].Value;
        if (match.Groups["number"].Success &&
            int.TryParse(
                match.Groups["number"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int number))
        {
            marker = string.Concat(
                (number + 1).ToString(CultureInfo.InvariantCulture),
                match.Groups["punctuation"].Value);
        }

        // Continuing a completed task (- [x] Done) must always insert an unchecked
        // task prefix (- [ ] ), never carrying [x] onto the new item.
        string taskSegment = match.Groups["task"].Success ? "[ ] " : string.Empty;
        string continuation = string.Concat(newline, indent, marker, " ", taskSegment);

        replaceStart = selectionStart;
        replaceLength = selectionLength;
        replacement = continuation;
        caretIndex = selectionStart + continuation.Length;
        return true;
    }

    public static string DetectEditorNewline(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "\r";
        }

        if (text.Contains("\r\n", StringComparison.Ordinal))
        {
            return "\r\n";
        }

        if (text.Contains('\r'))
        {
            return "\r";
        }

        return text.Contains('\n') ? "\n" : "\r";
    }

    private static void ApplyRangeReplacementPreservingUndo(
        TextBox textBox,
        int start,
        int length,
        string replacement,
        int targetCaretIndex)
    {
        int currentLength = textBox.Text?.Length ?? 0;
        int clampedStart = Math.Clamp(start, 0, currentLength);
        int clampedLength = Math.Clamp(length, 0, currentLength - clampedStart);

        textBox.Select(clampedStart, clampedLength);
        textBox.SelectedText = replacement;

        int updatedLength = textBox.Text?.Length ?? 0;
        int finalCaret = Math.Clamp(targetCaretIndex, 0, updatedLength);
        textBox.SelectionStart = finalCaret;
        textBox.SelectionLength = 0;
    }

    private static int FindLineStart(string text, int position)
    {
        for (int index = Math.Min(position, text.Length) - 1; index >= 0; index--)
        {
            if (text[index] is '\r' or '\n')
            {
                return index + 1;
            }
        }

        return 0;
    }

    private static int FindLineEnd(string text, int position)
    {
        for (int index = Math.Max(0, position); index < text.Length; index++)
        {
            if (text[index] is '\r' or '\n')
            {
                return index;
            }
        }

        return text.Length;
    }

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?:(?<number>\d+)(?<punctuation>[.)])|(?<marker>[-+*]))(?:[ \t]+(?<task>\[[ xX]\])(?:[ \t]+(?<content>.*)|(?<content>$))|[ \t]+(?<content>.*))$")]
    private static partial Regex ListLinePrefixRegex();
}
