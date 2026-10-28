using System.Runtime.InteropServices;
using DeskBox.Helpers;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// Manages the Windows taskbar Jump List for quick actions.
/// </summary>
public static class JumpListService
{
    private const string AppUserModelId = "DeskBox.DeskBox";

    private const string ArgToggleWidgets = "--toggle-widgets";
    private const string ArgNewFolderWidget = "--new-folder-widget";
    private const string ArgOpenSettings = "--open-settings";
    private const string ArgOpenStorage = "--open-storage";

    /// <summary>
    /// Register the App User Model ID so Windows associates the process
    /// with its taskbar button and Start Menu shortcut.
    /// Must be called early in startup, before any window is created.
    /// </summary>
    public static void RegisterAppUserModelId()
    {
        try
        {
            JumpListNativeMethods.SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            App.LogVerbose($"[JumpList] AUMID registered: {AppUserModelId}");
        }
        catch (Exception ex)
        {
            App.Log($"[JumpList] Failed to register AUMID: {ex.Message}");
        }

        // For unpackaged (Direct) distribution, ensure the Start Menu shortcut
        // carries the AUMID property so the taskbar Jump List can bind to it.
        // This is a no-op for MSIX (Store) distribution where the package
        // manifest already provides the identity.
        EnsureShortcutAumid();
    }

    /// <summary>
    /// Registers only the process AUMID — for the settings-host process,
    /// whose window must group with the main app's taskbar button but which
    /// must not touch the Start Menu shortcut (batch R6).
    /// </summary>
    public static void RegisterProcessAppUserModelIdOnly()
    {
        try
        {
            JumpListNativeMethods.SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            App.LogVerbose($"[JumpList] Process AUMID registered: {AppUserModelId}");
        }
        catch (Exception ex)
        {
            App.Log($"[JumpList] Failed to register process AUMID: {ex.Message}");
        }
    }

    /// <summary>
    /// Configure the taskbar Jump List with quick action items.
    /// Safe to call for both packaged (MSIX) and unpackaged apps.
    /// </summary>
    public static async Task ConfigureAsync(LocalizationService localization)
    {
        try
        {
            var jumpList = await Windows.UI.StartScreen.JumpList.LoadCurrentAsync();
            if (jumpList is null)
            {
                App.LogVerbose("[JumpList] LoadCurrentAsync returned null");
                return;
            }

            jumpList.SystemGroupKind = Windows.UI.StartScreen.JumpListSystemGroupKind.None;

            jumpList.Items.Clear();

            AddItem(jumpList, localization.T("JumpList.ToggleWidgets"), ArgToggleWidgets);
            AddItem(jumpList, localization.T("JumpList.NewFolderWidget"), ArgNewFolderWidget);
            AddItem(jumpList, localization.T("JumpList.OpenSettings"), ArgOpenSettings);
            AddItem(jumpList, localization.T("JumpList.OpenStorage"), ArgOpenStorage);

            await jumpList.SaveAsync();
            App.LogVerbose("[JumpList] Jump list configured successfully");
        }
        catch (Exception ex)
        {
            // Jump List requires a Start Menu shortcut with matching AUMID
            // for unpackaged apps.  Silently skip if not available.
            App.LogVerbose($"[JumpList] Configure skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// Check whether the given command-line arguments contain a Jump List
    /// activation and return the parsed argument if so.
    /// </summary>
    public static string? TryGetJumpListArgument(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return null;
        }

        string[] parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
        {
            string trimmed = part.Trim().Trim('"');
            if (trimmed is ArgToggleWidgets or ArgNewFolderWidget
                or ArgOpenSettings or ArgOpenStorage)
            {
                return trimmed;
            }
        }

        return null;
    }

    /// <summary>
    /// Handle a Jump List activation argument by invoking the
    /// corresponding action on the current App instance.
    /// </summary>
    public static async Task HandleActivationAsync(string argument)
    {
        var app = App.Current;
        if (app is null)
        {
            return;
        }

        App.Log($"[JumpList] Handling activation: {argument}");

        try
        {
            switch (argument)
            {
                case ArgToggleWidgets:
                    if (app.WidgetManager is { } wm)
                    {
                        bool anyVisible = wm.HasVisibleWidgets;
                        await wm.SetAllWidgetsVisibleAsync(!anyVisible);
                    }
                    break;

                case ArgNewFolderWidget:
                    if (app.WidgetManager is not null)
                    {
                        // The native folder dialog is modal on the UI thread,
                        // and jump-list activation can arrive mid-startup:
                        // blocking OnLaunched inside the dialog would hold
                        // the launch pipeline hostage until it closes. Leave
                        // the activation frame first (the same
                        // callback-isolation invariant the tray uses), so the
                        // modal runs once startup's synchronous work drains —
                        // its message loop keeps pumping queued startup work
                        // while it is up.
                        UiDispatch.PostAfterCallback(() =>
                            App.SafeFireAndForget(
                                () => app.CreateFolderWidgetFromPickerAsync(),
                                "jump-list-folder-map"));
                    }
                    break;

                case ArgOpenSettings:
                    app.ShowSettings();
                    break;

                case ArgOpenStorage:
                    string path = SettingsService.NormalizeManagedStorageRootPath(
                        app.SettingsService.Settings.DefaultManagedStorageRootPath);
                    Directory.CreateDirectory(path);
                    Win32Helper.OpenFile(path);
                    break;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[JumpList] Activation handler error: {ex}");
        }
    }

    private static void AddItem(
        Windows.UI.StartScreen.JumpList jumpList,
        string displayName,
        string arguments)
    {
        try
        {
            var item = Windows.UI.StartScreen.JumpListItem.CreateWithArguments(
                arguments,
                displayName);
            // No GroupName: custom items without one fall into the system-
            // localized "Tasks" section instead of an unlocalized header.
            item.Description = displayName;
            jumpList.Items.Add(item);
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[JumpList] Failed to add item '{arguments}': {ex.Message}");
        }
    }

    // ── Shortcut AUMID property (for unpackaged apps) ──

    private static readonly Guid s_iid_IPropertyStore =
        new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    private static readonly PropertyKey s_pkey_AppUserModelID = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    // shell32/propsys/ole32 AUMID entry points live in
    // DeskBox.Platform.JumpListNativeMethods.

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FmtId;
        public int Pid;

        public PropertyKey(Guid fmtid, int pid)
        {
            FmtId = fmtid;
            Pid = pid;
        }
    }

    // IPropertyStore via raw vtable slots (see ComVtable — classic ComImport
    // RCW marshaling does not exist on NativeAOT, so the retail build never
    // reached GetValue at all). Order per propsys.h after IUnknown:
    // GetCount(3), GetAt(4), GetValue(5), SetValue(6), Commit(7).
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PropertyStoreGetValue(
        IntPtr self, ref PropertyKey key, IntPtr pv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PropertyStoreSetValue(
        IntPtr self, ref PropertyKey key, IntPtr pv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PropertyStoreCommit(IntPtr self);

    private static int StoreGetValue(IntPtr store, ref PropertyKey key, IntPtr pv)
        => ComVtable.Slot<PropertyStoreGetValue>(store, 5)(store, ref key, pv);

    private static int StoreSetValue(IntPtr store, ref PropertyKey key, IntPtr pv)
        => ComVtable.Slot<PropertyStoreSetValue>(store, 6)(store, ref key, pv);

    private static int StoreCommit(IntPtr store)
        => ComVtable.Slot<PropertyStoreCommit>(store, 7)(store);

    /// <summary>
    /// Set the System.AppUserModelID property on the Start Menu shortcut
    /// so Windows can bind the Jump List to the taskbar button.
    /// </summary>
    private static void EnsureShortcutAumid()
    {
        try
        {
            string shortcutPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                "DeskBox.lnk");
            if (!File.Exists(shortcutPath))
            {
                return;
            }

            Guid iid = s_iid_IPropertyStore;
            int hr = JumpListNativeMethods.SHGetPropertyStoreFromParsingName(
                shortcutPath, IntPtr.Zero, 0, ref iid, out IntPtr storePtr);
            if (hr != 0 || storePtr == IntPtr.Zero)
            {
                return;
            }

            try
            {
                // Check current value
                var key = s_pkey_AppUserModelID;
                int propVarSize = IntPtr.Size == 8 ? 24 : 16;
                IntPtr pv = Marshal.AllocCoTaskMem(propVarSize);
                try
                {
                    Marshal.Copy(new byte[propVarSize], 0, pv, propVarSize);
                    if (StoreGetValue(storePtr, ref key, pv) < 0)
                    {
                        return;
                    }

                    string? current = ReadPropVariantString(pv);
                    JumpListNativeMethods.PropVariantClear(pv);

                    if (string.Equals(current, AppUserModelId, StringComparison.Ordinal))
                    {
                        return; // Already set
                    }

                    // Set new value
                    Marshal.Copy(new byte[propVarSize], 0, pv, propVarSize);
                    JumpListNativeMethods.PropVariantFromString(AppUserModelId, pv);
                    if (StoreSetValue(storePtr, ref key, pv) < 0)
                    {
                        return;
                    }

                    JumpListNativeMethods.PropVariantClear(pv);
                    if (StoreCommit(storePtr) < 0)
                    {
                        return;
                    }

                    App.LogVerbose($"[JumpList] Shortcut AUMID set: {shortcutPath}");
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pv);
                }
            }
            finally
            {
                Marshal.Release(storePtr);
            }
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[JumpList] EnsureShortcutAumid skipped: {ex.Message}");
        }
    }

    private static string? ReadPropVariantString(IntPtr pv)
    {
        try
        {
            // VT_LPWSTR = 31, stored at offset 8 (after vt + reserved)
            short vt = Marshal.ReadInt16(pv);
            if (vt != 31)
            {
                return null;
            }

            IntPtr strPtr = Marshal.ReadIntPtr(pv, IntPtr.Size == 8 ? 8 : 8);
            return strPtr == IntPtr.Zero ? null : Marshal.PtrToStringUni(strPtr);
        }
        catch
        {
            return null;
        }
    }
}
