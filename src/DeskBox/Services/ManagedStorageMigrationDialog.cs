using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;

namespace DeskBox.Services;

internal enum StorageMigrationStartChoice { Start, Restart, ChooseLocation }
internal sealed record StorageMigrationDialogResult(ManagedStorageMigrationResult? Migration, bool ChooseLocation);

/// <summary>Stable-sized migration UI; recovery actions describe the actual filesystem state.</summary>
internal sealed class ManagedStorageMigrationDialog
{
    private readonly LocalizationService _localization;
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<ManagedStorageMigrationOptions, Task<ManagedStorageMigrationResult>> _migrate;
    private readonly ContentDialog _dialog;
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, IsIndeterminate = true };
    private readonly TextBlock _phase = Text();
    private readonly TextBlock _percent = new() { Width = 52, TextAlignment = TextAlignment.Right };
    private readonly TextBlock _file = SingleLine(22);
    private readonly TextBlock _directory = SingleLine(20);
    private readonly TextBlock _activity = new() { Height = 20, FontSize = 12, Opacity = 0.75 };
    private readonly TextBlock _detail = Text();
    private readonly TextBlock _technical = Text();
    private readonly Expander _details;
    private readonly StackPanel _progressView;
    private readonly StackPanel _resultView;
    private readonly Button _primary;
    private readonly Button _secondary;
    private readonly Button _openSource;
    private readonly Button _openTarget;
    private CancellationTokenSource _cancel = new();
    private bool _running;
    private bool _committing;
    private bool _closed;
    private bool _restart;
    private bool _pickAnother;
    private bool _primaryChoosesLocation;
    private int _attempt;
    private ManagedStorageMigrationResult? _result;

    internal static async Task<StorageMigrationStartChoice?> ConfirmAsync(
        XamlRoot root, LocalizationService localization, int count, string oldPath, string newPath,
        Func<CancellationToken, Task<ManagedStorageMigrationPreview>> preview)
    {
        using var cancel = new CancellationTokenSource();
        bool closed = false;
        bool loading = false;
        StorageMigrationStartChoice? choice = null;
        ManagedStorageMigrationPreview? summary = null;
        StorageMigrationProblem? problem = null;
        var caption = Text(localization.Format("Settings.Dialog.SafeMigrationCounting", count), 13);
        var status = Text();
        var explanation = Text(localization.T("Settings.Dialog.SafeMigrationBrief"));
        var notes = Text(localization.T("Settings.Dialog.SafeMigrationPrecautions"), 13);
        var technical = Text();
        var detail = new Expander
        {
            Header = localization.T("Settings.Dialog.SafeMigrationMore"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new ScrollViewer { MaxHeight = 140, Content = technical }
        };
        technical.Text = localization.T("Settings.Dialog.SafeMigrationMoreBody");
        var start = ActionButton(localization.T("Settings.Dialog.SafeMigrationStartShort"), true);
        var back = ActionButton(localization.T("Settings.Dialog.SafeMigrationNotNow"));
        start.IsEnabled = false;
        var content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                caption,
                new StackPanel { Spacing = 6, Children =
                {
                    Location(localization.T("Settings.Dialog.SafeMigrationFrom"), oldPath),
                    Location(localization.T("Settings.Dialog.SafeMigrationTo"), newPath)
                }},
                new StackPanel { Spacing = 8, Children = { explanation, notes } },
                status, detail, Footer(back, start)
            }
        };
        var dialog = Dialog(root, localization);
        dialog.Content = content;
        using var sizing = new DialogSize(root, dialog, content);
        back.Click += (_, _) => dialog.Hide();
        start.Click += async (_, _) =>
        {
            if (loading) return;
            if (problem is { } failure)
            {
                if (ManagedStorageMigrationPresentation.NeedsNewLocation(failure))
                {
                    choice = StorageMigrationStartChoice.ChooseLocation;
                    dialog.Hide();
                }
                else await LoadPreviewAsync();
                return;
            }
            choice = summary?.CopiesRemoved == true ? StorageMigrationStartChoice.Restart : StorageMigrationStartChoice.Start;
            dialog.Hide();
        };
        dialog.Closing += (_, _) => { closed = true; cancel.Cancel(); };
        var visible = dialog.ShowAsync().AsTask();
        Task inspection = LoadPreviewAsync();
        await visible;
        closed = true;
        cancel.Cancel();
        await inspection;
        return choice;

        async Task LoadPreviewAsync()
        {
            loading = true;
            problem = null;
            start.IsEnabled = false;
            status.Text = localization.T("Settings.Dialog.SafeMigrationPhase.Preparing");
            try
            {
                summary = await preview(cancel.Token);
                if (closed) return;
                caption.Text = localization.Format("Settings.Dialog.SafeMigrationPreview", summary.WidgetCount,
                    summary.FileCount, FileMetaService.FormatSize(summary.TotalBytes));
                status.Text = summary.CopiesRemoved
                    ? localization.T("Settings.Dialog.SafeMigrationError.CopiesRemoved") : "";
                start.Content = localization.T(summary.CopiesRemoved
                    ? "Settings.Dialog.SafeMigrationRestart" : "Settings.Dialog.SafeMigrationStartShort");
                technical.Text = localization.T("Settings.Dialog.SafeMigrationMoreBody");
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (closed) return;
                problem = ManagedStorageMigrationPresentation.Classify(ex);
                status.Text = localization.T("Settings.Dialog.SafeMigrationError." + problem);
                // The localized precautions stay in the expander; the raw English
                // detail is log-only here. Only the run dialog exposes a
                // technical-details expander for it.
                App.Log($"[ManagedStorageMigration] Preview failed ({problem}): {ex}");
                start.Content = localization.T(ManagedStorageMigrationPresentation.NeedsNewLocation(problem.Value)
                    ? "Settings.Dialog.SafeMigrationChooseLocation" : "Settings.Dialog.SafeMigrationRetry");
            }
            finally
            {
                loading = false;
                if (!closed) start.IsEnabled = true;
            }
        }
    }

    private ManagedStorageMigrationDialog(XamlRoot root, DispatcherQueue dispatcher, LocalizationService localization,
        string oldPath, string newPath, bool restart,
        Func<ManagedStorageMigrationOptions, Task<ManagedStorageMigrationResult>> migrate)
    {
        _localization = localization;
        _dispatcher = dispatcher;
        _restart = restart;
        _migrate = migrate;
        _dialog = Dialog(root, localization);
        _primary = ActionButton(localization.T("Settings.Dialog.SafeMigrationRetry"), true);
        _secondary = ActionButton(localization.T("Settings.Dialog.SafeMigrationStop"));
        _openSource = ActionButton(localization.T("Settings.Dialog.SafeMigrationOpenSource"));
        _openTarget = ActionButton(localization.T("Settings.Dialog.MigrateOpenTargetButton"));
        _openSource.Click += async (_, _) => await OpenFolderAsync(oldPath);
        _openTarget.Click += async (_, _) => await OpenFolderAsync(newPath);
        _primary.Click += async (_, _) =>
        {
            if (_primaryChoosesLocation) { _pickAnother = true; _dialog.Hide(); }
            else await RunAttemptAsync();
        };
        _secondary.Click += (_, _) =>
        {
            if (!_running) { _dialog.Hide(); return; }
            if (_committing) return;
            _cancel.Cancel();
            _phase.Text = localization.T("Settings.Dialog.SafeMigrationStopping");
            _secondary.IsEnabled = false;
        };
        var heading = new Grid { ColumnDefinitions = { new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto } } };
        heading.Children.Add(_phase);
        Grid.SetColumn(_percent, 1);
        heading.Children.Add(_percent);
        _progressView = new StackPanel
        {
            Spacing = 12,
            Children = { heading, _bar, _file, _directory, _activity, _detail }
        };
        _resultView = new StackPanel { Spacing = 14 };
        var stateArea = new Grid { MinHeight = 220 };
        stateArea.Children.Add(_progressView);
        stateArea.Children.Add(_resultView);
        _details = new Expander
        {
            Header = localization.T("Settings.Dialog.SafeMigrationTechnicalDetails"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
            Content = new ScrollViewer { MaxHeight = 130, Content = _technical }
        };
        var links = new StackPanel { Orientation = Orientation.Horizontal, MinHeight = 32, Spacing = 8, Children = { _openSource, _openTarget } };
        var content = new StackPanel { Spacing = 12, Children = { stateArea, _details, links, Footer(_secondary, _primary) } };
        _dialog.Content = content;
        _sizing = new DialogSize(root, _dialog, content);
        _dialog.Closing += (_, args) =>
        {
            if (!_running) return;
            args.Cancel = true;
            if (!_committing) _cancel.Cancel();
        };
    }

    private readonly DialogSize _sizing;

    public static async Task<StorageMigrationDialogResult> RunAsync(
        XamlRoot root, DispatcherQueue dispatcher, LocalizationService localization, string oldPath, string newPath,
        Func<ManagedStorageMigrationOptions, Task<ManagedStorageMigrationResult>> migrate, bool restart = false)
    {
        var view = new ManagedStorageMigrationDialog(root, dispatcher, localization, oldPath, newPath, restart, migrate);
        try
        {
            var visible = view._dialog.ShowAsync().AsTask();
            await view.RunAttemptAsync();
            await visible;
            return new(view._result, view._pickAnother);
        }
        finally
        {
            view._closed = true;
            view._cancel.Cancel();
            view._cancel.Dispose();
            view._sizing.Dispose();
        }
    }

    private async Task RunAttemptAsync()
    {
        if (_running || _closed) return;
        _running = true;
        _committing = false;
        _result = null;
        _primaryChoosesLocation = false;
        int attempt = ++_attempt;
        _cancel.Dispose();
        _cancel = new();
        _primary.Visibility = Visibility.Collapsed;
        _openSource.Visibility = _openTarget.Visibility = Visibility.Collapsed;
        _details.Visibility = Visibility.Collapsed;
        _details.IsExpanded = false;
        _resultView.Visibility = Visibility.Collapsed;
        _progressView.Visibility = Visibility.Visible;
        _bar.IsIndeterminate = true;
        _file.Text = _directory.Text = _activity.Text = _percent.Text = "";
        _phase.Text = _localization.T("Settings.Dialog.SafeMigrationPhase.Preparing");
        _detail.Text = _localization.T("Settings.Dialog.SafeMigrationRunningNote");
        _secondary.Content = _localization.T("Settings.Dialog.SafeMigrationStop");
        _secondary.IsEnabled = true;
        try
        {
            _result = await _migrate(new(
                new Progress<ManagedStorageMigrationProgress>(p =>
                {
                    void Apply() { if (_running && attempt == _attempt && !_closed) Report(p); }
                    if (_dispatcher.HasThreadAccess) Apply(); else _dispatcher.TryEnqueue(Apply);
                }), _cancel.Token, _restart));
            ShowResult(_localization.T("Settings.Dialog.MigrateCompleteTitle"),
                _localization.Format("Settings.Dialog.SafeMigrationComplete", _result.CopiedFileCount,
                    FileMetaService.FormatSize(_result.CopiedBytes), _result.OldRootPath), _result.TemporaryBytes);
        }
        catch (OperationCanceledException ex)
        {
            _restart = false;
            ShowResult(_localization.T("Settings.Dialog.SafeMigrationStopped"),
                _localization.T("Settings.Dialog.SafeMigrationStoppedBody"),
                (ex as StorageMigrationStoppedException)?.TemporaryBytes);
            _primary.Content = _localization.T("Settings.Dialog.SafeMigrationRetry");
            _primary.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            var problem = ManagedStorageMigrationPresentation.Classify(ex);
            _restart = problem == StorageMigrationProblem.CopiesRemoved;
            _primaryChoosesLocation = ManagedStorageMigrationPresentation.NeedsNewLocation(problem);
            ShowResult(_localization.T(_restart ? "Settings.Dialog.SafeMigrationCopiesRemovedTitle" : "Settings.Dialog.MigrateFailedTitle"),
                _localization.T("Settings.Dialog.SafeMigrationError." + problem),
                (ex as StorageMigrationException)?.TemporaryBytes);
            _primary.Content = _localization.T(_restart ? "Settings.Dialog.SafeMigrationRestart" :
                _primaryChoosesLocation ? "Settings.Dialog.SafeMigrationChooseLocation" : "Settings.Dialog.SafeMigrationRetry");
            _primary.Visibility = Visibility.Visible;
            _technical.Text = ex.Message;
            _details.Visibility = Visibility.Visible;
        }
        finally
        {
            _running = false;
            _secondary.IsEnabled = true;
            _secondary.Content = _localization.T("Common.Close");
        }
    }

    private void ShowResult(string title, string message, long? temporaryBytes)
    {
        _progressView.Visibility = Visibility.Collapsed;
        _resultView.Visibility = Visibility.Visible;
        _resultView.Children.Clear();
        _resultView.Children.Add(Text(title, 16));
        _resultView.Children.Add(Text(message));
        if (temporaryBytes is > 0)
            _resultView.Children.Add(Text(_localization.Format("Settings.Dialog.SafeMigrationTemporaryBytes",
                FileMetaService.FormatSize(temporaryBytes.Value)), 13));
        _openSource.Visibility = _openTarget.Visibility = Visibility.Visible;
    }

    private void Report(ManagedStorageMigrationProgress progress)
    {
        _committing = progress.Phase is ManagedStorageMigrationPhase.Committing or ManagedStorageMigrationPhase.Completed;
        _secondary.IsEnabled = !_committing && !_cancel.IsCancellationRequested;
        if (_cancel.IsCancellationRequested && !_committing) return;
        _phase.Text = _localization.T("Settings.Dialog.SafeMigrationPhase." + progress.Phase);
        double? percentage = ManagedStorageMigrationPresentation.Percentage(progress);
        _bar.IsIndeterminate = percentage is null;
        if (percentage is { } value) _bar.Value = value;
        _percent.Text = percentage is { } percent ? $"{percent:0}%" : "";
        string fullPath = progress.CurrentItemName ?? "";
        _file.Text = Path.GetFileName(fullPath);
        _directory.Text = Path.GetDirectoryName(fullPath) ?? "";
        ToolTipService.SetToolTip(_file, fullPath);
        ToolTipService.SetToolTip(_directory, _directory.Text);
        _activity.Text = progress.CheckingCurrentFile ? _localization.T("Settings.Dialog.SafeMigrationCheckingFile") : "";
        _detail.Text = _localization.Format("Settings.Dialog.SafeMigrationProgress",
            progress.CompletedItems, progress.TotalItems, FileMetaService.FormatSize(progress.BytesProcessed),
            FileMetaService.FormatSize(progress.TotalBytes));
    }

    private static ContentDialog Dialog(XamlRoot root, LocalizationService localization)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = localization.T("Settings.Dialog.SafeMigrationTitle"),
            DefaultButton = ContentDialogButton.None
        };
        dialog.Resources["ContentDialogMinWidth"] = 0d;
        dialog.Resources["ContentDialogMaxWidth"] = 520d;
        return dialog;
    }

    private static TextBlock Text(string? text = null, double size = 14) => new()
    {
        Text = text ?? "", FontSize = size, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true
    };

    private static TextBlock SingleLine(double height) => new()
    {
        Height = height, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
        IsTextSelectionEnabled = true
    };

    private static Button ActionButton(string text, bool primary = false)
    {
        var button = new Button { Content = text, MinWidth = 96, MinHeight = 32, Padding = new Thickness(16, 5, 16, 5) };
        if (primary && Application.Current.Resources.TryGetValue("AccentButtonStyle", out object style))
            button.Style = (Style)style;
        return button;
    }

    private static StackPanel Footer(params Button[] buttons)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8, Margin = new Thickness(0, 10, 0, 0)
        };
        foreach (var button in buttons) panel.Children.Add(button);
        return panel;
    }

    private static Grid Location(string label, string path)
    {
        var row = new Grid { ColumnDefinitions = { new() { Width = new GridLength(52) }, new() { Width = new GridLength(1, GridUnitType.Star) } } };
        row.Children.Add(Text(label, 13));
        var value = SingleLine(22);
        value.Text = path;
        ToolTipService.SetToolTip(value, path);
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        return row;
    }

    private sealed class DialogSize : IDisposable
    {
        private readonly XamlRoot _root;
        private readonly FrameworkElement _content;
        internal DialogSize(XamlRoot root, ContentDialog dialog, FrameworkElement content)
        {
            _root = root;
            _content = content;
            root.Changed += Changed;
            Resize();
        }
        private void Changed(XamlRoot sender, XamlRootChangedEventArgs args) => Resize();
        private void Resize() => _content.Width = Math.Max(0, Math.Min(472, _root.Size.Width - 80));
        public void Dispose() => _root.Changed -= Changed;
    }

    private static async Task OpenFolderAsync(string path)
    {
        try { await Launcher.LaunchFolderAsync(await Windows.Storage.StorageFolder.GetFolderFromPathAsync(path)); }
        catch (Exception ex) { App.Log($"[ManagedStorageMigration] Open location failed: {ex.Message}"); }
    }
}
