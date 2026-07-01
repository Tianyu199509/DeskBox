using DeskBox.Models;

namespace DeskBox.Services;

public sealed class FeatureWidgetEntryFactory
{
    private readonly LocalizationService _localizationService;
    private readonly WidgetContentFactory _contentFactory;
    private readonly WidgetRegistry _widgetRegistry;
    private readonly Func<WidgetKind, bool> _isEnabledResolver;

    public FeatureWidgetEntryFactory(
        LocalizationService localizationService,
        WidgetContentFactory contentFactory,
        WidgetRegistry widgetRegistry,
        Func<WidgetKind, bool> isEnabledResolver)
    {
        _localizationService = localizationService;
        _contentFactory = contentFactory;
        _widgetRegistry = widgetRegistry;
        _isEnabledResolver = isEnabledResolver;
    }

    public IReadOnlyList<FeatureWidgetEntry> CreateEntries()
    {
        return _contentFactory
            .GetFeatureWidgetEntryDescriptors()
            .Select(CreateEntry)
            .ToArray();
    }

    private FeatureWidgetEntry CreateEntry(WidgetContentDescriptor descriptor)
    {
        bool isAvailable = descriptor.IsAvailable &&
                           _widgetRegistry.CanCreateWindow(descriptor.WidgetKind);
        bool showToggle = FeatureWidgetSettings.IsFeatureWidget(descriptor.WidgetKind);
        string titleKey = descriptor.WidgetKind == WidgetKind.QuickCapture
            ? "QuickCapture.Name"
            : $"{descriptor.WidgetKind}.Title";
        string localizedTitle = _localizationService.T(titleKey);
        string statusLabel = _localizationService.T(descriptor.StatusLabelKey);
        string description = _localizationService.T(descriptor.StatusDescriptionKey);
        string displayDescription = descriptor.IsPlanned
            ? $"{description} ({statusLabel})"
            : description;

        return new FeatureWidgetEntry(
            descriptor.WidgetKind,
            localizedTitle != titleKey ? localizedTitle : descriptor.DefaultTitle,
            description,
            descriptor.DefaultGlyph,
            _isEnabledResolver(descriptor.WidgetKind),
            showToggle && isAvailable,
            descriptor.HasSettingsPage,
            descriptor.SettingsSectionTag,
            statusLabel,
            displayDescription,
            showToggle,
            isAvailable);
    }
}
