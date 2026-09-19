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
            DeskBoxDiagnosticSnapshot snapshot = App.Current.CreateDiagnosticSnapshot();
            string archivePath = await App.Current.DiagnosticsBundleService.ExportAsync(
                tempDirectory,
                snapshot,
                DeskBoxDataPathService.Current.LogFilePath);
            try
            {
                DeskBoxFeedbackDiagnosticsStatus status = await FeedbackService.UploadDiagnosticsAsync(
                    feedbackId,
                    archivePath);
                return status == DeskBoxFeedbackDiagnosticsStatus.Uploaded;
            }
            finally
            {
                try { File.Delete(archivePath); } catch (Exception ex) { App.Log($"[Feedback] Temp diagnostics cleanup failed: {ex.Message}"); }
            }
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
            MinWidth = 480,
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

            var listPanel = new StackPanel { Spacing = 12 };
            foreach (DeskBoxFeedbackItem item in result.Items)
            {
                listPanel.Children.Add(BuildMyFeedbackCard(item));
            }

            dialog.Content = new ScrollViewer
            {
                Content = listPanel,
                MaxHeight = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
        }

        _ = LoadAsync();
        await dialog.ShowAsync();
    }

    private StackPanel BuildMyFeedbackCard(DeskBoxFeedbackItem item)
    {
        string T(string key) => _localizationService.T(key);

        string typeLabel = item.Type == "bug" ? T("Feedback.Dialog.TypeBug") : T("Feedback.Dialog.TypeSuggestion");
        var metaText = new TextBlock
        {
            Text = $"#{item.Id} · {typeLabel} · {StatusText(item.Status)} · {item.CreatedAt.ToLocalTime().ToString("d")}"
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

        var card = new StackPanel();
        card.Children.Add(metaText);
        card.Children.Add(contentText);

        if (!string.IsNullOrEmpty(item.Reply))
        {
            var replyLabel = new TextBlock
            {
                Text = T("Feedback.My.ReplyLabel"),
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = TryAccentBrush(),
                Margin = new Thickness(0, 10, 0, 0),
            };
            var replyText = new TextBlock
            {
                Text = item.Reply,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
            };
            var replyBorder = new Border
            {
                Padding = new Thickness(10, 8, 10, 8),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 4, 0, 0),
                Child = new StackPanel(),
            };
            ((StackPanel)replyBorder.Child).Children.Add(replyLabel);
            ((StackPanel)replyBorder.Child).Children.Add(replyText);
            card.Children.Add(replyBorder);
        }

        return card;
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
}
