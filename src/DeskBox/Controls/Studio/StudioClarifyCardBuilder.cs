using System.Text;
using DeskBox.Controls.WidgetContents;
using DeskBox.Services;
using DeskBox.Views;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls.Studio;

/// <summary>
/// Builds native WinUI 3 OMP <c>ask</c>-style inline multi-dimension confirmation cards
/// for <c>kind="clarify"</c> Studio turns, with preselected recommended defaults, optional
/// custom text input per dimension, and a 1-click "Build with Selected Plan" action.
/// </summary>
internal static class StudioClarifyCardBuilder
{
    private sealed class DraftQuestionSnapshot
    {
        public HashSet<int> SelectedOptionIndices { get; } = [];
        public bool CustomInputActive { get; set; }
        public string CustomText { get; set; } = string.Empty;
    }

    internal sealed class QuestionSelectionState
    {
        public required StudioClarifyQuestionSpec Spec { get; init; }
        public HashSet<int> SelectedOptionIndices { get; } = [];
        public bool CustomInputActive { get; set; }
        public string CustomText { get; set; } = string.Empty;
        public List<Button> OptionButtons { get; } = [];
        public Button? CustomToggleButton { get; set; }
        public TextBox? CustomTextBox { get; set; }
    }

    private static readonly Dictionary<string, List<DraftQuestionSnapshot>> s_draftSelectionsByMessageId =
        new(StringComparer.Ordinal);

    internal static UIElement Build(
        CustomWidgetStudioWindow shell,
        StudioChatMessage message,
        bool isChinese,
        bool isFollowedByNewerTurn = false)
    {
        LocalizationService loc = shell.Localization;
        string cultureName = loc.CurrentCultureName;
        Windows.UI.Color accent = StudioUiPrimitives.ResolveAccentColor(shell);
        bool isDark = StudioUiPrimitives.GetStudioExplicitTheme(shell) == ElementTheme.Dark;

        // Compact receipt state once submitted, superseded, or bypassed by a newer user turn
        if (!string.IsNullOrWhiteSpace(message.SubmittedSummary) || message.IsSuperseded || isFollowedByNewerTurn)
        {
            s_draftSelectionsByMessageId.Remove(message.Id);
            string receiptText = !string.IsNullOrWhiteSpace(message.SubmittedSummary)
                ? message.SubmittedSummary!
                : (isFollowedByNewerTurn
                    ? loc.T("CustomWidget.Workshop.Clarify.ContinuedFollowUp")
                    : BuildDefaultSummaryLine(message.ClarifyQuestions, cultureName, loc));

            var receiptRow = new Grid { ColumnSpacing = 8 };
            receiptRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            receiptRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            receiptRow.Children.Add(new FontIcon
            {
                Glyph = "\uE73E",
                FontSize = 12,
                Foreground = StudioUiPrimitives.ThemeBrush(shell, "AccentTextFillColorPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });

            var summaryBlock = new TextBlock
            {
                Text = receiptText,
                FontSize = 12,
                Foreground = StudioUiPrimitives.ThemeBrush(shell, "TextFillColorSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(summaryBlock, 1);
            receiptRow.Children.Add(summaryBlock);

            return new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 7, 10, 7),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(isDark ? 64 : 52), accent.R, accent.G, accent.B)),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(isDark ? 22 : 16), accent.R, accent.G, accent.B)),
                Child = receiptRow
            };
        }

        var rootStack = new StackPanel { Spacing = 10 };
        var states = new List<QuestionSelectionState>(message.ClarifyQuestions.Count);
        bool isBusy = shell.Session.IsAnyStudioJobRunning;
        s_draftSelectionsByMessageId.TryGetValue(message.Id, out List<DraftQuestionSnapshot>? savedSnapshots);

        void PersistDraftSnapshot()
        {
            if (string.IsNullOrWhiteSpace(message.Id))
            {
                return;
            }

            if (s_draftSelectionsByMessageId.Count >= 64 && !s_draftSelectionsByMessageId.ContainsKey(message.Id))
            {
                s_draftSelectionsByMessageId.Clear();
            }

            var snapshots = new List<DraftQuestionSnapshot>(states.Count);
            foreach (QuestionSelectionState s in states)
            {
                var snap = new DraftQuestionSnapshot
                {
                    CustomInputActive = s.CustomInputActive,
                    CustomText = s.CustomText
                };
                foreach (int sel in s.SelectedOptionIndices)
                {
                    snap.SelectedOptionIndices.Add(sel);
                }
                snapshots.Add(snap);
            }

            s_draftSelectionsByMessageId[message.Id] = snapshots;
        }

        int questionOrdinal = 0;
        foreach (StudioClarifyQuestionSpec q in message.ClarifyQuestions)
        {
            if (q.Options.Count == 0)
            {
                continue;
            }

            var qState = new QuestionSelectionState { Spec = q };
            DraftQuestionSnapshot? restored = savedSnapshots is not null && questionOrdinal < savedSnapshots.Count
                ? savedSnapshots[questionOrdinal]
                : null;
            questionOrdinal++;

            if (restored is not null)
            {
                qState.CustomInputActive = restored.CustomInputActive;
                qState.CustomText = restored.CustomText;
                foreach (int idx in restored.SelectedOptionIndices)
                {
                    if (idx >= 0 && idx < q.Options.Count)
                    {
                        qState.SelectedOptionIndices.Add(idx);
                    }
                }
            }
            else
            {
                for (int i = 0; i < q.Options.Count; i++)
                {
                    if (q.Options[i].Recommended)
                    {
                        qState.SelectedOptionIndices.Add(i);
                        if (!q.MultiSelect)
                        {
                            break;
                        }
                    }
                }
            }
            if (qState.SelectedOptionIndices.Count == 0 && !qState.CustomInputActive)
            {
                qState.SelectedOptionIndices.Add(0);
            }

            states.Add(qState);

            var groupStack = new StackPanel { Spacing = 6 };
            var headerRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };
            headerRow.Children.Add(new TextBlock
            {
                Text = q.Header,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = StudioUiPrimitives.ThemeBrush(shell, "TextFillColorPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
            if (q.MultiSelect)
            {
                headerRow.Children.Add(new TextBlock
                {
                    Text = loc.T("CustomWidget.Workshop.Clarify.MultiSelectHint"),
                    FontSize = 11,
                    Foreground = StudioUiPrimitives.ThemeBrush(shell, "TextFillColorTertiaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            groupStack.Children.Add(headerRow);

            var optionsWrap = new CustomWidgetWrapPanel
            {
                HorizontalSpacing = 6,
                VerticalSpacing = 6
            };

            string customLabel = loc.T("CustomWidget.Workshop.Clarify.CustomButton");
            void RefreshQuestionVisuals()
            {
                for (int idx = 0; idx < qState.OptionButtons.Count; idx++)
                {
                    bool selected = qState.SelectedOptionIndices.Contains(idx) && (!qState.CustomInputActive || q.MultiSelect);
                    StudioClarifyOptionSpec optSpec = q.Options[idx];
                    string displayLabel = optSpec.Recommended
                        ? loc.Format("CustomWidget.Workshop.Clarify.RecommendedOptionFormat", optSpec.Label)
                        : optSpec.Label;
                    ApplyPillSelectionChrome(shell, qState.OptionButtons[idx], displayLabel, selected, accent, isDark);
                }

                if (qState.CustomToggleButton is not null)
                {
                    ApplyPillSelectionChrome(shell, qState.CustomToggleButton, customLabel, qState.CustomInputActive, accent, isDark);
                }

                if (qState.CustomTextBox is not null)
                {
                    qState.CustomTextBox.Visibility = qState.CustomInputActive
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
            }

            for (int i = 0; i < q.Options.Count; i++)
            {
                int capturedIdx = i;
                var optBtn = new Button
                {
                    MinHeight = 28,
                    Padding = new Thickness(10, 4, 10, 4),
                    CornerRadius = new CornerRadius(6),
                    FontSize = 12,
                    IsEnabled = !isBusy
                };
                AutomationProperties.SetName(optBtn, q.Options[i].Label);
                optBtn.Click += (_, _) =>
                {
                    if (q.MultiSelect)
                    {
                        if (!qState.SelectedOptionIndices.Remove(capturedIdx))
                        {
                            qState.SelectedOptionIndices.Add(capturedIdx);
                        }
                    }
                    else
                    {
                        qState.CustomInputActive = false;
                        qState.SelectedOptionIndices.Clear();
                        qState.SelectedOptionIndices.Add(capturedIdx);
                    }

                    PersistDraftSnapshot();
                    RefreshQuestionVisuals();
                };

                qState.OptionButtons.Add(optBtn);
                optionsWrap.Children.Add(optBtn);
            }

            if (q.AllowCustomInput)
            {
                var customBtn = new Button
                {
                    MinHeight = 28,
                    Padding = new Thickness(10, 4, 10, 4),
                    CornerRadius = new CornerRadius(6),
                    FontSize = 12,
                    IsEnabled = !isBusy
                };
                AutomationProperties.SetName(customBtn, customLabel);

                var customBox = new TextBox
                {
                    Text = qState.CustomText,
                    PlaceholderText = !string.IsNullOrWhiteSpace(q.Placeholder)
                        ? q.Placeholder!
                        : loc.T("CustomWidget.Workshop.Clarify.CustomPlaceholder"),
                    FontSize = 12,
                    MinHeight = 30,
                    Padding = new Thickness(9, 5, 9, 5),
                    CornerRadius = new CornerRadius(6),
                    Visibility = Visibility.Collapsed,
                    IsEnabled = !isBusy
                };
                customBox.TextChanged += (_, _) =>
                {
                    qState.CustomText = customBox.Text?.Trim() ?? string.Empty;
                    PersistDraftSnapshot();
                };

                customBtn.Click += (_, _) =>
                {
                    qState.CustomInputActive = !qState.CustomInputActive;
                    if (qState.CustomInputActive && !q.MultiSelect)
                    {
                        qState.SelectedOptionIndices.Clear();
                    }
                    else if (!qState.CustomInputActive && qState.SelectedOptionIndices.Count == 0)
                    {
                        qState.SelectedOptionIndices.Add(0);
                    }

                    PersistDraftSnapshot();
                    RefreshQuestionVisuals();
                    if (qState.CustomInputActive)
                    {
                        customBox.Focus(FocusState.Programmatic);
                    }
                };

                qState.CustomToggleButton = customBtn;
                qState.CustomTextBox = customBox;
                optionsWrap.Children.Add(customBtn);
                groupStack.Children.Add(optionsWrap);
                groupStack.Children.Add(customBox);
            }
            else
            {
                groupStack.Children.Add(optionsWrap);
            }

            RefreshQuestionVisuals();
            rootStack.Children.Add(groupStack);
        }

        // Footer bar: hint + primary "Build with Selected Plan" button
        var footerGrid = new Grid
        {
            ColumnSpacing = 10,
            Margin = new Thickness(0, 2, 0, 0)
        };
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var footerHint = new TextBlock
        {
            Text = loc.T("CustomWidget.Workshop.Clarify.PreselectedHint"),
            FontSize = 11.5,
            Foreground = StudioUiPrimitives.ThemeBrush(shell, "TextFillColorTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        footerGrid.Children.Add(footerHint);

        string submitText = loc.T("CustomWidget.Workshop.Clarify.SubmitPlanButton");
        var submitBtn = new Button
        {
            Height = 30,
            Padding = new Thickness(13, 0, 13, 0),
            CornerRadius = new CornerRadius(6),
            Style = StudioUiPrimitives.AccentButtonStyle(),
            IsEnabled = !isBusy,
            Content = StudioUiPrimitives.CreateSmallIconLabel("\uE945", submitText)
        };
        AutomationProperties.SetName(submitBtn, submitText);

        submitBtn.Click += (_, _) => _ = StudioUiPrimitives.SafeInvokeAsync(shell, "SubmitClarifyPlan", async () =>
        {
            if (shell.Session.IsAnyStudioJobRunning)
            {
                return;
            }

            (string composedPrompt, string receiptSummary) = ComposeConfirmedPlanPrompt(states, cultureName, loc);
            s_draftSelectionsByMessageId.Remove(message.Id);
            MarkClarifyMessageSubmitted(shell, message.Id, receiptSummary);
            shell.ChatPanel.RefreshInPlaceGenerationChrome();
            await shell.Generation.BeginGenerationAsync(composedPrompt);
        });

        Grid.SetColumn(submitBtn, 1);
        footerGrid.Children.Add(submitBtn);
        rootStack.Children.Add(footerGrid);

        return new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(isDark ? 92 : 76), accent.R, accent.G, accent.B)),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(isDark ? 24 : 16), accent.R, accent.G, accent.B)),
            Child = rootStack
        };
    }

    private static void ApplyPillSelectionChrome(
        CustomWidgetStudioWindow shell,
        Button btn,
        string labelText,
        bool isSelected,
        Windows.UI.Color accent,
        bool isDark)
    {
        if (isSelected)
        {
            btn.Background = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(isDark ? 56 : 36), accent.R, accent.G, accent.B));
            btn.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(isDark ? 175 : 150), accent.R, accent.G, accent.B));
            btn.BorderThickness = new Thickness(1);
            Brush fg = StudioUiPrimitives.ThemeBrush(shell, "AccentTextFillColorPrimaryBrush");
            btn.Foreground = fg;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            row.Children.Add(new FontIcon
            {
                Glyph = "\uE73E",
                FontSize = 10.5,
                Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(new TextBlock
            {
                Text = labelText,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center
            });
            btn.Content = row;
        }
        else
        {
            btn.Background = StudioUiPrimitives.ThemeBrush(shell, "SubtleFillColorSecondaryBrush");
            btn.BorderBrush = StudioUiPrimitives.ThemeBrush(shell, "CardStrokeColorDefaultBrush");
            btn.BorderThickness = new Thickness(1);
            Brush fg = StudioUiPrimitives.ThemeBrush(shell, "TextFillColorPrimaryBrush");
            btn.Foreground = fg;
            btn.Content = new TextBlock
            {
                Text = labelText,
                FontSize = 12,
                Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
    }

    internal static (string Prompt, string ReceiptSummary) ComposeConfirmedPlanPrompt(
        IReadOnlyList<QuestionSelectionState> states,
        bool isChinese)
        => ComposeConfirmedPlanPrompt(states, isChinese ? "zh-CN" : "en-US", loc: null);

    internal static (string Prompt, string ReceiptSummary) ComposeConfirmedPlanPrompt(
        IReadOnlyList<QuestionSelectionState> states,
        string cultureName,
        LocalizationService? loc)
    {
        bool useFullWidthPunct = cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ||
                                 cultureName.StartsWith("ja", StringComparison.OrdinalIgnoreCase);
        string sep = useFullWidthPunct ? "、" : ", ";
        string colon = useFullWidthPunct ? "：" : ": ";

        var promptSb = new StringBuilder(256);
        promptSb.AppendLine(TranslateClarify(loc, cultureName, "CustomWidget.Workshop.Clarify.ConfirmedPromptHeader"));

        var summaryParts = new List<string>(states.Count);
        foreach (QuestionSelectionState st in states)
        {
            var chosenLabels = new List<string>(3);
            foreach (int idx in st.SelectedOptionIndices.OrderBy(static x => x))
            {
                if (idx >= 0 && idx < st.Spec.Options.Count)
                {
                    StudioClarifyOptionSpec opt = st.Spec.Options[idx];
                    string readable = !string.IsNullOrWhiteSpace(opt.Label) ? opt.Label.Trim() : opt.Value.Trim();
                    if (!string.IsNullOrEmpty(readable))
                    {
                        chosenLabels.Add(readable);
                    }
                }
            }

            if (st.CustomInputActive && !string.IsNullOrWhiteSpace(st.CustomText))
            {
                chosenLabels.Add(st.CustomText);
            }

            if (chosenLabels.Count == 0 && st.Spec.Options.Count > 0)
            {
                StudioClarifyOptionSpec first = st.Spec.Options[0];
                chosenLabels.Add(!string.IsNullOrWhiteSpace(first.Label) ? first.Label.Trim() : first.Value.Trim());
            }

            if (chosenLabels.Count > 0)
            {
                string joined = string.Join(sep, chosenLabels);
                promptSb.Append("- ").Append(st.Spec.Header).Append(colon).AppendLine(joined);
                summaryParts.Add($"{st.Spec.Header}: {joined}");
            }
        }

        string summaryBody = string.Join("  |  ", summaryParts);
        string receipt = FormatClarify(loc, cultureName, "CustomWidget.Workshop.Clarify.ConfirmedSummaryFormat", summaryBody);
        return (promptSb.ToString().Trim(), receipt);
    }

    private static string BuildDefaultSummaryLine(
        IReadOnlyList<StudioClarifyQuestionSpec> questions,
        string cultureName,
        LocalizationService? loc)
    {
        var parts = new List<string>(questions.Count);
        foreach (StudioClarifyQuestionSpec q in questions)
        {
            StudioClarifyOptionSpec? rec = q.Options.FirstOrDefault(static o => o.Recommended) ?? q.Options.FirstOrDefault();
            if (rec is not null)
            {
                parts.Add($"{q.Header}: {rec.Label}");
            }
        }

        return FormatClarify(
            loc,
            cultureName,
            "CustomWidget.Workshop.Clarify.ConfirmedSummaryFormat",
            string.Join("  |  ", parts));
    }

    private static string TranslateClarify(LocalizationService? loc, string cultureName, string key) =>
        loc is not null
            ? loc.T(key)
            : LocalizationService.TranslateForCulture(cultureName, key);

    private static string FormatClarify(LocalizationService? loc, string cultureName, string key, object arg0) =>
        loc is not null
            ? loc.Format(key, arg0)
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, LocalizationService.TranslateForCulture(cultureName, key), arg0);

    private static void MarkClarifyMessageSubmitted(
        CustomWidgetStudioWindow shell,
        string messageId,
        string submittedSummary)
    {
        List<StudioChatMessage> list = shell.Session.ChatMessages;
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].Id, messageId, StringComparison.Ordinal))
            {
                list[i] = list[i] with { SubmittedSummary = submittedSummary };
                shell.Session.PersistActiveDraftIfDirty();
                break;
            }
        }
    }
}
