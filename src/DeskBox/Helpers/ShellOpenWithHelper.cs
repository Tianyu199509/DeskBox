using System.Runtime.InteropServices;
using DeskBox.Platform;

namespace DeskBox.Helpers;

/// <summary>
/// One recommended "Open with" application, backed by a live Shell
/// association handler. The handler must stay alive until the menu entry is
/// invoked or disposed, matching the Shell lifetime rule for menu hosts.
/// The native pointer is held raw and called through vtable slots — see
/// <see cref="ComVtable"/> for why this is not a ComImport RCW.
/// </summary>
public sealed class OpenWithHandlerEntry : IDisposable
{
    private IntPtr _handler;

    internal OpenWithHandlerEntry(
        IntPtr handler,
        string displayName,
        string? iconSourcePath,
        int iconSourceIndex)
    {
        _handler = handler;
        UiName = displayName;
        IconSourcePath = iconSourcePath;
        IconSourceIndex = iconSourceIndex;
    }

    public string UiName { get; }

    /// <summary>Icon location from IAssocHandler::GetIconLocation; null
    /// when the handler has no icon path (the entry stays text-only).</summary>
    public string? IconSourcePath { get; }

    public int IconSourceIndex { get; }

    /// <summary>
    /// Opens <paramref name="filePath"/> with this application through the
    /// Shell (works for desktop and store applications). Must be called on
    /// the UI thread from a user gesture.
    /// </summary>
    public bool TryInvoke(string filePath)
    {
        if (_handler == IntPtr.Zero || string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        if (!ShellOpenWithInterop.TryCreateDataObjectForPath(
                filePath,
                out IntPtr dataObject))
        {
            return false;
        }

        try
        {
            using IDisposable? childScope =
                DeskBox.Services.ProcessEnvironmentSyncService.AcquireChildLaunchContaminationScope();
            _ = Win32Helper.AllowSetForegroundWindow(uint.MaxValue);
            int hr = ShellOpenWithInterop.HandlerInvoke(_handler, dataObject);
            if (hr < 0)
            {
                App.Log(
                    $"[OpenWith] invoke failed hr=0x{hr:X8} " +
                    $"handler='{UiName}' file='{filePath}'");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            App.Log(
                $"[OpenWith] invoke failed handler='{UiName}' " +
                $"file='{filePath}': {ex.Message}");
            return false;
        }
        finally
        {
            ShellOpenWithInterop.ReleaseDataObject(ref dataObject);
        }
    }

    public void Dispose()
    {
        if (_handler != IntPtr.Zero)
        {
            Marshal.Release(_handler);
            _handler = IntPtr.Zero;
        }
    }
}

/// <summary>
/// Enumerates the recommended "Open with" applications for a file and shows
/// the system "choose another app" dialog. Uses only documented Shell
/// association APIs (SHAssocEnumHandlers); the full classic Shell menu with
/// third-party handlers still belongs to <see cref="ShellContextMenuProxy"/>.
/// </summary>
public static class ShellOpenWithHelper
{
    private const int MaxEntries = 12;

    public static IReadOnlyList<OpenWithHandlerEntry> GetRecommendedApps(
        string filePath)
    {
        string? extension = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(extension))
        {
            return [];
        }

        if (!ShellOpenWithInterop.TryEnumHandlers(extension, out IntPtr enumHandlers))
        {
            return [];
        }

        var entries = new List<OpenWithHandlerEntry>();
        try
        {
            var seen = new HashSet<string>(StringComparer.CurrentCulture);
            var buffer = new IntPtr[1];
            while (entries.Count < MaxEntries)
            {
                int nextHr = ShellOpenWithInterop.HandlersNext(
                    enumHandlers,
                    buffer,
                    out uint fetched);
                if (nextHr < 0 || fetched == 0 || buffer[0] == IntPtr.Zero)
                {
                    break;
                }

                IntPtr handlerPtr = buffer[0];
                OpenWithHandlerEntry? entry =
                    TryCreateEntry(handlerPtr, seen);
                if (entry is null)
                {
                    // Nameless or duplicate handlers drop their reference
                    // here; kept entries own theirs until Dispose.
                    Marshal.Release(handlerPtr);
                }
                else
                {
                    entries.Add(entry);
                }
            }

            return entries;
        }
        catch (Exception ex)
        {
            foreach (OpenWithHandlerEntry leaked in entries)
            {
                leaked.Dispose();
            }
            entries.Clear();
            App.Log(
                $"[OpenWith] enumeration failed ext='{extension}': " +
                ex.Message);
            return [];
        }
        finally
        {
            Marshal.Release(enumHandlers);
        }
    }

    private static OpenWithHandlerEntry? TryCreateEntry(
        IntPtr handlerPtr,
        HashSet<string> seen)
    {
        if (ShellOpenWithInterop.HandlerGetUIName(
                handlerPtr,
                out IntPtr namePtr) < 0)
        {
            return null;
        }

        string? displayName = PtrToStringOrNull(namePtr);
        if (displayName is null)
        {
            return null;
        }

        string? iconPath = null;
        int iconIndex = 0;
        if (ShellOpenWithInterop.HandlerGetIconLocation(
                handlerPtr,
                out IntPtr iconPtr,
                out iconIndex) >= 0)
        {
            // No icon location is fine; the entry stays text-only.
            iconPath = PtrToStringOrNull(iconPtr);
        }

        if (!seen.Add(displayName))
        {
            return null;
        }

        return new OpenWithHandlerEntry(
            handlerPtr,
            displayName,
            iconPath,
            iconIndex);
    }

    /// <summary>
    /// Whether the type can meaningfully be opened with another app:
    /// executables (the Open With dialog refuses them outright) and
    /// shortcuts (.lnk/.url — Explorer opens the target instead) are
    /// hidden, matching Explorer. Extension-less files DO offer Open With
    /// natively (picking a handler is the point), so only these families
    /// are filtered.
    /// </summary>
    public static bool SupportsOpenWith(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        string extension = Path.GetExtension(filePath);
        return !ElevatedFileLauncher.ExecutableExtensions.Contains(extension) &&
            !ShortcutHelper.IsShortcutPath(filePath);
    }

    /// <summary>
    /// Shows the system Open With dialog for the file via the documented
    /// <c>openas</c> verb.
    /// </summary>
    public static bool ShowChooseAppDialog(
        IntPtr ownerWindow,
        string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        try
        {
            return ShellOpenWithInterop.TryShowOpenAsDialog(
                ownerWindow,
                filePath);
        }
        catch (Exception ex)
        {
            App.Log(
                $"[OpenWith] choose-app dialog failed file='{filePath}': " +
                ex.Message);
            return false;
        }
    }

    private static string? PtrToStringOrNull(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            string? value = Marshal.PtrToStringUni(pointer);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        finally
        {
            ShellOpenWithInterop.CoTaskMemFree(pointer);
        }
    }
}
