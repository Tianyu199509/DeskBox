using System.Runtime.InteropServices;
using DeskBox.Helpers;
using DeskBox.Platform;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Controls;

/// <summary>
/// Wraps the Shell's native file data object in a WinRT DataPackage. This is
/// used for existing .lnk files because the StorageItem broker does not expose
/// a reliable, non-blocking preflight for shortcut paths. Explorer then
/// receives a real CF_HDROP in the original drag transaction and remains
/// responsible for the drop position.
/// </summary>
internal static partial class NativeShellFileDragProvider
{
    private const ushort CF_HDROP = 15;
    private const uint DVASPECT_CONTENT = 1;
    private const uint TYMED_HGLOBAL = 1;
    private const int S_OK = 0;

    private static readonly Guid s_dataObjectInterfaceId =
        new("0000010E-0000-0000-C000-000000000046");
    private static readonly Guid s_dataObjectProviderInterfaceId =
        new("3D25F6D6-4B2A-433C-9184-7C33AD35D001");

    // Native entry points live in DeskBox.Platform.ShellItemDragNativeMethods.

    /// <param name="hidePreferredDropEffect">
    /// Hide the CFSTR_PREFERREDDROPEFFECT value WinUI copies from
    /// DataPackage.RequestedOperation from out-of-process drop targets so a
    /// multi-bit request can widen the external allowed-operation mask
    /// without making Explorer prompt for the operation on every drop.
    /// </param>
    internal static bool TryAttach(
        DataPackage dataPackage,
        IReadOnlyList<string> sourcePaths,
        bool hidePreferredDropEffect = false)
    {
        ArgumentNullException.ThrowIfNull(dataPackage);
        if (!CanAttachPaths(sourcePaths))
        {
            return false;
        }

        nint shellDataObject = 0;
        nint filterDataObject = 0;
        try
        {
            shellDataObject = CreateShellDataObject(sourcePaths);
            if (shellDataObject == 0 ||
                !HasFileDropFormat(shellDataObject))
            {
                App.Log(
                    "[DragStart] Shell data object did not expose CF_HDROP.");
                return false;
            }

            nint attachedDataObject = shellDataObject;
            if (hidePreferredDropEffect)
            {
                filterDataObject =
                    PreferredDropEffectFilterDataObject.CreateInterfacePointer(
                        shellDataObject);
                attachedDataObject = filterDataObject;
            }

            int result = SetDataObject(dataPackage, attachedDataObject);
            if (result < 0)
            {
                App.Log(
                    $"[DragStart] IDataObjectProvider.SetDataObject failed " +
                    $"hr=0x{result:X8} paths={sourcePaths.Count}");
                return false;
            }

            App.LogVerbose(
                $"[DragStart] Attached native Shell file data object " +
                $"paths={sourcePaths.Count} " +
                $"hidePreferredDropEffect={hidePreferredDropEffect}");
            return true;
        }
        catch (Exception ex)
        {
            App.Log(
                $"[DragStart] Failed to attach native Shell file data " +
                $"object: {ex}");
            return false;
        }
        finally
        {
            PreferredDropEffectFilterDataObject.ReleaseInterfacePointer(
                filterDataObject);
            ReleaseInterface(shellDataObject);
        }
    }

    internal static bool CanAttachPaths(
        IReadOnlyList<string> sourcePaths,
        Func<string, bool>? pathExists = null)
    {
        pathExists ??= path => File.Exists(path) || Directory.Exists(path);
        if (sourcePaths.Count == 0)
        {
            return false;
        }

        string? parentPath = null;
        foreach (string path in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !pathExists(path))
            {
                return false;
            }

            string? currentParent = Path.GetDirectoryName(
                Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(currentParent))
            {
                return false;
            }

            parentPath ??= currentParent;
            if (!string.Equals(
                    parentPath,
                    currentParent,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool AreExistingShortcuts(
        IReadOnlyList<string> sourcePaths,
        Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        return sourcePaths.Count > 0 &&
               sourcePaths.All(path =>
                   !string.IsNullOrWhiteSpace(path) &&
                   string.Equals(
                       Path.GetExtension(path),
                       ".lnk",
                       StringComparison.OrdinalIgnoreCase) &&
                   fileExists(path));
    }

    internal static bool RequiresStorageBrokerBypass(
        IReadOnlyList<string> sourcePaths,
        Func<string, bool>? fileExists = null)
    {
        // Attribute checks are not a safe preflight here. A normal-looking
        // .lnk can still be rejected by the WinRT StorageFile broker (for
        // example when its target is unavailable or permission-sensitive),
        // and TryPrepare runs on the UI STA. Route every existing shell link
        // through the native Shell data object so the drag never has to wait
        // synchronously for that broker.
        return AreExistingShortcuts(sourcePaths, fileExists);
    }

    internal static unsafe nint CreateShellDataObject(
        IReadOnlyList<string> sourcePaths)
    {
        string[] normalizedPaths = sourcePaths
            .Select(Path.GetFullPath)
            .ToArray();
        string parentPath = Path.GetDirectoryName(normalizedPaths[0])!;
        nint parentItemIdList = 0;
        var absoluteItemIdLists = new nint[normalizedPaths.Length];
        var childItemIdLists = new nint[normalizedPaths.Length];
        nint dataObject = 0;

        try
        {
            Marshal.ThrowExceptionForHR(ParseItemIdList(
                parentPath,
                out parentItemIdList));
            for (int index = 0; index < normalizedPaths.Length; index++)
            {
                Marshal.ThrowExceptionForHR(ParseItemIdList(
                    normalizedPaths[index],
                    out absoluteItemIdLists[index]));
                childItemIdLists[index] = ShellItemDragNativeMethods.ILFindLastID(
                    absoluteItemIdLists[index]);
                if (childItemIdLists[index] == 0)
                {
                    throw new InvalidOperationException(
                        $"The Shell did not return a child PIDL for " +
                        $"'{normalizedPaths[index]}'.");
                }
            }

            Guid interfaceId = s_dataObjectInterfaceId;
            fixed (nint* children = childItemIdLists)
            {
                int result = ShellItemDragNativeMethods.SHCreateDataObject(
                    parentItemIdList,
                    (uint)childItemIdLists.Length,
                    children,
                    0,
                    &interfaceId,
                    out dataObject);
                Marshal.ThrowExceptionForHR(result);
            }

            return dataObject;
        }
        catch
        {
            ReleaseInterface(dataObject);
            throw;
        }
        finally
        {
            foreach (nint itemIdList in absoluteItemIdLists)
            {
                FreeItemIdList(itemIdList);
            }

            FreeItemIdList(parentItemIdList);
        }
    }

    private static int ParseItemIdList(
        string path,
        out nint itemIdList)
    {
        int result = ShellItemDragNativeMethods.SHParseDisplayName(
            path,
            0,
            out itemIdList,
            0,
            out _);
        if (result >= 0 && itemIdList == 0)
        {
            return unchecked((int)0x80004005);
        }

        return result;
    }

    private static bool HasFileDropFormat(nint dataObject)
    {
        var format = new NativeFormatEtc
        {
            ClipboardFormat = CF_HDROP,
            TargetDevice = 0,
            Aspect = DVASPECT_CONTENT,
            Index = -1,
            MediumType = TYMED_HGLOBAL
        };
        return new NativeOleDataObject(dataObject).QueryGetData(ref format) ==
               S_OK;
    }

    private static unsafe int SetDataObject(
        DataPackage dataPackage,
        nint shellDataObject)
    {
        nint inspectable = WinRT.MarshalInspectable<DataPackage>.FromManaged(
            dataPackage);
        nint provider = 0;
        try
        {
            int result = QueryInterface(
                inspectable,
                s_dataObjectProviderInterfaceId,
                out provider);
            if (result < 0)
            {
                return result;
            }

            nint* vtable = *(nint**)provider;
            if (vtable == null || vtable[4] == 0)
            {
                return unchecked((int)0x80004002);
            }

            var setDataObject =
                (delegate* unmanaged[Stdcall]<nint, nint, int>)vtable[4];
            return setDataObject(provider, shellDataObject);
        }
        finally
        {
            ReleaseInterface(provider);
            WinRT.MarshalInspectable<DataPackage>.DisposeAbi(inspectable);
        }
    }

    private static unsafe int QueryInterface(
        nint unknown,
        Guid interfaceId,
        out nint resultPointer)
    {
        resultPointer = 0;
        if (unknown == 0)
        {
            return unchecked((int)0x80004003);
        }

        nint* vtable = *(nint**)unknown;
        if (vtable == null || vtable[0] == 0)
        {
            return unchecked((int)0x80004002);
        }

        var queryInterface =
            (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtable[0];
        nint localResult = 0;
        int result = queryInterface(
            unknown,
            &interfaceId,
            &localResult);
        resultPointer = localResult;
        return result;
    }

    internal static unsafe void ReleaseInterface(nint unknown)
    {
        if (unknown == 0)
        {
            return;
        }

        nint* vtable = *(nint**)unknown;
        if (vtable is not null && vtable[2] != 0)
        {
            var release =
                (delegate* unmanaged[Stdcall]<nint, uint>)vtable[2];
            _ = release(unknown);
        }
    }

    private static void FreeItemIdList(nint itemIdList)
    {
        if (itemIdList != 0)
        {
            ShellItemDragNativeMethods.CoTaskMemFree(itemIdList);
        }
    }
}
