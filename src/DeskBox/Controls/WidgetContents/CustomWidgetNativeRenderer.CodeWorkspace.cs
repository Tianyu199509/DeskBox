using System.Numerics;
using System.Text;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class CustomWidgetNativeRenderer
{
    private static readonly Color DarkJsonKeyColor = Color.FromArgb(255, 125, 211, 252);
    private static readonly Color DarkJsonStringColor = Color.FromArgb(255, 134, 239, 172);
    private static readonly Color DarkJsonNumberColor = Color.FromArgb(255, 253, 186, 116);
    private static readonly Color DarkJsonLiteralColor = Color.FromArgb(255, 196, 181, 253);

    private static readonly Color LightJsonKeyColor = Color.FromArgb(255, 3, 105, 161);
    private static readonly Color LightJsonStringColor = Color.FromArgb(255, 21, 128, 61);
    private static readonly Color LightJsonNumberColor = Color.FromArgb(255, 194, 65, 12);
    private static readonly Color LightJsonLiteralColor = Color.FromArgb(255, 109, 40, 217);

    internal static string FormatCodeLineNumbers(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return "1";
        }

        int lines = 1;
        for (int i = 0; i < code.Length; i++)
        {
            if (code[i] == '\n')
            {
                lines++;
            }
        }

        if (lines == 1)
        {
            return "1";
        }

        var sb = new StringBuilder(lines * 3);
        for (int line = 1; line <= lines; line++)
        {
            if (line > 1)
            {
                sb.Append('\n');
            }
            sb.Append(line);
        }
        return sb.ToString();
    }

    internal readonly record struct CodeHighlightToken(string Text, string Role);

    internal static List<CodeHighlightToken> TokenizeCodeForHighlight(string? code, string? languageHint)
    {
        code ??= string.Empty;
        var tokens = new List<CodeHighlightToken>();
        bool wantsJson = string.Equals(languageHint, "json", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(languageHint, "auto", StringComparison.OrdinalIgnoreCase);

        ReadOnlySpan<char> trimmed = code.AsSpan().TrimStart();
        if (!wantsJson ||
            code.Length == 0 ||
            code.Length > 65_536 ||
            (trimmed.Length > 0 && trimmed[0] != '{' && trimmed[0] != '['))
        {
            if (code.Length > 0)
            {
                tokens.Add(new CodeHighlightToken(code, "default"));
            }
            return tokens;
        }

        int i = 0;
        int len = code.Length;
        while (i < len)
        {
            char ch = code[i];

            if (char.IsWhiteSpace(ch))
            {
                int start = i;
                while (i < len && char.IsWhiteSpace(code[i]))
                {
                    i++;
                }
                tokens.Add(new CodeHighlightToken(code[start..i], "ws"));
                continue;
            }

            if (ch == '"')
            {
                int start = i++;
                bool escaped = false;
                while (i < len)
                {
                    char c = code[i++];
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }
                    if (c == '\\')
                    {
                        escaped = true;
                        continue;
                    }
                    if (c == '"')
                    {
                        break;
                    }
                }

                int j = i;
                while (j < len && char.IsWhiteSpace(code[j]))
                {
                    j++;
                }
                bool isPropertyKey = j < len && code[j] == ':';
                tokens.Add(new CodeHighlightToken(code[start..i], isPropertyKey ? "key" : "string"));
                continue;
            }

            if (ch is '{' or '}' or '[' or ']' or ':' or ',')
            {
                int start = i++;
                while (i < len && code[i] is '{' or '}' or '[' or ']' or ':' or ',')
                {
                    i++;
                }
                tokens.Add(new CodeHighlightToken(code[start..i], "punct"));
                continue;
            }

            if ((ch == '-' || char.IsAsciiDigit(ch)) &&
                (i == 0 || code[i - 1] is ':' or ',' or '[' || char.IsWhiteSpace(code[i - 1])))
            {
                int start = i++;
                while (i < len && (char.IsAsciiDigit(code[i]) || code[i] is '.' or 'e' or 'E' or '+' or '-'))
                {
                    i++;
                }
                tokens.Add(new CodeHighlightToken(code[start..i], "number"));
                continue;
            }

            if (TryMatchJsonKeyword(code, i, "true", out int nextIdx) ||
                TryMatchJsonKeyword(code, i, "false", out nextIdx) ||
                TryMatchJsonKeyword(code, i, "null", out nextIdx))
            {
                tokens.Add(new CodeHighlightToken(code[i..nextIdx], "literal"));
                i = nextIdx;
                continue;
            }

            int plainStart = i++;
            while (i < len &&
                   !char.IsWhiteSpace(code[i]) &&
                   code[i] is not ('"' or '{' or '}' or '[' or ']' or ':' or ','))
            {
                i++;
            }
            tokens.Add(new CodeHighlightToken(code[plainStart..i], "default"));
        }

        return tokens;
    }

    internal static void PopulateHighlightedCodeInlines(
        TextBlock codeTb,
        string code,
        string? languageHint,
        bool isDark,
        Brush defaultBrush,
        Brush mutedBrush,
        Func<Color, Brush>? brushFactory = null)
    {
        ArgumentNullException.ThrowIfNull(codeTb);
        code ??= string.Empty;

        List<CodeHighlightToken> tokens = TokenizeCodeForHighlight(code, languageHint);
        if (tokens.Count == 0 || (tokens.Count == 1 && string.Equals(tokens[0].Role, "default", StringComparison.Ordinal)))
        {
            codeTb.Inlines.Clear();
            codeTb.Foreground = defaultBrush;
            codeTb.Text = code;
            return;
        }

        Func<Color, Brush> resolveBrush = brushFactory ?? (static c => new SolidColorBrush(c));
        Brush keyBrush = resolveBrush(isDark ? DarkJsonKeyColor : LightJsonKeyColor);
        Brush strBrush = resolveBrush(isDark ? DarkJsonStringColor : LightJsonStringColor);
        Brush numBrush = resolveBrush(isDark ? DarkJsonNumberColor : LightJsonNumberColor);
        Brush litBrush = resolveBrush(isDark ? DarkJsonLiteralColor : LightJsonLiteralColor);

        codeTb.Inlines.Clear();
        codeTb.Foreground = defaultBrush;

        foreach (CodeHighlightToken tok in tokens)
        {
            Brush brush = tok.Role switch
            {
                "key" => keyBrush,
                "string" => strBrush,
                "punct" => mutedBrush,
                "number" => numBrush,
                "literal" => litBrush,
                _ => defaultBrush
            };
            AddCodeSpan(codeTb, tok.Text, brush);
        }
    }

    private static bool TryMatchJsonKeyword(string source, int index, string keyword, out int nextIndex)
    {
        nextIndex = index;
        if (index + keyword.Length > source.Length ||
            !source.AsSpan(index, keyword.Length).Equals(keyword.AsSpan(), StringComparison.Ordinal))
        {
            return false;
        }

        bool leftOk = index == 0 || source[index - 1] is ':' or ',' or '[' || char.IsWhiteSpace(source[index - 1]);
        int after = index + keyword.Length;
        bool rightOk = after >= source.Length || source[after] is ',' or '}' or ']' || char.IsWhiteSpace(source[after]);
        if (!leftOk || !rightOk)
        {
            return false;
        }

        nextIndex = after;
        return true;
    }

    private static void AddCodeSpan(TextBlock tb, string text, Brush foreground)
    {
        if (text.Length > 0)
        {
            tb.Inlines.Add(new Run { Text = text, Foreground = foreground });
        }
    }

    private void PlayCodeRefreshFeedback(UIElement codeVisual, Border cardBorder, Brush restoreBorderBrush)
    {
        if (!FluentMotionHelper.AreAnimationsEnabled)
        {
            return;
        }

        FluentMotionHelper.PlayEntranceSlideAndFade(codeVisual, offsetX: 0f, offsetY: 2f, durationMs: 130);

        DispatcherQueue? dq = cardBorder.DispatcherQueue;
        if (dq is null)
        {
            return;
        }

        try
        {
            cardBorder.BorderBrush = ResolveForegroundBrush("accent");
            DispatcherQueueTimer pulseTimer = dq.CreateTimer();
            pulseTimer.Interval = TimeSpan.FromMilliseconds(260);
            pulseTimer.IsRepeating = false;
            void Cleanup(object _, RoutedEventArgs __)
            {
                pulseTimer.Stop();
                cardBorder.Unloaded -= Cleanup;
            }
            cardBorder.Unloaded += Cleanup;
            pulseTimer.Tick += (_, _) =>
            {
                pulseTimer.Stop();
                cardBorder.Unloaded -= Cleanup;
                cardBorder.BorderBrush = restoreBorderBrush;
            };
            pulseTimer.Start();
        }
        catch
        {
        }
    }

    private static void PlayHorizontalErrorNudge(UIElement target)
    {
        if (!FluentMotionHelper.AreAnimationsEnabled)
        {
            return;
        }

        try
        {
            Compositor compositor = CompositionTarget.GetCompositorForCurrentThread();
            float baseZ = target.Translation.Z;
            Vector3KeyFrameAnimation nudge = compositor.CreateVector3KeyFrameAnimation();
            nudge.Target = nameof(UIElement.Translation);
            nudge.InsertKeyFrame(0f, new Vector3(0f, 0f, baseZ));
            nudge.InsertKeyFrame(0.25f, new Vector3(-2.5f, 0f, baseZ));
            nudge.InsertKeyFrame(0.55f, new Vector3(2.5f, 0f, baseZ));
            nudge.InsertKeyFrame(0.8f, new Vector3(-1.2f, 0f, baseZ));
            nudge.InsertKeyFrame(1f, new Vector3(0f, 0f, baseZ));
            nudge.Duration = TimeSpan.FromMilliseconds(220);

            target.Translation = new Vector3(0f, 0f, baseZ);
            target.StartAnimation(nudge);
        }
        catch
        {
        }
    }

    private Action AttachCopyMorphFeedback(
        Button btn,
        FrameworkElement? iconEl,
        TextBlock? labelTb,
        Func<string?>? copiedLabelResolver,
        Func<string>? restoreLabelResolver,
        Func<string>? restoreGlyphResolver,
        Brush? defaultIconForeground)
    {
        DispatcherQueueTimer? resetTimer = null;
        void StopResetTimer()
        {
            if (resetTimer is not null)
            {
                resetTimer.Stop();
                resetTimer = null;
            }
        }

        btn.Unloaded += (_, _) => StopResetTimer();

        void TriggerMorph()
        {
            if (btn.ActualWidth > 0 && (double.IsNaN(btn.MinWidth) || btn.MinWidth < btn.ActualWidth))
            {
                btn.MinWidth = Math.Ceiling(btn.ActualWidth);
            }

            if (iconEl is FontIcon fi)
            {
                fi.Glyph = "\uE73E";
                fi.Foreground = ResolveForegroundBrush("emerald");
                if (FluentMotionHelper.AreAnimationsEnabled)
                {
                    FluentMotionHelper.AnimateHoverScale(fi, 1.12f, durationMs: 90);
                }
            }

            if (labelTb is not null)
            {
                string? copiedLabel = copiedLabelResolver?.Invoke();
                if (!string.IsNullOrWhiteSpace(copiedLabel))
                {
                    labelTb.Text = copiedLabel;
                }
            }

            DispatcherQueue? dq = btn.DispatcherQueue;
            if (dq is null)
            {
                return;
            }

            StopResetTimer();
            resetTimer = dq.CreateTimer();
            resetTimer.Interval = TimeSpan.FromMilliseconds(1200);
            resetTimer.IsRepeating = false;
            resetTimer.Tick += (_, _) =>
            {
                StopResetTimer();
                if (iconEl is FontIcon restoreFi)
                {
                    string glyph = restoreGlyphResolver?.Invoke() ?? "\uE8C8";
                    UpdateInlineIconGlyph(restoreFi, string.IsNullOrWhiteSpace(glyph) ? "\uE8C8" : glyph);
                    if (defaultIconForeground is not null)
                    {
                        restoreFi.Foreground = defaultIconForeground;
                    }
                    else
                    {
                        restoreFi.ClearValue(IconElement.ForegroundProperty);
                    }
                    if (FluentMotionHelper.AreAnimationsEnabled)
                    {
                        FluentMotionHelper.AnimateHoverScale(restoreFi, 1.0f, durationMs: 120);
                    }
                }

                if (labelTb is not null && restoreLabelResolver is not null)
                {
                    labelTb.Text = restoreLabelResolver();
                }
            };
            resetTimer.Start();
        }

        return TriggerMorph;
    }

    private void AttachWorkspaceEditorBehavior(TextBox textBox, CustomWidgetNodeSpec spec)
    {
        if (!string.Equals(spec.Id, "workspace-editor", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string prevEditing = "false";
        string prevFocusSeq = string.Empty;

        RegisterRetainedPatcher(liveCtx =>
        {
            string curEditing = liveCtx.State.GetValueOrDefault("editing", "false");
            string curFocusSeq = liveCtx.State.GetValueOrDefault("focusSeq", string.Empty);
            bool enteredEdit = string.Equals(curEditing, "true", StringComparison.OrdinalIgnoreCase) &&
                             (!string.Equals(prevEditing, "true", StringComparison.OrdinalIgnoreCase) ||
                              !string.Equals(prevFocusSeq, curFocusSeq, StringComparison.Ordinal));
            prevEditing = curEditing;
            prevFocusSeq = curFocusSeq;

            if (!enteredEdit)
            {
                return;
            }

            string errOffsetRaw = liveCtx.State.GetValueOrDefault("errorOffset", string.Empty);
            textBox.DispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                try
                {
                    textBox.Focus(FocusState.Programmatic);
                    int textLen = textBox.Text?.Length ?? 0;
                    if (int.TryParse(errOffsetRaw, out int offset) && offset >= 0 && offset <= textLen)
                    {
                        textBox.Select(offset, Math.Min(1, Math.Max(0, textLen - offset)));
                    }
                    else
                    {
                        textBox.Select(textLen, 0);
                    }
                }
                catch
                {
                }
            });
        });

        textBox.KeyDown += (_, e) => FireAndForgetSafeUi(async () =>
        {
            if (DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e) ||
                e.Key != VirtualKey.Escape)
            {
                return;
            }

            CustomWidgetEvaluationContext liveCtx = _viewModel.CreateEvaluationContext();
            if (string.Equals(liveCtx.State.GetValueOrDefault("editing"), "true", StringComparison.OrdinalIgnoreCase))
            {
                e.Handled = true;
                EndInteractiveSession(textBox);
                await _viewModel.ExecuteActionsAsync(
                [
                    new CustomWidgetActionSpec("state.set", Key: "editing", Value: "false")
                ], liveCtx);
            }
        });
    }

    private static bool IsCopyActionNode(CustomWidgetNodeSpec spec)
    {
        if (string.Equals(spec.Id, "btn-copy", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        for (int i = 0; i < spec.OnClick.Count; i++)
        {
            if (string.Equals(spec.OnClick[i].Type, "clipboard.copy", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
