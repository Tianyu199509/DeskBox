using DeskBox.Contracts;
using DeskBox.Features.Appearance;
using DeskBox.Features.FeatureWidgets;
using DeskBox.Features.FileDisplay;
using DeskBox.Features.FileStack;
using DeskBox.Features.Interaction;
using DeskBox.Features.ManagedStorage;
using DeskBox.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DeskBox.Views.SettingsSections;

/// <summary>
/// Unified "文件与整理" settings page combining embedded desktop-organization
/// rules (upper section) and file-widget storage, drag-and-drop, appearance,
/// stacking, and open/menu settings (lower section). All dynamic bindings in
/// XAML use compiled <c>{x:Bind}</c> against typed editor DependencyProperties.
/// </summary>
public sealed partial class FileWidgetSettingsSection : UserControl
{
    public static readonly DependencyProperty FileStackProperty =
        DependencyProperty.Register(
            nameof(FileStack),
            typeof(FileStackSettingsViewModel),
            typeof(FileWidgetSettingsSection),
            new PropertyMetadata(null));

    public static readonly DependencyProperty FeatureWidgetsProperty =
        DependencyProperty.Register(
            nameof(FeatureWidgets),
            typeof(FeatureWidgetsSettingsViewModel),
            typeof(FileWidgetSettingsSection),
            new PropertyMetadata(null));

    public static readonly DependencyProperty InteractionProperty =
        DependencyProperty.Register(
            nameof(Interaction),
            typeof(InteractionSettingsViewModel),
            typeof(FileWidgetSettingsSection),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ManagedStorageProperty =
        DependencyProperty.Register(
            nameof(ManagedStorage),
            typeof(ManagedStorageSettingsViewModel),
            typeof(FileWidgetSettingsSection),
            new PropertyMetadata(null));

    public static readonly DependencyProperty FileDisplayProperty =
        DependencyProperty.Register(
            nameof(FileDisplay),
            typeof(FileDisplaySettingsViewModel),
            typeof(FileWidgetSettingsSection),
            new PropertyMetadata(null));

    public static readonly DependencyProperty AppearanceProperty =
        DependencyProperty.Register(
            nameof(Appearance),
            typeof(AppearanceSettingsViewModel),
            typeof(FileWidgetSettingsSection),
            new PropertyMetadata(null));

    private bool _isSyncingQuickAccessToggle;
    private bool _isSyncingDesktopShortcutToggle;

    public FileWidgetSettingsSection()
    {
        InitializeComponent();
        DesktopOrganizationPart.SubPageChanged += OnDesktopOrganizationSubPageChanged;
    }

    public DesktopOrganizationSettingsSection OrganizationSection => DesktopOrganizationPart;

    public bool IsFileDisplaySubPageOpen => FileDisplaySubPagePanel.Visibility == Visibility.Visible;

    public bool IsDetailSubPageOpen => DesktopOrganizationPart.IsDetailSubPageOpen || IsFileDisplaySubPageOpen;

    public FileStackSettingsViewModel? FileStack
    {
        get => (FileStackSettingsViewModel?)GetValue(FileStackProperty);
        set => SetValue(FileStackProperty, value);
    }

    public FeatureWidgetsSettingsViewModel? FeatureWidgets
    {
        get => (FeatureWidgetsSettingsViewModel?)GetValue(FeatureWidgetsProperty);
        set => SetValue(FeatureWidgetsProperty, value);
    }

    public InteractionSettingsViewModel? Interaction
    {
        get => (InteractionSettingsViewModel?)GetValue(InteractionProperty);
        set => SetValue(InteractionProperty, value);
    }

    public ManagedStorageSettingsViewModel? ManagedStorage
    {
        get => (ManagedStorageSettingsViewModel?)GetValue(ManagedStorageProperty);
        set => SetValue(ManagedStorageProperty, value);
    }

    public FileDisplaySettingsViewModel? FileDisplay
    {
        get => (FileDisplaySettingsViewModel?)GetValue(FileDisplayProperty);
        set => SetValue(FileDisplayProperty, value);
    }

    public AppearanceSettingsViewModel? Appearance
    {
        get => (AppearanceSettingsViewModel?)GetValue(AppearanceProperty);
        set => SetValue(AppearanceProperty, value);
    }

    /// <summary>
    /// Host app actions for the desktop-organization opener; injected by the
    /// settings window alongside the editor properties.
    /// </summary>
    public ISettingsAppActions? AppActions { get; set; }

    public event EventHandler<SettingsSectionNavigationRequestedEventArgs>? NavigationRequested;
    public event EventHandler? SubPageChanged;
    public event EventHandler<RoutedEventArgs>? OpenStoragePathRequested;
    public event EventHandler<RoutedEventArgs>? ChangeStoragePathRequested;
    public event EventHandler<bool>? QuickAccessToggled;
    public event EventHandler<bool>? DesktopShortcutToggled;
    public event EventHandler<RoutedEventArgs>? ManageOrphanFoldersRequested;
    public event EventHandler? SliderDragStarted;
    public event EventHandler? SliderDragCompleted;

    public void SetQuickAccessState(bool isPinned, bool isEnabled)
    {
        _isSyncingQuickAccessToggle = true;
        try
        {
            ManagedStorageQuickAccessToggle.IsOn = isPinned;
            ManagedStorageQuickAccessToggle.IsEnabled = isEnabled;
        }
        finally
        {
            _isSyncingQuickAccessToggle = false;
        }
    }

    public void SetDesktopShortcutState(bool isOn, bool isEnabled = true)
    {
        _isSyncingDesktopShortcutToggle = true;
        try
        {
            ManagedStorageDesktopShortcutToggle.IsOn = isOn;
            ManagedStorageDesktopShortcutToggle.IsEnabled = isEnabled;
        }
        finally
        {
            _isSyncingDesktopShortcutToggle = false;
        }
    }

    public void SetStoragePathWarning(
        bool isOpen,
        InfoBarSeverity severity,
        string? title,
        string? message)
    {
        Visibility visibility = isOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        ManagedStorageWarningInfoBar.Severity = severity;
        ManagedStorageWarningInfoBar.Title = title ?? string.Empty;
        ManagedStorageWarningInfoBar.Message = message ?? string.Empty;
        ManagedStorageWarningInfoBar.IsOpen = isOpen;
        ManagedStorageWarningInfoBar.Visibility = visibility;
        ManagedStorageWarningCard.Visibility = visibility;
    }

    public void SetOrphanFoldersSummary(int count, string summary)
    {
        OrphanManagedStorageCard.Description = summary;
        ManageOrphanFoldersButton.IsEnabled = count > 0;
        ManageOrphanFoldersButton.Visibility = count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public void SetOrphanFoldersState(int count, string summaryText, string buttonText)
    {
        OrphanManagedStorageCard.Description = summaryText;
        if (!string.IsNullOrEmpty(buttonText))
        {
            ManageOrphanFoldersButton.Content = buttonText;
        }

        ManageOrphanFoldersButton.IsEnabled = count > 0;
        ManageOrphanFoldersButton.Visibility = count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public void SetDragOutWin10State(bool modifiersReachExternalTargets, string dragOutDescription)
    {
        DragOutActionCard.Description = dragOutDescription;
        DragOutModifierTipToggle.IsEnabled = modifiersReachExternalTargets;
    }

    /// <summary>
    /// Backward-compatibility shim while the legacy storage template is being
    /// retired in SettingsWindow; detaches the desktop-shortcut card if passed.
    /// </summary>
    public void AttachManagedStorageSection(FrameworkElement section)
    {
        if (section.FindName("ManagedStorageDesktopShortcutCard") is FrameworkElement shortcutCard &&
            shortcutCard.Parent is Panel storagePanel)
        {
            storagePanel.Children.Remove(shortcutCard);
        }
    }

    private void OnDesktopOrganizationSubPageChanged(object? sender, EventArgs e)
    {
        if (DesktopOrganizationPart.IsDetailSubPageOpen && IsFileDisplaySubPageOpen)
        {
            FileDisplaySubPagePanel.Visibility = Visibility.Collapsed;
        }

        SyncSubPageVisibility();
        SubPageChanged?.Invoke(this, EventArgs.Empty);
    }

    private void FileDisplayEntryCard_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDisplaySubPage(animate: true);
    }

    public void OpenFileDisplaySubPage(bool animate = true)
    {
        bool wasAlreadyOpen = IsFileDisplaySubPageOpen;
        DesktopOrganizationPart.CloseAllSubPages(animate: false);
        FileDisplaySubPagePanel.Visibility = Visibility.Visible;
        SyncSubPageVisibility();
        ResetHostScrollToTop();
        SubPageChanged?.Invoke(this, EventArgs.Empty);
        if (animate && !wasAlreadyOpen && IsLoaded)
        {
            FileDisplaySubPagePanel.UpdateLayout();
            DetailPageTransitionHelper.PlayNavigationEnter(FileDisplaySubPagePanel, 40f);
        }
    }

    public void CloseFileDisplaySubPage(bool animate = true)
    {
        if (!IsFileDisplaySubPageOpen)
        {
            return;
        }

        FileDisplaySubPagePanel.Visibility = Visibility.Collapsed;
        SyncSubPageVisibility();
        ResetHostScrollToTop();
        SubPageChanged?.Invoke(this, EventArgs.Empty);
        if (animate && IsLoaded)
        {
            FileWidgetSettingsLowerPanel.UpdateLayout();
            DetailPageTransitionHelper.PlayNavigationEnter(FileWidgetSettingsLowerPanel, -40f);
        }
    }

    public void CloseAllSubPages(bool animate = false)
    {
        bool hadFileDisplay = IsFileDisplaySubPageOpen;
        FileDisplaySubPagePanel.Visibility = Visibility.Collapsed;
        DesktopOrganizationPart.CloseAllSubPages(animate: animate);
        SyncSubPageVisibility();
        if (hadFileDisplay)
        {
            SubPageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool TryNavigateBack()
    {
        if (IsFileDisplaySubPageOpen)
        {
            CloseFileDisplaySubPage(animate: true);
            return true;
        }

        return DesktopOrganizationPart.TryNavigateBack();
    }

    private void SyncSubPageVisibility()
    {
        if (IsFileDisplaySubPageOpen)
        {
            PageTitleText.Visibility = Visibility.Collapsed;
            DesktopOrganizationPart.Visibility = Visibility.Collapsed;
            FileWidgetSettingsLowerPanel.Visibility = Visibility.Collapsed;
            return;
        }

        bool isOrgSubPageOpen = DesktopOrganizationPart.IsDetailSubPageOpen;
        PageTitleText.Visibility = isOrgSubPageOpen ? Visibility.Collapsed : Visibility.Visible;
        DesktopOrganizationPart.Visibility = Visibility.Visible;
        FileWidgetSettingsLowerPanel.Visibility = isOrgSubPageOpen ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ResetHostScrollToTop()
    {
        DependencyObject ancestor = this;
        while (ancestor is not null)
        {
            if (ancestor is ScrollViewer scroller)
            {
                scroller.ChangeView(null, 0, null, disableAnimation: true);
                return;
            }
            ancestor = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(ancestor);
        }
    }

    private void NestedSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string sectionTag })
        {
            NavigationRequested?.Invoke(this, new SettingsSectionNavigationRequestedEventArgs(sectionTag));
        }
    }

    private void OpenManagedStoragePathButton_Click(object sender, RoutedEventArgs e) =>
        OpenStoragePathRequested?.Invoke(sender, e);

    private void ChangeManagedStoragePathButton_Click(object sender, RoutedEventArgs e) =>
        ChangeStoragePathRequested?.Invoke(sender, e);

    private void ManagedStorageQuickAccessToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isSyncingQuickAccessToggle)
        {
            QuickAccessToggled?.Invoke(this, ManagedStorageQuickAccessToggle.IsOn);
        }
    }

    private void ManagedStorageDesktopShortcutToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_isSyncingDesktopShortcutToggle)
        {
            DesktopShortcutToggled?.Invoke(this, ManagedStorageDesktopShortcutToggle.IsOn);
        }
    }

    private void ManageOrphanFoldersButton_Click(object sender, RoutedEventArgs e) =>
        ManageOrphanFoldersRequested?.Invoke(sender, e);

    private void AppearanceSlider_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e) =>
        SliderDragStarted?.Invoke(this, EventArgs.Empty);

    private void AppearanceSlider_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e) =>
        SliderDragCompleted?.Invoke(this, EventArgs.Empty);

    private void AppearanceSlider_PointerPressed(object sender, PointerRoutedEventArgs e) =>
        SliderDragStarted?.Invoke(this, EventArgs.Empty);

    private void AppearanceSlider_PointerReleased(object sender, PointerRoutedEventArgs e) =>
        SliderDragCompleted?.Invoke(this, EventArgs.Empty);

    private void AppearanceSlider_LostFocus(object sender, RoutedEventArgs e) =>
        SliderDragCompleted?.Invoke(this, EventArgs.Empty);
}
