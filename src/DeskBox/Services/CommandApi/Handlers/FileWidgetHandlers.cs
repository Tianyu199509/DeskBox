using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;
using DeskBox.ViewModels;
using DeskBox.Protocol;

namespace DeskBox.Services.CommandApi.Handlers;

/// <summary>
/// File widget content commands. Both run on the UI thread through the
/// live WidgetViewModel: listing reads the observable Items collection and
/// imports go through ImportPathsAsync, which drives the full managed-
/// folder pipeline (move/copy semantics, Shell progress, organization
/// history for undo).
/// </summary>
internal static class FileWidgetAccess
{
    public static WidgetViewModel RequireViewModel(
        Func<string, WidgetViewModel?> resolver,
        string widgetId)
    {
        WidgetViewModel? viewModel = resolver(widgetId);
        if (viewModel is null)
        {
            throw new CommandValidationException(new CommandErrorPayload
            {
                Code = CommandApiProtocol.ErrorCodes.WidgetNotLoaded,
                Phase = "execute",
                Message = $"File widget '{widgetId}' is configured but not currently loaded.",
                Hint = "Call widgets/show with this widgetId first, then retry.",
            });
        }

        return viewModel;
    }
}

public sealed record WidgetFileItem(
    string Name,
    string Path,
    bool IsFolder,
    bool IsShortcut,
    long FileSize,
    DateTimeOffset? LastModified);

public sealed record WidgetFileListResult(
    string WidgetId,
    string? MappedFolderPath,
    int Count,
    IReadOnlyList<WidgetFileItem> Items);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WidgetFileListResult), TypeInfoPropertyName = "WidgetFileListResult")]
[JsonSerializable(typeof(WidgetFileItem), TypeInfoPropertyName = "WidgetFileItem")]
internal sealed partial class FileWidgetJsonContext : JsonSerializerContext
{
}

/// <summary>Lists the entries currently shown in one file widget.</summary>
public sealed class FilesListHandler : ICommandHandler
{
    private readonly Func<string, WidgetViewModel?> _resolver;

    public FilesListHandler(Func<string, WidgetViewModel?> resolver)
    {
        _resolver = resolver;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "files/list",
        ThreadAffinity: CommandThreadAffinity.UiThread,
        Capability: CommandApiProtocol.Capabilities.FilesRead,
        MutatesState: false,
        Destructive: false,
        Summary: "Lists the file/folder entries currently shown in one file widget.",
        Arguments:
        [
            new CommandArgumentDescriptor("widgetId", "string", true, "File widget id (from widgets/list).", "\"3f2a\""),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":23,"method":"files/list","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"widgetId":"3f2a"}}}""",
        ExampleResponseJson: """{"result":{"data":{"widgetId":"3f2a","mappedFolderPath":"D:\\Docs","count":1,"items":[{"name":"报告.docx","path":"D:\\Docs\\报告.docx"}]}}}""");

    public Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        string widgetId = CommandArguments.RequireWidgetId(arguments);
        WidgetViewModel viewModel = FileWidgetAccess.RequireViewModel(_resolver, widgetId);

        List<WidgetFileItem> items = viewModel.Items
            .Select(item => new WidgetFileItem(
                item.Name,
                string.IsNullOrWhiteSpace(item.TargetPath) ? item.Path : item.TargetPath,
                item.IsFolder,
                item.IsShortcut,
                item.FileSize,
                item.LastModified == default
                    ? null
                    : new DateTimeOffset(item.LastModified)))
            .ToList();
        WidgetFileListResult result = new(widgetId, viewModel.MappedFolderPath, items.Count, items);
        return Task.FromResult(JsonSerializer.SerializeToElement(result, FileWidgetJsonContext.Default.WidgetFileListResult));
    }
}

public sealed record WidgetFileImportResult(string WidgetId, int ImportedCount, IReadOnlyList<string> ImportedPaths, bool Moved);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WidgetFileImportResult), TypeInfoPropertyName = "WidgetFileImportResult")]
internal sealed partial class WidgetFileImportJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Copies or moves files/folders into one file widget's mapped folder.
/// Defaults to the user's managed-drop setting; pass move=true/false to
/// override. Moving removes the source files (recorded in organization
/// history and undoable in the app).
/// </summary>
public sealed class FilesAddHandler : ICommandHandler
{
    private readonly Func<string, WidgetViewModel?> _resolver;

    public FilesAddHandler(Func<string, WidgetViewModel?> resolver)
    {
        _resolver = resolver;
    }

    public CommandRegistration Registration { get; } = new(
        Method: "files/add",
        ThreadAffinity: CommandThreadAffinity.UiThread,
        Capability: CommandApiProtocol.Capabilities.FilesWrite,
        MutatesState: true,
        Destructive: false,
        Summary: "Imports (copies or moves) files/folders into one file widget's mapped folder.",
        Arguments:
        [
            new CommandArgumentDescriptor("widgetId", "string", true, "File widget id.", "\"3f2a\""),
            new CommandArgumentDescriptor("paths", "array", true, "Absolute source paths.", "[\"C:\\a.txt\"]"),
            new CommandArgumentDescriptor("move", "boolean", false,
                "true = move sources into the folder, false = copy; default follows the app's managed-drop setting.", "false"),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":24,"method":"files/add","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"widgetId":"3f2a","paths":["C:\\a.txt"],"move":false}}}""",
        ExampleResponseJson: """{"result":{"data":{"widgetId":"3f2a","importedCount":1,"importedPaths":["C:\\a.txt"],"moved":false}}}""");

    public async Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        string widgetId = CommandArguments.RequireWidgetId(arguments);
        WidgetViewModel viewModel = FileWidgetAccess.RequireViewModel(_resolver, widgetId);
        if (!CommandArguments.TryGetStringArray(arguments, "paths", out List<string> paths)
            || paths.Count == 0)
        {
            throw CommandValidationException.ValidationFailed(
                "The 'paths' argument is required and must be a non-empty array of absolute paths.",
                """Retry with {"paths":["C:\\file.txt"]}.""");
        }

        foreach (string path in paths)
        {
            if (!System.IO.Path.IsPathRooted(path))
            {
                throw CommandValidationException.ValidationFailed(
                    $"The path is not absolute: {path}",
                    "Pass fully qualified absolute paths (drive letter or UNC).");
            }
        }

        bool? moveWhenMapped = CommandArguments.TryGetBool(arguments, "move", out bool move)
            ? move
            : null;

        IReadOnlyList<string> imported = await viewModel
            .ImportPathsAsync(paths, moveWhenMapped)
            .ConfigureAwait(true);

        // When the caller leaves the decision to the app's managed-drop
        // setting we conservatively report moved=false; the authoritative
        // record lives in the organization history either way.
        bool moved = moveWhenMapped == true;
        WidgetFileImportResult result = new(widgetId, imported.Count, imported, moved);
        return JsonSerializer.SerializeToElement(result, WidgetFileImportJsonContext.Default.WidgetFileImportResult);
    }
}
