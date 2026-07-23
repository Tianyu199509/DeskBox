using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using Microsoft.UI.Xaml;

namespace DeskBox.Services;

/// <summary>
/// Creates widget content views without owning host windows or z-order behavior.
/// Future widget kinds can be validated here before they become creatable windows.
/// </summary>
public sealed class WidgetContentFactory
{
    private readonly LocalizationService _localizationService;
    private readonly IReadOnlyDictionary<WidgetKind, IWidgetContentProvider> _contentProviders;

    public WidgetContentFactory(LocalizationService localizationService)
    {
        _localizationService = localizationService;
        _contentProviders = CreateContentProviders();
    }

    private static readonly IReadOnlyList<WidgetContentDescriptor> DescriptorList =
    [
        new(
            WidgetKind.File,
            "DeskBox",
            "\uE8A5",
            WidgetContentStage.Implemented,
            CanShowInCreateEntry: true,
            WidgetContentAvailability.Available,
            "WidgetContent.File.StatusLabel",
            "WidgetContent.File.StatusDescription",
            "Common.NewWidget"),
        new(
            WidgetKind.QuickCapture,
            "Quick Capture",
            "\uE70F",
            WidgetContentStage.Implemented,
            CanShowInCreateEntry: false,
            WidgetContentAvailability.Available,
            "WidgetContent.QuickCapture.StatusLabel",
            "WidgetContent.QuickCapture.StatusDescription",
            HasSettingsPage: true,
            SettingsSectionTag: "QuickCaptureSettings"),
        new(
            WidgetKind.Todo,
            "Todo",
            "\uE9D5",
            WidgetContentStage.Implemented,
            CanShowInCreateEntry: false,
            WidgetContentAvailability.Available,
            "WidgetContent.Todo.StatusLabel",
            "WidgetContent.Todo.StatusDescription",
            "Todo.NewWidget",
            HasSettingsPage: true,
            SettingsSectionTag: "TodoSettings"),
        new(
            WidgetKind.Music,
            "Music",
            "\uEC4F",
            WidgetContentStage.Implemented,
            CanShowInCreateEntry: false,
            WidgetContentAvailability.Available,
            "WidgetContent.Music.StatusLabel",
            "WidgetContent.Music.StatusDescription",
            HasSettingsPage: true,
            SettingsSectionTag: "MusicSettings",
            ChromeCategory: WidgetChromeCategory.Display,
            DefaultChromeMode: WidgetChromeMode.Overlay),
        new(
            WidgetKind.Weather,
            "Weather",
            "\uE706",
            WidgetContentStage.Implemented,
            CanShowInCreateEntry: false,
            WidgetContentAvailability.Available,
            "WidgetContent.Weather.StatusLabel",
            "WidgetContent.Weather.StatusDescription",
            HasSettingsPage: true,
            SettingsSectionTag: "WeatherSettings",
            ChromeCategory: WidgetChromeCategory.Display,
            DefaultChromeMode: WidgetChromeMode.Overlay),
        new(
            WidgetKind.Tags,
            "Tags",
            "\uE8EC",
            WidgetContentStage.Placeholder,
            CanShowInCreateEntry: false,
            WidgetContentAvailability.Planned,
            "WidgetContent.Tags.StatusLabel",
            "WidgetContent.Tags.StatusDescription"),
        new(
            WidgetKind.SystemMonitor,
            "System Monitor",
            "\uE9D9",
            WidgetContentStage.Placeholder,
            CanShowInCreateEntry: false,
            WidgetContentAvailability.Planned,
            "WidgetContent.SystemMonitor.StatusLabel",
            "WidgetContent.SystemMonitor.StatusDescription",
            ChromeCategory: WidgetChromeCategory.Display,
            DefaultChromeMode: WidgetChromeMode.Overlay),
        new(
            WidgetKind.Search,
            "Search",
            "\uE721",
            WidgetContentStage.Implemented,
            CanShowInCreateEntry: false,
            WidgetContentAvailability.Available,
            "WidgetContent.Search.StatusLabel",
            "WidgetContent.Search.StatusDescription",
            HasSettingsPage: true,
            SettingsSectionTag: "SearchSettings",
            DefaultChromeMode: WidgetChromeMode.Overlay)
    ];

    private static readonly IReadOnlyDictionary<WidgetKind, WidgetContentDescriptor> Descriptors =
        DescriptorList.ToDictionary(descriptor => descriptor.WidgetKind);

    public IWidgetContent CreateExistingContent(WidgetConfig config, FrameworkElement view)
    {
        return new ExistingWidgetContent(config, view);
    }

    public IWidgetContent CreatePlaceholderContent(WidgetConfig config)
    {
        var descriptor = GetDescriptor(config.WidgetKind);
        if (!descriptor.HasPlaceholderContent)
        {
            throw new NotSupportedException($"Widget kind '{config.WidgetKind}' does not have placeholder content.");
        }

        return new PlaceholderWidgetContent(config, descriptor);
    }

    public IWidgetContent CreateTodoContent(WidgetConfig config, TodoWidgetStore? store = null, SettingsService? settingsService = null)
    {
        if (config.WidgetKind != WidgetKind.Todo)
        {
            throw new ArgumentException("Todo content requires a Todo widget config.", nameof(config));
        }

        return CreateDetachedContent(
            config,
            _ => store ?? new TodoWidgetStore(config.Id),
            settingsService);
    }

    /// <summary>
    /// Creates content that is not yet attached to a production widget window.
    /// This is a hidden pipeline path for validating future widget kinds before
    /// they are exposed through user-facing creation flows.
    /// </summary>
    internal IWidgetContent CreateDetachedContent(
        WidgetConfig config,
        Func<WidgetConfig, TodoWidgetStore>? todoStoreFactory = null,
        SettingsService? settingsService = null)
    {
        if (!_contentProviders.TryGetValue(config.WidgetKind, out var provider) ||
            !provider.CanCreateDetachedContent)
        {
            throw new NotSupportedException(
                $"Widget kind '{config.WidgetKind}' does not have detached content.");
        }

        var context = new WidgetContentProviderContext(
            _localizationService,
            settingsService,
            todoStoreFactory,
            GetDescriptor);
        return provider.CreateDetachedContent(config, context);
    }

    internal bool CanCreateDetachedContent(WidgetKind widgetKind)
    {
        return _contentProviders.TryGetValue(widgetKind, out var provider) &&
               provider.CanCreateDetachedContent;
    }

    public IReadOnlyList<WidgetContentDescriptor> GetDescriptors()
    {
        return DescriptorList;
    }

    public WidgetContentDescriptor GetDescriptor(WidgetKind widgetKind)
    {
        if (Descriptors.TryGetValue(widgetKind, out var descriptor))
        {
            return descriptor;
        }

        throw new NotSupportedException($"Widget kind '{widgetKind}' does not have a content descriptor.");
    }

    public IReadOnlyList<WidgetContentDescriptor> GetCreateEntryDescriptors()
    {
        return DescriptorList
            .Where(descriptor => descriptor.CanShowInCreateEntry)
            .ToArray();
    }

    public IReadOnlyList<WidgetContentDescriptor> GetFeatureWidgetEntryDescriptors()
    {
        return DescriptorList
            .Where(descriptor =>
                descriptor.WidgetKind != WidgetKind.File &&
                descriptor.HasImplementedContent &&
                descriptor.IsAvailable)
            .ToArray();
    }

    public bool HasImplementedContent(WidgetKind widgetKind)
    {
        return Descriptors.TryGetValue(widgetKind, out var descriptor) &&
               descriptor.HasImplementedContent;
    }

    public bool IsPlaceholderOnly(WidgetKind widgetKind)
    {
        return Descriptors.TryGetValue(widgetKind, out var descriptor) &&
               descriptor.IsPlaceholderOnly;
    }

    public bool CanShowInCreateEntry(WidgetKind widgetKind)
    {
        return Descriptors.TryGetValue(widgetKind, out var descriptor) &&
               descriptor.CanShowInCreateEntry;
    }

    public bool IsAvailable(WidgetKind widgetKind)
    {
        return Descriptors.TryGetValue(widgetKind, out var descriptor) &&
               descriptor.IsAvailable;
    }

    public bool IsPlanned(WidgetKind widgetKind)
    {
        return Descriptors.TryGetValue(widgetKind, out var descriptor) &&
               descriptor.IsPlanned;
    }

    public bool CanCreatePlaceholderContent(WidgetKind widgetKind)
    {
        return Descriptors.TryGetValue(widgetKind, out var descriptor) &&
               descriptor.HasPlaceholderContent;
    }

    private static IReadOnlyDictionary<WidgetKind, IWidgetContentProvider> CreateContentProviders()
    {
        IWidgetContentProvider[] providers =
        [
            new TodoWidgetContentProvider(),
            new MusicWidgetContentProvider(),
            new WeatherWidgetContentProvider(),
            new SearchWidgetContentProvider(),
            new PlaceholderWidgetContentProvider(WidgetKind.Tags),
            new PlaceholderWidgetContentProvider(WidgetKind.SystemMonitor)
        ];

        return providers.ToDictionary(provider => provider.WidgetKind);
    }
}
