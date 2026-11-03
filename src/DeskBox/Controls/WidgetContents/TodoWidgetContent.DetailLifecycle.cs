using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class TodoWidgetContent
{
    private bool _isDetailPageInitialized;

    internal bool IsDetailPageRealized => DetailPage is not null;

    private bool EnsureDetailPageRealized()
    {
        if (DetailPage is null)
        {
            _ = FindName(nameof(DetailPage));
        }

        if (DetailPage is null)
        {
            return false;
        }

        if (!_isDetailPageInitialized)
        {
            _isDetailPageInitialized = true;
            DetailTitleTextBox.AddHandler(
                UIElement.PreviewKeyDownEvent,
                _detailTitlePreviewKeyDownHandler,
                handledEventsToo: true);
            InitializeDetailNotesAndSteps();
            ApplyDetailPaneColumnPlacement();
            SynchronizeDetailNotes();
            QueueDetailTitleHeightUpdate();
        }

        return true;
    }

    private bool EnsureTodoInlineEditorRealized()
    {
        if (TodoInlineEditor is null)
        {
            _ = FindName(nameof(TodoInlineEditor));
            if (TodoInlineEditor is not null)
            {
                ApplyLocalizedText();
                ApplyEditorVisualStyle();
            }
        }

        return TodoInlineEditor is not null;
    }

    private bool EnsureCustomDueDateOverlayRealized()
    {
        if (CustomDueDateOverlay is null)
        {
            _ = FindName(nameof(CustomDueDateOverlay));
            if (CustomDueDateOverlay is not null)
            {
                ApplyCustomDueDateLocalization(App.Current.LocalizationService);
                ApplyCustomDueDateVisualStyle(ActualTheme == ElementTheme.Dark);
                UpdateCustomDueTimeText();
            }
        }

        return CustomDueDateOverlay is not null;
    }

    private void ApplyDetailPaneColumnPlacement()
    {
        if (DetailPage is null)
        {
            return;
        }

        if (_isDualPane)
        {
            Grid.SetColumn(DetailPage, 2);
            Grid.SetColumnSpan(DetailPage, 1);
            DetailBackColumn.Width = new GridLength(0);
            DetailBackButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            Grid.SetColumn(DetailPage, 0);
            Grid.SetColumnSpan(DetailPage, 3);
            DetailBackColumn.Width = new GridLength(30);
            DetailBackButton.Visibility = Visibility.Visible;
        }
    }
}
