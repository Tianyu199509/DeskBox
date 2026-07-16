using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Content adapter for the future Todo widget. This keeps Todo in the shared
/// content pipeline without making the widget kind user-creatable yet.
/// </summary>
public sealed class TodoWidgetContentAdapter : IWidgetContent, IWidgetAddActionContent, IDisposable
{
    private readonly Func<TodoWidgetViewModel, FrameworkElement> _viewFactory;
    private FrameworkElement? _view;

    public TodoWidgetContentAdapter(WidgetConfig config, LocalizationService localizationService)
        : this(config, new TodoWidgetStore(config.Id), localizationService)
    {
    }

    public TodoWidgetContentAdapter(WidgetConfig config, TodoWidgetStore store, LocalizationService localizationService)
        : this(config, new TodoWidgetViewModel(store, localizationService, config))
    {
    }

    public TodoWidgetContentAdapter(WidgetConfig config, TodoWidgetStore store, LocalizationService localizationService, SettingsService settingsService)
        : this(config, new TodoWidgetViewModel(store, localizationService, config, settingsService))
    {
    }

    internal TodoWidgetContentAdapter(
        WidgetConfig config,
        TodoWidgetViewModel viewModel,
        Func<TodoWidgetViewModel, FrameworkElement>? viewFactory = null)
    {
        if (config.WidgetKind != WidgetKind.Todo)
        {
            throw new ArgumentException("Todo content requires a Todo widget config.", nameof(config));
        }

        Config = config;
        ViewModel = viewModel;
        _viewFactory = viewFactory ?? (vm => new TodoWidgetContent(vm));
    }

    public WidgetConfig Config { get; }

    public string WidgetId => Config.Id;

    public WidgetKind WidgetKind => Config.WidgetKind;

    public FrameworkElement View => _view ??= _viewFactory(ViewModel);

    public TodoWidgetViewModel ViewModel { get; }

    public Task InitializeAsync()
    {
        return ViewModel.InitializeAsync();
    }

    public Task RefreshAsync()
    {
        return ViewModel.InitializeAsync();
    }

    public void ApplyAppearance()
    {
        ViewModel.ApplyAppearance();
    }

    public void OnActivated()
    {
    }

    public void OnDeactivated()
    {
    }

    public Task AddFromTitleButtonAsync()
    {
        if (View is TodoWidgetContent todoContent)
        {
            todoContent.OpenAddEditor();
        }

        return Task.CompletedTask;
    }

    internal Task<bool> CanImportExternalDropAsync(DataPackageView dataView)
    {
        return View is TodoWidgetContent todoContent
            ? todoContent.CanImportExternalDropAsync(dataView)
            : Task.FromResult(false);
    }

    internal Task<bool> ImportExternalDropAsync(DataPackageView dataView)
    {
        return View is TodoWidgetContent todoContent
            ? todoContent.ImportExternalDropAsync(dataView)
            : Task.FromResult(false);
    }

    public void Dispose()
    {
        if (ViewModel is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
