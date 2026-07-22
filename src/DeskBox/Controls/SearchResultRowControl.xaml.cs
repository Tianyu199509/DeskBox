using DeskBox.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls;

/// <summary>
/// A single row in the search popup result list. Binds to a
/// <see cref="SearchResultItem"/> DataContext and owns its own hover and
/// keyboard-selection visuals, so the popup window only needs to flip
/// <see cref="IsSelected"/> and call <see cref="RefreshIconVisuals"/> when the
/// lazily resolved shell icon arrives.
/// </summary>
public sealed partial class SearchResultRowControl : UserControl
{
    // Captured from XAML (Transparent) so the row stays hit-testable even when no
    // hover/selection brush is applied.
    private readonly Brush _defaultBackground;
    private bool _isHovered;

    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.Register(
            nameof(IsSelected),
            typeof(bool),
            typeof(SearchResultRowControl),
            new PropertyMetadata(false, OnIsSelectedChanged));

    public SearchResultRowControl()
    {
        InitializeComponent();
        _defaultBackground = RowRoot.Background;
        PointerEntered += OnRowPointerEntered;
        PointerExited += OnRowPointerExited;
    }

    /// <summary>Keyboard-selection state, driven by the popup window.</summary>
    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    /// <summary>
    /// The bound result item, if any. Internal so the XAML type-info generator does not
    /// try to emit an activator for SearchResultItem (which has required members).
    /// </summary>
    internal SearchResultItem? Item => DataContext as SearchResultItem;

    private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SearchResultRowControl control)
        {
            return;
        }

        control.SelectionBar.Visibility = (bool)e.NewValue
            ? Visibility.Visible
            : Visibility.Collapsed;
        control.RefreshBackground();
    }

    private void OnRowPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isHovered = true;
        RefreshBackground();
    }

    private void OnRowPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isHovered = false;
        RefreshBackground();
    }

    /// <summary>
    /// Recomputes the row background from the current hover and selection state so the
    /// two never fight over the Background property.
    /// </summary>
    private void RefreshBackground()
    {
        RowRoot.Background = IsSelected
            ? ResolveThemeBrush("ControlFillColorSecondaryBrush")
            : _isHovered
                ? ResolveThemeBrush("SubtleFillColorSecondaryBrush")
                : _defaultBackground;
    }

    /// <summary>
    /// Syncs the shell icon / glyph fallback toggle and the icon source. Called by the
    /// popup window whenever a row element is (re)prepared: <see cref="SearchResultItem.Icon"/>
    /// is populated lazily by the FileMetaService and is not observable, and recycled
    /// rows may be re-bound to the same item instance (no DataContextChanged), so the
    /// XAML binding alone cannot track it.
    /// </summary>
    public void RefreshIconVisuals()
    {
        var item = Item;
        bool hasIcon = item?.Icon is not null;
        FileIcon.Source = item?.Icon;
        FileIcon.Visibility = hasIcon ? Visibility.Visible : Visibility.Collapsed;
        GlyphBlock.Visibility = hasIcon ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetFileColumnsVisible(bool visible)
    {
        TypeColumn.Width = visible ? new GridLength(75) : new GridLength(0);
        SizeColumn.Width = visible ? new GridLength(90) : new GridLength(0);
        DateColumn.Width = visible ? new GridLength(110) : new GridLength(0);
        TypeText.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SizeText.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        DateText.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Brush? ResolveThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out object? value)
            ? value as Brush
            : null;
}
