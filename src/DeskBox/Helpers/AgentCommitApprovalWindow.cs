using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace DeskBox.Helpers;

/// <summary>
/// Host-side native approval prompt for external Agent (MCP/CLI) widget commits
/// that overwrite an existing widget, clear its data, or declare privileged
/// capabilities. Denies automatically after 60 seconds so an unattended prompt
/// can never silently approve.
/// </summary>
internal sealed partial class AgentCommitApprovalWindow : Window
{
    private const int TimeoutSeconds = 60;

    private readonly TaskCompletionSource<bool> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TextBlock _timeoutNote;
    private Microsoft.UI.Xaml.DispatcherTimer? _timer;
    private int _remainingSeconds = TimeoutSeconds;
    private bool _finished;

    public Task<bool> Result => _result.Task;

    public AgentCommitApprovalWindow(
        Services.LocalizationService localization,
        string widgetId,
        string reasons)
    {
        Title = localization.T("CustomWidget.AgentBridge.Approval.Title");
        _timeoutNote = new TextBlock
        {
            Text = localization.T("CustomWidget.AgentBridge.Approval.TimeoutNote"),
            Opacity = 0.6,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };

        var allowButton = new Button
        {
            Content = localization.T("CustomWidget.AgentBridge.Approval.Allow"),
            Style = TryFindStyle("AccentButtonStyle"),
            MinWidth = 132
        };
        allowButton.Click += (_, _) => Finish(allow: true);

        var denyButton = new Button
        {
            Content = localization.T("CustomWidget.AgentBridge.Approval.Deny"),
            MinWidth = 110
        };
        denyButton.Click += (_, _) => Finish(allow: false);

        Content = new Grid
        {
            Padding = new Thickness(24),
            RowDefinitions =
            {
                new RowDefinition(),
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto }
            },
            Children =
            {
                new TextBlock
                {
                    Text = localization.Format("CustomWidget.AgentBridge.Approval.Body", widgetId, reasons),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Top
                },
                _timeoutNote
            }
        };
        Grid.SetRow(_timeoutNote, 1);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { allowButton, denyButton }
        };
        Grid.SetRow(buttonPanel, 2);
        ((Grid)Content).Children.Add(buttonPanel);

        const int width = 520;
        const int height = 300;
        Microsoft.UI.Windowing.DisplayArea display = Microsoft.UI.Windowing.DisplayArea.Primary;
        AppWindow.Resize(new SizeInt32(width, height));
        if (display is not null && display.WorkArea.Width >= width && display.WorkArea.Height >= height)
        {
            AppWindow.Move(new PointInt32(
                display.WorkArea.X + ((display.WorkArea.Width - width) / 2),
                display.WorkArea.Y + ((display.WorkArea.Height - height) / 2)));
        }

        Closed += (_, _) => Finish(allow: false);
    }

    public new void Activate()
    {
        base.Activate();
        _timer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _remainingSeconds--;
            if (_remainingSeconds <= 0)
            {
                Finish(allow: false);
                return;
            }

            _timeoutNote.Text = $"{_remainingSeconds}s";
        };
        _timer.Start();
    }

    private static Style? TryFindStyle(string key) =>
        Application.Current.Resources.TryGetValue(key, out object? style) && style is Style s ? s : null;

    private void Finish(bool allow)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _timer?.Stop();
        _result.TrySetResult(allow);
        if (AppWindow is not null)
        {
            Close();
        }
    }
}