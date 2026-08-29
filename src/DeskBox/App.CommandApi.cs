using System.Reflection;
using DeskBox.Protocol;
using DeskBox.Services;
using DeskBox.Services.CommandApi;
using DeskBox.Services.CommandApi.Handlers;
using DeskBox.ViewModels;
using Microsoft.UI.Dispatching;

namespace DeskBox;

/// <summary>
/// Composition and lifecycle for the local command API (named-pipe JSON-RPC
/// host used by DeskBox.Cli and MCP clients). Kept in its own partial so the
/// App surface grows by one member instead of absorbing the command API into
/// the startup path.
/// </summary>
public partial class App
{
    private PipeRpcServer? _commandApiServer;

    private string CommandApiServerVersion
        => (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)) ?? "0.0.0";

    private void StartCommandApi()
    {
        if (_commandApiServer is not null)
        {
            return;
        }

        if (!SettingsService.Settings.EnableCommandApi)
        {
            Log("Command API disabled by settings; pipe server not started.");
            return;
        }

        try
        {
            DeskBoxDataPathService dataPaths = DeskBoxDataPathService.Current;
            string pipeName = CommandApiProtocol.GetPipeName(dataPaths.InstanceScope);
            string auditLogFilePath = Path.Combine(dataPaths.RootPath, "CommandApi.audit.log");

            // OnLaunched runs on the UI thread, so the queue for that thread
            // is the correct marshal target for command handlers.
            DispatcherQueue? uiQueue = DispatcherQueue.GetForCurrentThread();
            if (uiQueue is null)
            {
                Log("[CommandApi] No UI DispatcherQueue on the launch thread; pipe server not started.");
                return;
            }

            CommandRegistry registry = BuildCommandApiRegistry();
            ICommandUiDispatcher uiDispatcher = new DispatcherQueueCommandUiDispatcher(uiQueue);
            CommandDispatcher dispatcher = new(
                registry,
                isReadOnlyMode: () => SettingsService.Settings.CommandApiReadOnly,
                allowsDestructive: () => SettingsService.Settings.AllowDestructiveCommands,
                uiDispatcher: uiDispatcher,
                serverVersion: CommandApiServerVersion);

            _commandApiServer = new PipeRpcServer(
                dispatcher,
                pipeName,
                auditLogFilePath: auditLogFilePath,
                log: message => Log($"[CommandApi] {message}"));
            _commandApiServer.Start();
        }
        catch (Exception ex)
        {
            // The command API is an optional surface: a failure to host it
            // must never take the desktop app down.
            Log($"[CommandApi] Failed to start pipe server: {ex.Message}");
            _commandApiServer = null;
        }
    }

    private async void StopCommandApi()
    {
        if (_commandApiServer is null)
        {
            return;
        }

        try
        {
            await _commandApiServer.StopAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log($"[CommandApi] Failed to stop pipe server cleanly: {ex.Message}");
        }
        finally
        {
            _commandApiServer = null;
        }
    }

    private CommandRegistry BuildCommandApiRegistry()
    {
        string serverVersion = CommandApiServerVersion;
        Func<bool> readOnly = () => SettingsService.Settings.CommandApiReadOnly;
        Func<bool> destructive = () => SettingsService.Settings.AllowDestructiveCommands;
        CommandRegistry registry = null!;

        List<ICommandHandler> handlers =
        [
            new ServerPingHandler(),
            new ServerInfoHandler(
                () => registry,
                serverVersion,
                readOnly,
                destructive),
            new ServerSchemaHandler(() => registry, serverVersion),
            new SettingsGetHandler(() => SettingsService.Settings),
            new QuickCaptureListHandler(() => QuickCaptureService),
            new QuickCaptureAddHandler(() => QuickCaptureService),
            new QuickCapturePinHandler(() => QuickCaptureService),
            new QuickCaptureUpdateHandler(() => QuickCaptureService),
            new QuickCaptureDeleteHandler(() => QuickCaptureService),
            new TodoListHandler(),
            new TodoAddHandler(),
            new TodoSetCompletedHandler(ResolveTodoViewModel),
            new TodoDeleteHandler(ResolveTodoViewModel),
            new TodoEditHandler(ResolveTodoViewModel),
            new TodoSetDueDateHandler(ResolveTodoViewModel),
            new TodoClearCompletedHandler(ResolveTodoViewModel),
            new WidgetsListHandler(() => WidgetManager),
            new WidgetsCreateHandler(() => WidgetManager),
            new WidgetsRemoveHandler(() => WidgetManager),
            new WidgetsShowHandler(() => WidgetManager),
            new WidgetsHideHandler(() => WidgetManager),
            new WidgetsRenameHandler(() => WidgetManager),
            new FilesListHandler(ResolveFileViewModel),
            new FilesAddHandler(ResolveFileViewModel),
        ];

        registry = new CommandRegistry(handlers);
        return registry;
    }

    private TodoWidgetViewModel? ResolveTodoViewModel(string widgetId)
    {
        if (WidgetManager is not null
            && WidgetManager.TryGetTodoWidgetViewModel(widgetId, out TodoWidgetViewModel? viewModel))
        {
            return viewModel;
        }

        return null;
    }

    private WidgetViewModel? ResolveFileViewModel(string widgetId)
    {
        if (WidgetManager is not null
            && WidgetManager.TryGetFileWidgetViewModel(widgetId, out WidgetViewModel? viewModel))
        {
            return viewModel;
        }

        return null;
    }

    /// <summary>Bridges the dispatcher-agnostic command API to the WinUI DispatcherQueue.</summary>
    private sealed class DispatcherQueueCommandUiDispatcher(DispatcherQueue dispatcherQueue) : ICommandUiDispatcher
    {
        public bool TryPost(Action work)
            => dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () => work());
    }
}
