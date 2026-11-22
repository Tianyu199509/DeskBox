using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class TodoWidgetContent
{
    private bool _isDetailTitleHeightUpdateQueued;
    private bool _isDetailTitleHeightDragging;
    private double? _detailTitlePreferredHeight;

    private void DetailTitleTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        QueueDetailTitleHeightUpdate();
        ScheduleDetailTitleAutoSave();
    }

    private void QueueDetailTitleHeightUpdate()
    {
        if (_isDisposed || _isDetailTitleHeightUpdateQueued || _isDetailTitleHeightDragging)
        {
            return;
        }

        _isDetailTitleHeightUpdateQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                _isDetailTitleHeightUpdateQueued = false;
                if (_isDisposed)
                {
                    return;
                }

                UpdateDetailTitleEditorHeight();
            }))
        {
            _isDetailTitleHeightUpdateQueued = false;
        }
    }

    private void UpdateDetailTitleEditorHeight()
    {
        if (_isDisposed || _isDetailTitleHeightDragging || DetailTitleTextBox is null)
        {
            return;
        }

        double availableHeight = DetailPage?.ActualHeight > 0
            ? DetailPage.ActualHeight
            : ActualHeight;
        double maximumHeight = TodoTitleEditorHeightPolicy.ResolveMaximumHeight(availableHeight);
        DetailTitleTextBox.MinHeight = TodoTitleEditorHeightPolicy.MinimumHeight;
        DetailTitleTextBox.MaxHeight = maximumHeight;
        DetailTitleHeightSizer.Minimum = TodoTitleEditorHeightPolicy.MinimumHeight;
        DetailTitleHeightSizer.Maximum = maximumHeight;

        string text = DetailTitleTextBox.Text ?? string.Empty;
        double measuredContentHeight = MeasureDetailTitleContentHeight(text);
        double targetHeight = TodoTitleEditorHeightPolicy.ResolveHeight(
            measuredContentHeight,
            availableHeight,
            string.IsNullOrWhiteSpace(text),
            _detailTitlePreferredHeight);
        if (!double.IsFinite(DetailTitleTextBox.Height) ||
            Math.Abs(DetailTitleTextBox.Height - targetHeight) >= 0.5)
        {
            DetailTitleTextBox.Height = targetHeight;
        }
    }

    private double MeasureDetailTitleContentHeight(string text)
    {
        double width = DetailTitleTextBox.ActualWidth;
        if (!double.IsFinite(width) || width <= 0)
        {
            return TodoTitleEditorHeightPolicy.MinimumHeight;
        }

        Thickness padding = DetailTitleTextBox.Padding;
        double contentWidth = Math.Max(1, width - padding.Left - padding.Right - 2);
        var probe = new TextBlock
        {
            Text = text,
            FontFamily = DetailTitleTextBox.FontFamily,
            FontSize = DetailTitleTextBox.FontSize,
            FontWeight = DetailTitleTextBox.FontWeight,
            CharacterSpacing = DetailTitleTextBox.CharacterSpacing,
            TextWrapping = TextWrapping.Wrap
        };
        probe.Measure(new Windows.Foundation.Size(contentWidth, double.PositiveInfinity));
        return probe.DesiredSize.Height + padding.Top + padding.Bottom + 4;
    }

    private void DetailTitleHeightSizer_ManipulationStarted(
        object sender,
        ManipulationStartedRoutedEventArgs e) =>
        _isDetailTitleHeightDragging = true;

    private void DetailTitleHeightSizer_ManipulationCompleted(
        object sender,
        ManipulationCompletedRoutedEventArgs e)
    {
        _isDetailTitleHeightDragging = false;
        CapturePreferredDetailTitleHeight();
    }

    private void DetailTitleHeightSizer_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Up or VirtualKey.Down)
        {
            CapturePreferredDetailTitleHeight();
        }
    }

    private void DetailTitleHeightSizer_DoubleTapped(
        object sender,
        DoubleTappedRoutedEventArgs e)
    {
        _detailTitlePreferredHeight = null;
        ViewModel?.ClearPreferredTitleEditorHeight();
        QueueDetailTitleHeightUpdate();
        e.Handled = true;
    }

    private void CapturePreferredDetailTitleHeight()
    {
        _detailTitlePreferredHeight = TodoTitleEditorHeightPolicy.NormalizePersistedHeight(
            DetailTitleTextBox.Height);
        ViewModel?.PersistTitleEditorHeight(_detailTitlePreferredHeight.Value);
        QueueDetailTitleHeightUpdate();
    }
}
