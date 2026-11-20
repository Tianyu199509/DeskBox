using System.Diagnostics;

namespace DeskBox.Helpers;

public enum QuickAccessPinState
{
    Unknown,
    NotPinned,
    Pinned
}

public readonly record struct QuickAccessStateResult(QuickAccessPinState State, string? Error);

public readonly record struct QuickAccessOperationResult(bool Succeeded, string? Error);

/// <summary>
/// Shell helpers for exposing DeskBox folders in File Explorer entry points.
/// </summary>
public static class ExplorerQuickAccessHelper
{

    public static Task<QuickAccessStateResult> GetQuickAccessPinStateAsync(string folderPath)
    {
        return RunShellStaAsync(
            () =>
            {
                QuickAccessPinState state = GetQuickAccessPinState(folderPath, out string? error);
                return new QuickAccessStateResult(state, error);
            },
            "GetPinState");
    }

    public static QuickAccessPinState GetQuickAccessPinState(string folderPath, out string? error)
    {
        return GetQuickAccessPinStateRust(folderPath, out error);
    }

    public static Task<QuickAccessOperationResult> TryPinFolderToQuickAccessAsync(string folderPath)
    {
        return RunShellStaAsync(
            () =>
            {
                bool succeeded = TryPinFolderToQuickAccess(folderPath, out string? error);
                return new QuickAccessOperationResult(succeeded, error);
            },
            "PinFolder");
    }

    public static bool TryPinFolderToQuickAccess(string folderPath, out string? error)
    {
        return TryInvokeFolderVerbRust(
            folderPath,
            QuickAccessNativeOperation.Pin,
            createFolder: true,
            out error);
    }

    public static Task<QuickAccessOperationResult> TryUnpinFolderFromQuickAccessAsync(string folderPath)
    {
        return RunShellStaAsync(
            () =>
            {
                bool succeeded = TryUnpinFolderFromQuickAccess(folderPath, out string? error);
                return new QuickAccessOperationResult(succeeded, error);
            },
            "UnpinFolder");
    }

    public static bool TryUnpinFolderFromQuickAccess(string folderPath, out string? error)
    {
        return TryUnpinFolderFromQuickAccessRust(folderPath, out error);
    }

    private static QuickAccessPinState GetQuickAccessPinStateRust(
        string folderPath,
        out string? error)
    {
        error = null;
        if (!TryNormalizeFolderPath(folderPath, out string fullPath, out error))
        {
            return QuickAccessPinState.Unknown;
        }

        if (!Directory.Exists(fullPath))
        {
            return QuickAccessPinState.NotPinned;
        }

        QuickAccessNativeCallResult result = QuickAccessNativeBackend.Invoke(
            QuickAccessNativeOperation.QueryPinState,
            fullPath,
            string.Empty,
            string.Empty);
        if (!result.Success)
        {
            error = result.Detail;
            return QuickAccessPinState.Unknown;
        }

        return result.PinState;
    }

    private static bool TryUnpinFolderFromQuickAccessRust(string folderPath, out string? error)
    {
        QuickAccessPinState state = GetQuickAccessPinStateRust(folderPath, out error);
        if (state == QuickAccessPinState.NotPinned)
        {
            error = null;
            return true;
        }

        return TryInvokeFolderVerbRust(
            folderPath,
            QuickAccessNativeOperation.Unpin,
            createFolder: false,
            out error);
    }

    private static bool TryInvokeFolderVerbRust(
        string folderPath,
        QuickAccessNativeOperation operation,
        bool createFolder,
        out string? error)
    {
        error = null;
        try
        {
            if (!TryPrepareFolderItem(
                    folderPath,
                    createFolder,
                    out string fullPath,
                    out string parentPath,
                    out string folderName,
                    out error))
            {
                return false;
            }

            QuickAccessNativeCallResult result = QuickAccessNativeBackend.Invoke(
                operation,
                fullPath,
                parentPath,
                folderName);
            if (!result.Success)
            {
                error = result.Detail;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryPrepareFolderItem(
        string folderPath,
        bool createFolder,
        out string fullPath,
        out string parentPath,
        out string folderName,
        out string? error)
    {
        parentPath = string.Empty;
        folderName = string.Empty;
        if (!TryNormalizeFolderPath(folderPath, out fullPath, out error))
        {
            return false;
        }

        if (createFolder)
        {
            Directory.CreateDirectory(fullPath);
        }
        else if (!Directory.Exists(fullPath))
        {
            error = "Folder does not exist.";
            return false;
        }

        parentPath = Path.GetDirectoryName(fullPath) ?? string.Empty;
        folderName = Path.GetFileName(
            fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(parentPath) || string.IsNullOrWhiteSpace(folderName))
        {
            error = "Folder path is invalid.";
            return false;
        }

        return true;
    }

    private static Task<T> RunShellStaAsync<T>(Func<T> action, string operationName)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                T result = action();
                LogShellTiming(operationName, stopwatch.Elapsed);
                completion.TrySetResult(result);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                App.Log($"[QuickAccess] {operationName} failed after {stopwatch.ElapsedMilliseconds}ms: {ex}");
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = $"DeskBox QuickAccess {operationName}"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void LogShellTiming(string operationName, TimeSpan elapsed)
    {
        string message = $"[QuickAccess] {operationName} completed in {elapsed.TotalMilliseconds:0}ms";
        if (elapsed >= TimeSpan.FromSeconds(1))
        {
            App.Log(message);
            return;
        }

        App.LogVerbose(message);
    }

    private static bool TryNormalizeFolderPath(string folderPath, out string fullPath, out string? error)
    {
        fullPath = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(folderPath))
        {
            error = "Folder path is empty.";
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(folderPath);
            string trimmedPath = fullPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (!string.IsNullOrWhiteSpace(trimmedPath) &&
                !string.Equals(
                    trimmedPath,
                    Path.GetPathRoot(fullPath)?.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                fullPath = trimmedPath;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

}
