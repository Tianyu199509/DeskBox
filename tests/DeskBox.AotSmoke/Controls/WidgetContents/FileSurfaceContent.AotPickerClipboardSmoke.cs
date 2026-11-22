#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    internal async Task<AotPickerInvocationSnapshot>
        InvokeAotFilePickerAsync(
            string suggestedFolder,
            bool expectCancel)
    {
        if (_hostWindowHandle == IntPtr.Zero ||
            !Directory.Exists(suggestedFolder))
        {
            throw new InvalidOperationException(
                "The picker probe requires a real File Widget HWND and an owned suggested folder.");
        }

        HashSet<long> baseline =
            AotPickerClipboardFixture.CaptureVisibleTopLevelWindowHandles();
        string action = expectCancel ? "Cancel" : "Select";
        Task<AotPickerDialogSnapshot> dialogTask = Task.Run(() =>
            AotPickerClipboardFixture.ObservePickerDialogAsync(
                _hostWindowHandle.ToInt64(),
                baseline,
                action));
        IReadOnlyList<string> selectedPaths =
            await PickAndImportFilesAsync(suggestedFolder);
        AotPickerDialogSnapshot dialog = await dialogTask;

        if (expectCancel != (selectedPaths.Count == 0))
        {
            throw new InvalidOperationException(
                $"The real picker '{action}' result did not match the requested branch.");
        }

        return new AotPickerInvocationSnapshot(
            action,
            _hostWindowHandle.ToInt64(),
            Path.GetFullPath(suggestedFolder),
            selectedPaths,
            dialog);
    }

    internal async Task<AotClipboardStorageItemsSnapshot>
        ImportAotClipboardStorageItemsAsync(
            IReadOnlyCollection<string> sourcePaths)
    {
        string[] normalizedPaths = sourcePaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedPaths.Length != 2 ||
            normalizedPaths.Count(File.Exists) != 1 ||
            normalizedPaths.Count(Directory.Exists) != 1)
        {
            throw new InvalidOperationException(
                "The StorageItems probe requires one owned file and one owned folder.");
        }

        IReadOnlyList<IStorageItem> storageItems =
            await _fileService.GetStorageItemsAsync(normalizedPaths);
        if (storageItems.Count != normalizedPaths.Length)
        {
            throw new InvalidOperationException(
                "The owned paths did not materialize as real WinRT StorageItems.");
        }

        var package = new DataPackage
        {
            RequestedOperation = DataPackageOperation.Copy
        };
        package.SetStorageItems(storageItems);
        DataPackageView view = package.GetView();
        bool containsStorageItems =
            view.Contains(StandardDataFormats.StorageItems);
        bool hasDeskBoxSourcePaths = GetPackagePaths(view).Length > 0;
        IReadOnlyList<IStorageItem> materializedItems =
            await view.GetStorageItemsAsync();

        WidgetFeedbackRequest? feedback = null;
        void OnFeedbackRequested(
            object? sender,
            WidgetFeedbackRequestedEventArgs args)
        {
            if (string.Equals(
                    args.Request.DeduplicationKey,
                    "file-paste",
                    StringComparison.Ordinal))
            {
                feedback = args.Request;
            }
        }

        FeedbackRequested += OnFeedbackRequested;
        try
        {
            await PasteDataPackageAsync(
                view,
                includeShellFileDropFallback: false);
        }
        finally
        {
            FeedbackRequested -= OnFeedbackRequested;
        }

        return new AotClipboardStorageItemsSnapshot(
            _hostWindowHandle.ToInt64(),
            containsStorageItems,
            hasDeskBoxSourcePaths,
            package.RequestedOperation.ToString(),
            materializedItems.Select(item => item.Path).ToArray(),
            materializedItems.Select(item => item.GetType().Name).ToArray(),
            feedback?.DeduplicationKey ?? string.Empty,
            feedback?.Severity.ToString() ?? string.Empty,
            feedback?.Message ?? string.Empty,
            ShellFallbackBypassed: true,
            GlobalClipboardUntouched: true);
    }
}

internal sealed record AotPickerInvocationSnapshot(
    string Action,
    long HostWindowHandle,
    string SuggestedFolder,
    IReadOnlyList<string> SelectedPaths,
    AotPickerDialogSnapshot Dialog);

internal sealed record AotClipboardStorageItemsSnapshot(
    long HostWindowHandle,
    bool ContainsStorageItems,
    bool HasDeskBoxSourcePaths,
    string RequestedOperation,
    IReadOnlyList<string> MaterializedPaths,
    IReadOnlyList<string> MaterializedTypes,
    string FeedbackKey,
    string FeedbackSeverity,
    string FeedbackMessage,
    bool ShellFallbackBypassed,
    bool GlobalClipboardUntouched);
#endif
