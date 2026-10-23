using DeskBox.Contracts;
using DeskBox.Features.FeatureWidgets;
using DeskBox.Features.FileStack;
using DeskBox.Features.Interaction;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Views.SettingsSections;

/// <summary>
/// The file-widget overview page. Its compiled bindings span three
/// feature domains, so instead of one shell-facade dependency property the
/// control carries three typed editor dependencies (batch 45, the
/// blueprint-approved x:Bind bridge-free exception): the open-mode combo
/// and its summary read the file-stack editor, the folder-open combo reads
/// the feature-widgets editor, and the file-item context-menu toggle reads
/// the interaction editor. Each editor owns its persisted projection and
/// writes through its coordinator.
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

    public FileWidgetSettingsSection()
    {
        InitializeComponent();
    }

    public FileStackSettingsViewModel? FileStack
    {
        get => (FileStackSettingsViewModel?)GetValue(FileStackProperty);
        set => SetValue(FileStackProperty, value);
    }

    /// <summary>
    /// Host app actions for the desktop-organization opener; injected by the
    /// settings window alongside the editor properties.
    /// </summary>
    public ISettingsAppActions? AppActions { get; set; }

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

    /// <summary>
    /// Mounts the managed-storage (收纳与路径) section inside the overview
    /// card stack — directly below the 桌面整理 entry card (index 0) and
    /// above 文件显示: storage path card → drag-behavior expander. The
    /// desktop-shortcut card rides in the same section template but moves to
    /// the very bottom of this stack, below the 桌面原版右键菜单 card, so the
    /// entry-point toggle reads as the page's closing item instead of sitting
    /// between the storage cards.
    /// </summary>
    private const int ManagedStorageSectionIndex = 1;

    public void AttachManagedStorageSection(FrameworkElement section)
    {
        int index = Math.Min(ManagedStorageSectionIndex, SectionCardsPanel.Children.Count);
        SectionCardsPanel.Children.Insert(index, section);

        // The lookup stays inside the mounted section's own XAML namescope,
        // so the card keeps its window-side wiring (toggle handler and state
        // refresh resolve it by name) even after moving into this panel.
        // WinUI's UIElementCollection.Add does not reparent: the card must be
        // removed from the storage panel first or the append throws
        // 0x800F1000 and aborts the whole first navigation into this page.
        if (section.FindName("ManagedStorageDesktopShortcutCard") is FrameworkElement shortcutCard &&
            shortcutCard.Parent is Panel storagePanel)
        {
            storagePanel.Children.Remove(shortcutCard);
            SectionCardsPanel.Children.Add(shortcutCard);
        }
    }

    public event EventHandler<SettingsSectionNavigationRequestedEventArgs>? NavigationRequested;

    private void NestedSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // Drill-down rows are Buttons, but the 文件显示-style entry cards are
        // toolkit SettingsCards (ButtonBase, not Button); match the shell's
        // FrameworkElement guard so both navigate.
        if (sender is FrameworkElement { Tag: string sectionTag })
        {
            NavigationRequested?.Invoke(this, new SettingsSectionNavigationRequestedEventArgs(sectionTag));
        }
    }

    private void OrganizeDesktopButton_Click(object sender, RoutedEventArgs e)
    {
        AppActions?.ShowDesktopOrganizationWindow();
    }
}
