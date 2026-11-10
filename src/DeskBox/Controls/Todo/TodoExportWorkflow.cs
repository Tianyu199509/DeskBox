using System.Diagnostics;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.Windows.Storage.Pickers;

namespace DeskBox.Controls.Todo;

internal static class TodoExportWorkflow
{
    public static async Task<string?> PickAndExportAsync(
        IntPtr ownerHwnd,
        IReadOnlyList<TodoItem> items,
        TodoExportOptions options,
        LocalizationService localizationService,
        DateTimeOffset? nowOverride = null,
        bool isSelectionSubset = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(localizationService);

        if (items.Count == 0 || ownerHwnd == IntPtr.Zero || !Win32Helper.IsWindow(ownerHwnd))
        {
            return null;
        }

        DateTimeOffset now = nowOverride ?? DateTimeOffset.Now;
        string suggestedFileName = TodoExportService.BuildSuggestedFileName(
            options,
            localizationService,
            now,
            isSelectionSubset);

        var picker = new FileSavePicker(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(ownerHwnd))
        {
            SuggestedStartLocation = PickerLocationId.Desktop,
            SuggestedFileName = suggestedFileName
        };

        string excelLabel = localizationService.T("Todo.Export.FileType.Excel");
        string csvLabel = localizationService.T("Todo.Export.FileType.Csv");

        if (options.Format == TodoExportFormat.Csv)
        {
            picker.DefaultFileExtension = ".csv";
            picker.FileTypeChoices.Add(csvLabel, [".csv"]);
            picker.FileTypeChoices.Add(excelLabel, [".xlsx"]);
        }
        else
        {
            picker.DefaultFileExtension = ".xlsx";
            picker.FileTypeChoices.Add(excelLabel, [".xlsx"]);
            picker.FileTypeChoices.Add(csvLabel, [".csv"]);
        }

        PickFileResult? destination = await picker.PickSaveFileAsync();
        if (string.IsNullOrWhiteSpace(destination?.Path))
        {
            return null;
        }

        await TodoExportService.ExportToFileAsync(
            destination.Path,
            items,
            options,
            localizationService,
            now,
            cancellationToken);

        return destination.Path;
    }

    public static void OpenExportedFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[TodoExport] Shell open failed for '{filePath}', falling back: {ex.Message}");
            try
            {
                Win32Helper.OpenFileOrChooseApp(IntPtr.Zero, filePath);
            }
            catch (Exception innerEx)
            {
                App.Log($"[TodoExport] Fallback open failed for '{filePath}': {innerEx.Message}");
            }
        }
    }

    public static void RevealExportedFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            Win32Helper.ShowInExplorer(filePath);
        }
        catch (Exception ex)
        {
            App.Log($"[TodoExport] ShowInExplorer failed for '{filePath}': {ex.Message}");
        }
    }
}
