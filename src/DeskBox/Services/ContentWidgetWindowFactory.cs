using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Views;

namespace DeskBox.Services;

/// <summary>
/// Prepares lightweight content widget windows without exposing future widget
/// kinds through user-facing creation flows.
/// </summary>
public sealed class ContentWidgetWindowFactory
{
    private readonly WidgetContentFactory _contentFactory;
    private readonly SettingsService _settingsService;
    private readonly Func<WidgetConfig, IWidgetContent, SettingsService, WidgetContentDescriptor, ContentWidgetWindow> _windowFactory;
    private readonly Func<WidgetConfig, TodoWidgetStore>? _todoStoreFactory;

    public ContentWidgetWindowFactory(
        WidgetContentFactory contentFactory,
        SettingsService settingsService,
        Func<WidgetConfig, IWidgetContent, SettingsService, WidgetContentDescriptor, ContentWidgetWindow>? windowFactory = null,
        Func<WidgetConfig, TodoWidgetStore>? todoStoreFactory = null)
    {
        _contentFactory = contentFactory;
        _settingsService = settingsService;
        _windowFactory = windowFactory ?? ((config, content, settings, descriptor) =>
            new ContentWidgetWindow(config, content, settings, descriptor));
        _todoStoreFactory = todoStoreFactory;
    }

    internal bool CanCreateHiddenContentWindow(WidgetKind widgetKind)
    {
        return _contentFactory.CanCreateDetachedContent(widgetKind);
    }

    internal ContentWidgetWindow CreateHiddenContentWindow(WidgetConfig config)
    {
        var plan = CreateHiddenContentWindowPlan(config);
        return _windowFactory(plan.Config, plan.Content, _settingsService, plan.Descriptor);
    }

    internal ContentWidgetWindowPlan CreateHiddenContentWindowPlan(WidgetConfig config)
    {
        if (!CanCreateHiddenContentWindow(config.WidgetKind))
        {
            throw new NotSupportedException(
                $"Widget kind '{config.WidgetKind}' does not support hidden content windows.");
        }

        var descriptor = _contentFactory.GetDescriptor(config.WidgetKind);
        var content = _contentFactory.CreateDetachedContent(config, _todoStoreFactory);
        return new ContentWidgetWindowPlan(config, content, descriptor);
    }
}

internal sealed record ContentWidgetWindowPlan(
    WidgetConfig Config,
    IWidgetContent Content,
    WidgetContentDescriptor Descriptor);
