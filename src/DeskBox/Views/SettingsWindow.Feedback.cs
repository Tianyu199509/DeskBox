using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DeskBox.Views;

public sealed partial class SettingsWindow
{
    private const string FeedbackWallUrl = "https://deskbox.fun/feedback/";
    private const string FeedbackTempDirectoryName = "DeskBoxFeedback";

    private FeedbackService? _feedbackService;
    private bool _feedbackDialogShowing;

    private FeedbackService FeedbackService => _feedbackService ??= new FeedbackService(
        localeProvider: () => _localizationService.CurrentCultureName);

    private async void ShowFeedbackDialogButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null || _feedbackDialogShowing)
        {
            return;
        }

        _feedbackDialogShowing = true;
        try
        {
            await ShowFeedbackDialogAsync();
        }
        finally
        {
            _feedbackDialogShowing = false;
        }
    }

    private async Task ShowFeedbackDialogAsync()
    {
        string T(string key) => _localizationService.T(key);
        string F(string key, object arg) => _localizationService.Format(key, arg);

        var kind = DeskBoxFeedbackKind.Suggestion;
        bool diagnosticsTouched = false;

        var noticeText = new TextBlock
        {
            Text = T("Feedback.Dialog.AnonymousNotice"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.8,
        };
        var noticeBorder = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Background = TryThemeBrush("CardBackgroundFillColorSecondaryBrush"),
            BorderBrush = TryThemeBrush("CardStrokeColorDefaultBrush"),
            Child = noticeText,
        };

        var diagnosticsCheckBox = new CheckBox
        {
            Content = T("Feedback.Dialog.AttachDiagnostics"),
            IsChecked = false,
            // The dialog opens on the Suggestion kind, which never offers diagnostics.
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 10, 0, 0),
        };
        diagnosticsCheckBox.Click += (_, _) => diagnosticsTouched = true;

        var suggestionRadio = new RadioButton
        {
            GroupName = "FeedbackKind",
            Content = T("Feedback.Dialog.TypeSuggestion"),
            IsChecked = true,
        };
        var bugRadio = new RadioButton
        {
            GroupName = "FeedbackKind",
            Content = T("Feedback.Dialog.TypeBug"),
        };
        suggestionRadio.Checked += (_, _) =>
        {
            kind = DeskBoxFeedbackKind.Suggestion;
            // Diagnostics only apply to bug reports; hide the option entirely.
            diagnosticsCheckBox.Visibility = Visibility.Collapsed;
            diagnosticsCheckBox.IsChecked = false;
        };
        bugRadio.Checked += (_, _) =>
        {
            kind = DeskBoxFeedbackKind.Bug;
            diagnosticsCheckBox.Visibility = Visibility.Visible;
            if (!diagnosticsTouched) diagnosticsCheckBox.IsChecked = true;
        };

        var contentBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxLength = 2000,
            MinHeight = 120,
            PlaceholderText = T("Feedback.Dialog.ContentPlaceholder"),
            Margin = new Thickness(0, 12, 0, 0),
        };
        var counterText = new TextBlock
        {
            Text = _localizationService.Format("Feedback.Dialog.CharCount", 0),
            FontSize = 11,
            Opacity = 0.7,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0),
        };
        var tooShortText = new TextBlock
        {
            Text = T("Feedback.Dialog.ContentTooShort"),
            FontSize = 11,
            Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 12, 0),
        };
        var wallLink = new HyperlinkButton
        {
            Content = T("Feedback.Dialog.ViewWall"),
            NavigateUri = new Uri(FeedbackWallUrl),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = 12,
        };

        var errorText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0,
            Margin = new Thickness(0, 10, 0, 0),
        };

        var formPanel = new StackPanel { Spacing = 0 };
        formPanel.Children.Add(noticeBorder);
        formPanel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { suggestionRadio, bugRadio },
        });
        formPanel.Children.Add(contentBox);
        formPanel.Children.Add(new Grid { Children = { tooShortText, counterText } });
        formPanel.Children.Add(diagnosticsCheckBox);
        formPanel.Children.Add(wallLink);
        // errorText intentionally lives OUTSIDE formPanel: once a submission
        // succeeds the form is collapsed, and a rate-limit or network error
        // surfaced afterwards would otherwise be invisible (feedback #115).

        var successIdText = new TextBlock
        {
            Text = string.Empty,
            FontSize = 26,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var successHint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var successWallLink = new HyperlinkButton
        {
            Content = T("Feedback.Dialog.ViewWall"),
            NavigateUri = new Uri(FeedbackWallUrl),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var diagnosticsNote = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.75,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        var resultPanel = new StackPanel { Visibility = Visibility.Collapsed };
        resultPanel.Children.Add(successIdText);
        resultPanel.Children.Add(successHint);
        resultPanel.Children.Add(successWallLink);
        resultPanel.Children.Add(diagnosticsNote);

        var submitAnotherButton = new Button
        {
            Content = T("Feedback.Dialog.SubmitAnother"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
        };
        resultPanel.Children.Add(submitAnotherButton);

        var contentGrid = new Grid();
        contentGrid.Children.Add(formPanel);
        contentGrid.Children.Add(resultPanel);

        var dialogContent = new StackPanel { Spacing = 0 };
        dialogContent.Children.Add(contentGrid);
        dialogContent.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = T("Feedback.Dialog.Title"),
            Content = dialogContent,
            PrimaryButtonText = T("Feedback.Dialog.Submit"),
            CloseButtonText = T("Common.Close"),
            DefaultButton = ContentDialogButton.Primary,
        };

        // Rate limiting outlives a single validation pass: editing the text
        // must not re-enable submit while the server window is still open.
        bool rateLimitedPrimaryDisabled = false;

        void RefreshValidation()
        {
            int length = contentBox.Text.Trim().Length;
            counterText.Text = _localizationService.Format("Feedback.Dialog.CharCount", length);
            tooShortText.Opacity = length is > 0 and < 10 ? 1 : 0;
            dialog.IsPrimaryButtonEnabled = !rateLimitedPrimaryDisabled && length is >= 10 and <= 2000;
        }

        void RestoreFormControls()
        {
            dialog.PrimaryButtonText = T("Feedback.Dialog.Submit");
            suggestionRadio.IsEnabled = true;
            bugRadio.IsEnabled = true;
            contentBox.IsEnabled = true;
            diagnosticsCheckBox.IsEnabled = true;
            RefreshValidation();
        }

        void ShowSubmitError(string message, bool disablePrimary)
        {
            rateLimitedPrimaryDisabled = disablePrimary;
            errorText.Text = message;
            errorText.Opacity = 1;
            RestoreFormControls();
            if (disablePrimary)
            {
                // Retrying inside the rate-limit window can only fail again;
                // keep the button off so pressing it never looks silently
                // ignored. The text stays editable for when the user returns.
                dialog.IsPrimaryButtonEnabled = false;
            }
        }

        contentBox.TextChanged += (_, _) => RefreshValidation();

        submitAnotherButton.Click += (_, _) =>
        {
            rateLimitedPrimaryDisabled = false;
            errorText.Opacity = 0;
            formPanel.Visibility = Visibility.Visible;
            resultPanel.Visibility = Visibility.Collapsed;
            contentBox.Text = string.Empty;
            // Re-selecting Suggestion also resets the kind and hides the
            // diagnostics checkbox through its Checked handler.
            suggestionRadio.IsChecked = true;
            kind = DeskBoxFeedbackKind.Suggestion;
            // Success collapsed the primary button (empty text) and left the
            // inputs disabled — RestoreFormControls undoes both, otherwise
            // the "submit another" form comes back dead.
            RestoreFormControls();
        };

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            ContentDialogButtonClickDeferral deferral = args.GetDeferral();
            try
            {
                dialog.IsPrimaryButtonEnabled = false;
                suggestionRadio.IsEnabled = false;
                bugRadio.IsEnabled = false;
                contentBox.IsEnabled = false;
                diagnosticsCheckBox.IsEnabled = false;
                dialog.PrimaryButtonText = T("Feedback.Dialog.Submitting");
                errorText.Opacity = 0;

                string originalText = contentBox.Text;
                bool attachDiagnostics = diagnosticsCheckBox.IsChecked == true;
                DeskBoxFeedbackSubmissionResult result = await FeedbackService.SubmitAsync(
                    kind,
                    originalText,
                    attachDiagnostics);

                if (result.Ok && result.Id is long feedbackId)
                {
                    bool diagnosticsUploaded = true;
                    if (attachDiagnostics)
                    {
                        dialog.PrimaryButtonText = T("Feedback.Dialog.UploadingDiagnostics");
                        diagnosticsUploaded = await TryUploadDiagnosticsAsync(feedbackId);
                    }

                    successIdText.Text = F("Feedback.Dialog.SuccessId", feedbackId);
                    successHint.Text = T("Feedback.Dialog.SuccessHint");
                    diagnosticsNote.Text = T("Feedback.Dialog.DiagnosticsFailed");
                    diagnosticsNote.Visibility = attachDiagnostics && !diagnosticsUploaded
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    formPanel.Visibility = Visibility.Collapsed;
                    resultPanel.Visibility = Visibility.Visible;
                    // Empty button text collapses the primary button in ContentDialog.
                    dialog.PrimaryButtonText = string.Empty;
                }
                else if (result.RetryAfterMinutes is int minutes && minutes > 0)
                {
                    ShowSubmitError(F("Feedback.Dialog.RateLimited", minutes), disablePrimary: true);
                }
                else
                {
                    ShowSubmitError(T("Feedback.Dialog.NetworkError"), disablePrimary: false);
                }
            }
            catch (Exception ex)
            {
                // Last-resort guard: any unexpected failure must still land on
                // a visible error and complete the deferral, never leave the
                // dialog stuck on "Submitting…" (feedback #115).
                App.Log($"[Feedback] Submit handler failed unexpectedly: {ex}");
                ShowSubmitError(T("Feedback.Dialog.NetworkError"), disablePrimary: false);
            }
            finally
            {
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();
    }

    private async Task<bool> TryUploadDiagnosticsAsync(long feedbackId)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), FeedbackTempDirectoryName);
        try
        {
            return await _appActions.UploadDiagnosticsForFeedbackAsync(feedbackId, tempDirectory);
        }
        catch (Exception ex)
        {
            App.Log($"[Feedback] Diagnostics export for upload failed: {ex.Message}");
            return false;
        }
    }

    private async void ShowMyFeedbackButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null || _feedbackDialogShowing)
        {
            return;
        }

        _feedbackDialogShowing = true;
        try
        {
            await ShowMyFeedbackDialogAsync();
        }
        finally
        {
            _feedbackDialogShowing = false;
        }
    }

    private async Task ShowMyFeedbackDialogAsync()
    {
        string T(string key) => _localizationService.T(key);

        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = T("Feedback.My.Title"),
            CloseButtonText = T("Common.Close"),
            MinWidth = 520,
        };

        async Task LoadAsync()
        {
            dialog.Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new ProgressRing
                    {
                        IsActive = true,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Width = 32,
                        Height = 32,
                    },
                },
            };

            DeskBoxFeedbackMineResult result = await FeedbackService.GetMyFeedbackAsync();
            if (!result.Ok)
            {
                var retryButton = new Button { Content = T("Feedback.My.Retry") };
                retryButton.Click += (_, _) => _ = LoadAsync();
                dialog.Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = T("Feedback.My.LoadFailed"), TextWrapping = TextWrapping.Wrap },
                        retryButton,
                    },
                };
                return;
            }

            if (result.Items.Count == 0)
            {
                dialog.Content = new TextBlock
                {
                    Text = T("Feedback.My.Empty"),
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.8,
                };
                return;
            }

            var listPanel = new StackPanel { Spacing = 12, Padding = new Thickness(0, 0, 8, 0) };
            foreach (DeskBoxFeedbackItem item in result.Items)
            {
                listPanel.Children.Add(BuildMyFeedbackCard(item));
            }

            dialog.Content = new ScrollViewer
            {
                Content = listPanel,
                MaxHeight = 460,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
        }

        _ = LoadAsync();
        await dialog.ShowAsync();
    }

    private UIElement BuildMyFeedbackCard(DeskBoxFeedbackItem item)
    {
        string T(string key) => _localizationService.T(key);
        string F(string key, object arg) => _localizationService.Format(key, arg);

        string typeLabel = item.Type == "bug" ? T("Feedback.Dialog.TypeBug") : T("Feedback.Dialog.TypeSuggestion");
        var metaText = new TextBlock
        {
            Text = $"#{item.Id} · {typeLabel} · {StatusText(item.Status)} · {item.CreatedAt.ToLocalTime():d}"
                + (string.IsNullOrEmpty(item.AppVersion) ? string.Empty : $" · v{item.AppVersion}"),
            FontSize = 11.5,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        };
        var contentText = new TextBlock
        {
            Text = item.Content,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };

        var card = new StackPanel { Spacing = 0 };
        card.Children.Add(metaText);
        card.Children.Add(contentText);

        var messagesPanel = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(0, 10, 0, 0),
        };

        if (item.Messages is { Count: > 0 })
        {
            foreach (DeskBoxFeedbackMessage message in item.Messages)
            {
                messagesPanel.Children.Add(BuildFeedbackMessageBubble(message));
            }
        }
        else if (!string.IsNullOrEmpty(item.Reply))
        {
            messagesPanel.Children.Add(BuildFeedbackMessageBubble(
                new DeskBoxFeedbackMessage(0, "official", null, item.Reply, item.CreatedAt),
                showTimestamp: false));
        }

        messagesPanel.Visibility = messagesPanel.Children.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        card.Children.Add(messagesPanel);

        var addReplyButton = new Button
        {
            Content = T("Feedback.My.AddReply"),
            FontSize = 12,
            Padding = new Thickness(10, 4, 10, 5),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };

        var replyBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxLength = 2000,
            MinHeight = 68,
            PlaceholderText = T("Feedback.My.ReplyPlaceholder"),
        };

        var replyErrorText = new TextBlock
        {
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.9,
            Visibility = Visibility.Collapsed,
        };

        var cancelReplyButton = new Button
        {
            Content = T("Common.Cancel"),
            FontSize = 12,
            Padding = new Thickness(10, 4, 10, 5),
        };

        var sendReplyButton = new Button
        {
            Content = T("Feedback.My.SendReply"),
            FontSize = 12,
            Padding = new Thickness(12, 4, 12, 5),
            IsEnabled = false,
        };
        if (Application.Current?.Resources.TryGetValue("AccentButtonStyle", out object accentStyle) == true &&
            accentStyle is Style buttonStyle)
        {
            sendReplyButton.Style = buttonStyle;
        }

        var composerPanel = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        composerPanel.Children.Add(replyBox);
        composerPanel.Children.Add(replyErrorText);
        composerPanel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancelReplyButton, sendReplyButton },
        });

        bool sendingReply = false;
        void RefreshReplyButtonState()
        {
            int length = replyBox.Text.Trim().Length;
            sendReplyButton.IsEnabled = !sendingReply && length is >= 2 and <= 2000;
        }

        replyBox.TextChanged += (_, _) =>
        {
            if (replyErrorText.Visibility == Visibility.Visible)
            {
                replyErrorText.Visibility = Visibility.Collapsed;
            }

            RefreshReplyButtonState();
        };

        addReplyButton.Click += (_, _) =>
        {
            addReplyButton.Visibility = Visibility.Collapsed;
            composerPanel.Visibility = Visibility.Visible;
            replyBox.Focus(FocusState.Programmatic);
        };

        cancelReplyButton.Click += (_, _) =>
        {
            replyErrorText.Visibility = Visibility.Collapsed;
            composerPanel.Visibility = Visibility.Collapsed;
            addReplyButton.Visibility = Visibility.Visible;
        };

        sendReplyButton.Click += async (_, _) =>
        {
            if (sendingReply)
            {
                return;
            }

            sendingReply = true;
            replyBox.IsEnabled = false;
            cancelReplyButton.IsEnabled = false;
            sendReplyButton.IsEnabled = false;
            replyErrorText.Visibility = Visibility.Collapsed;

            try
            {
                DeskBoxFeedbackReplyResult replyResult = await FeedbackService.PostReplyAsync(
                    item.Id,
                    replyBox.Text);
                if (replyResult.Ok && replyResult.Message is { } createdMessage)
                {
                    messagesPanel.Children.Add(BuildFeedbackMessageBubble(createdMessage));
                    messagesPanel.Visibility = Visibility.Visible;
                    replyBox.Text = string.Empty;
                    composerPanel.Visibility = Visibility.Collapsed;
                    addReplyButton.Visibility = Visibility.Visible;
                }
                else if (replyResult.RetryAfterMinutes is int minutes && minutes > 0)
                {
                    replyErrorText.Text = F("Feedback.My.ReplyRateLimited", minutes);
                    replyErrorText.Visibility = Visibility.Visible;
                }
                else if (string.Equals(replyResult.Error, "content_too_short", StringComparison.Ordinal))
                {
                    replyErrorText.Text = T("Feedback.My.ReplyTooShort");
                    replyErrorText.Visibility = Visibility.Visible;
                }
                else
                {
                    replyErrorText.Text = T("Feedback.Dialog.NetworkError");
                    replyErrorText.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                App.Log($"[Feedback] Reply submit failed unexpectedly: {ex}");
                replyErrorText.Text = T("Feedback.Dialog.NetworkError");
                replyErrorText.Visibility = Visibility.Visible;
            }
            finally
            {
                sendingReply = false;
                replyBox.IsEnabled = true;
                cancelReplyButton.IsEnabled = true;
                RefreshReplyButtonState();
            }
        };

        card.Children.Add(addReplyButton);
        card.Children.Add(composerPanel);

        return new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = TryThemeBrush("CardBackgroundFillColorDefaultBrush")
                ?? TryThemeBrush("CardBackgroundFillColorSecondaryBrush"),
            BorderBrush = TryThemeBrush("CardStrokeColorDefaultBrush"),
            Child = card,
        };
    }

    private Border BuildFeedbackMessageBubble(DeskBoxFeedbackMessage message, bool showTimestamp = true)
    {
        string T(string key) => _localizationService.T(key);

        bool isOfficial = string.Equals(message.AuthorRole, "official", StringComparison.OrdinalIgnoreCase);
        bool isOp = string.Equals(message.AuthorRole, "op", StringComparison.OrdinalIgnoreCase);
        string authorLabel = isOfficial
            ? T("Feedback.My.ReplyLabel")
            : isOp
                ? T("Feedback.My.MyReplyLabel")
                : !string.IsNullOrWhiteSpace(message.AuthorNickname)
                    ? message.AuthorNickname!
                    : T("Feedback.My.CommunityReplyLabel");

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var roleText = new TextBlock
        {
            Text = authorLabel,
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            TextWrapping = TextWrapping.NoWrap,
        };
        if (isOfficial)
        {
            roleText.Foreground = TryAccentBrush();
        }
        else
        {
            roleText.Opacity = 0.85;
        }

        headerGrid.Children.Add(roleText);

        if (showTimestamp && message.CreatedAt.Year > 2000)
        {
            var timeText = new TextBlock
            {
                Text = message.CreatedAt.ToLocalTime().ToString("g"),
                FontSize = 10.5,
                Opacity = 0.6,
                Margin = new Thickness(8, 0, 0, 0),
            };
            Grid.SetColumn(timeText, 1);
            headerGrid.Children.Add(timeText);
        }

        var bodyText = new TextBlock
        {
            Text = message.Content,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            Margin = new Thickness(0, 3, 0, 0),
        };

        var bubbleStack = new StackPanel();
        bubbleStack.Children.Add(headerGrid);
        bubbleStack.Children.Add(bodyText);

        return new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Background = TryThemeBrush("CardBackgroundFillColorSecondaryBrush"),
            BorderBrush = TryThemeBrush("CardStrokeColorDefaultBrush"),
            Child = bubbleStack,
        };
    }

    private string StatusText(string status)
    {
        return status switch
        {
            "open" => _localizationService.T("Feedback.Status.Open"),
            "confirmed" => _localizationService.T("Feedback.Status.Confirmed"),
            "in_progress" => _localizationService.T("Feedback.Status.InProgress"),
            "done" => _localizationService.T("Feedback.Status.Done"),
            "wontfix" => _localizationService.T("Feedback.Status.Wontfix"),
            _ => status,
        };
    }

    private static Brush? TryAccentBrush()
    {
        if (Application.Current?.Resources.TryGetValue("AccentTextFillColorPrimaryBrush", out object brush) == true)
        {
            return brush as Brush;
        }

        return null;
    }

    // Same lookups as the nested snapshot rows in SettingsWindow.xaml: the
    // notice/reply borders were declared with thickness+radius but no brushes,
    // so they never actually rendered a box.
    private static Brush? TryThemeBrush(string key)
    {
        if (Application.Current?.Resources.TryGetValue(key, out object brush) == true)
        {
            return brush as Brush;
        }

        return null;
    }
}
