using System.Runtime.InteropServices;
using DeskBox.Helpers;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// Manages the Windows taskbar Jump List for quick actions.
/// </summary>
public static class JumpListService
{
    internal const string AppUserModelId = "DeskBox.DeskBox";

    internal const string ArgNewWidget = "--new-widget";
    internal const string ArgNewFolderMapping = "--new-folder-mapping";
    internal const string ArgLegacyNewFolderWidget = "--new-folder-widget";
    internal const string ArgToggleWidgets = "--toggle-widgets";
    internal const string ArgOpenSettings = "--open-settings";
    internal const string ArgOpenStorage = "--open-storage";

    /// <summary>
    /// Register the App User Model ID so Windows associates the process
    /// with its taskbar button and Start Menu shortcut.
    /// Must be called early in startup, before any window is created.
    /// </summary>
    public static void RegisterAppUserModelId()
    {
        if (AppDistributionService.Current.IsMicrosoftStore ||
            AppDistributionService.Current.IsPackaged)
        {
            // Packaged (MSIX / Microsoft Store) processes already carry their
            // native PackageFamilyName!App identity; overriding it splits
            // pinned Store taskbar icons when --settings spawns.
            return;
        }

        try
        {
            int hr = JumpListNativeMethods.SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            if (hr >= 0)
            {
                App.LogVerbose($"[JumpList] AUMID registered: {AppUserModelId}");
            }
            else
            {
                App.Log($"[JumpList] SetCurrentProcessExplicitAppUserModelID failed hr=0x{hr:X8}");
            }
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
        if (AppDistributionService.Current.IsMicrosoftStore ||
            AppDistributionService.Current.IsPackaged)
        {
            return;
        }

        try
        {
            int hr = JumpListNativeMethods.SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            if (hr >= 0)
            {
                App.LogVerbose($"[JumpList] Process AUMID registered: {AppUserModelId}");
            }
            else
            {
                App.Log($"[JumpList] Process SetCurrentProcessExplicitAppUserModelID failed hr=0x{hr:X8}");
            }
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
        if (!AppDistributionService.Current.IsPackaged)
        {
            // Windows.UI.StartScreen.JumpList requires MSIX package identity;
            // skip on Direct unpackaged installs instead of hitting guaranteed
            // APPMODEL_ERROR_NO_PACKAGE exceptions at startup.
            return;
        }

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

            foreach ((string displayName, string argument) in BuildQuickActionItems(localization))
            {
                AddItem(jumpList, displayName, argument);
            }

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

    internal static IReadOnlyList<(string DisplayName, string Argument)> BuildQuickActionItems(
        LocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(localization);
        return
        [
            (localization.T("Common.NewWidget"), ArgNewWidget),
            (localization.T("Common.NewFolderMapping"), ArgNewFolderMapping),
            (localization.T("JumpList.ToggleWidgets"), ArgToggleWidgets),
            (localization.T("JumpList.OpenStorage"), ArgOpenStorage),
            (localization.T("JumpList.OpenSettings"), ArgOpenSettings),
        ];
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
            if (trimmed is ArgNewWidget or ArgNewFolderMapping or ArgLegacyNewFolderWidget
                or ArgToggleWidgets or ArgOpenSettings or ArgOpenStorage)
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

                case ArgNewWidget:
                    if (app.WidgetManager is { } newWidgetManager)
                    {
                        await newWidgetManager.CreateWidgetOfKindAsync(Models.WidgetKind.File);
                    }
                    break;

                case ArgNewFolderMapping:
                case ArgLegacyNewFolderWidget:
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
                    ShellLaunchService.FireAndForgetOpenPath(path);
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

    private static readonly PropertyKey s_pkey_ToastActivatorCLSID = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 26);

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

    private const uint GpsDefault = 0x00000000;
    private const uint GpsReadWrite = 0x00000002;
    private const short VtLpwstr = 31;
    private const short VtClsid = 72;

    /// <summary>
    /// Set the System.AppUserModelID property on the Start Menu shortcut
    /// so Windows can bind the Jump List to the taskbar button.
    /// </summary>
    private static void EnsureShortcutAumid()
    {
        foreach (string shortcutPath in GetStartMenuShortcutPaths())
        {
            _ = TryStampShortcutAppUserModelId(shortcutPath, AppUserModelId);
        }
    }

    internal static void SyncStartMenuShortcutToastActivator(Guid activatorClsid)
    {
        if (activatorClsid == Guid.Empty ||
            DeskBoxDataPathService.Current.IsDevelopmentRoot ||
            AppDistributionService.Current.IsPackaged)
        {
            return;
        }

        string? currentExePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExePath))
        {
            return;
        }

        string userPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        string userShortcut = string.IsNullOrWhiteSpace(userPrograms)
            ? string.Empty
            : Path.Combine(userPrograms, "DeskBox.lnk");
        string commonPrograms = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        string commonShortcut = string.IsNullOrWhiteSpace(commonPrograms)
            ? string.Empty
            : Path.Combine(commonPrograms, "DeskBox.lnk");

        bool userStamped = !string.IsNullOrWhiteSpace(userShortcut) &&
            TryStampShortcutToastActivatorClsid(userShortcut, AppUserModelId, activatorClsid, currentExePath);

        bool commonExists = !string.IsNullOrWhiteSpace(commonShortcut) && File.Exists(commonShortcut);
        bool commonStamped = commonExists &&
            TryStampShortcutToastActivatorClsid(commonShortcut, AppUserModelId, activatorClsid, currentExePath);

        // All-users Direct installs place DeskBox.lnk in %ProgramData%, which standard
        // users cannot open with GPS_READWRITE. Mirror the verified shortcut into the
        // per-user Start Menu folder so Windows 10 Action Center resolves ToastActivatorCLSID.
        if (!userStamped &&
            !commonStamped &&
            commonExists &&
            !string.IsNullOrWhiteSpace(userShortcut) &&
            !File.Exists(userShortcut) &&
            Directory.Exists(userPrograms) &&
            ShortcutTargetsExecutable(commonShortcut, currentExePath))
        {
            if (StoreUserAssetMigrationFileOps.TryMirrorShortcutFileQuietly(commonShortcut, userShortcut) &&
                !TryStampShortcutToastActivatorClsid(userShortcut, AppUserModelId, activatorClsid, currentExePath))
            {
                StoreUserAssetMigrationFileOps.TryDeleteFileQuietly(userShortcut);
            }
        }
    }

    private static bool ShortcutTargetsExecutable(string shortcutPath, string expectedExePath)
    {
        if (!ShortcutTargetProbe.TryResolveShortcutLocalTarget(
                shortcutPath,
                targetPathHint: null,
                out string resolvedTarget,
                out _,
                out _))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(resolvedTarget),
                Path.GetFullPath(expectedExePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> GetStartMenuShortcutPaths()
    {
        string userPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (!string.IsNullOrWhiteSpace(userPrograms))
        {
            yield return Path.Combine(userPrograms, "DeskBox.lnk");
        }

        string commonPrograms = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        if (!string.IsNullOrWhiteSpace(commonPrograms))
        {
            yield return Path.Combine(commonPrograms, "DeskBox.lnk");
        }
    }

    internal static unsafe bool TryStampShortcutToastActivatorClsid(
        string shortcutPath,
        string expectedAppUserModelId,
        Guid activatorClsid,
        string? expectedTargetExePath = null)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath) ||
            string.IsNullOrWhiteSpace(expectedAppUserModelId) ||
            activatorClsid == Guid.Empty ||
            !File.Exists(shortcutPath))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(expectedTargetExePath) &&
            !ShortcutTargetsExecutable(shortcutPath, expectedTargetExePath))
        {
            return false;
        }

        try
        {
            Guid iid = s_iid_IPropertyStore;
            var aumidKey = s_pkey_AppUserModelID;
            var activatorKey = s_pkey_ToastActivatorCLSID;
            int propVarSize = IntPtr.Size == 8 ? 24 : 16;
            IntPtr pv = Marshal.AllocCoTaskMem(propVarSize);
            try
            {
                int readHr = JumpListNativeMethods.SHGetPropertyStoreFromParsingName(
                    shortcutPath, IntPtr.Zero, GpsDefault, ref iid, out IntPtr readStorePtr);
                if (readHr != 0 || readStorePtr == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    new Span<byte>((void*)pv, propVarSize).Clear();
                    if (StoreGetValue(readStorePtr, ref aumidKey, pv) < 0)
                    {
                        return false;
                    }

                    string? currentAumid;
                    try
                    {
                        currentAumid = ReadPropVariantString(pv);
                    }
                    finally
                    {
                        _ = JumpListNativeMethods.PropVariantClear(pv);
                    }

                    if (!string.Equals(currentAumid, expectedAppUserModelId, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    new Span<byte>((void*)pv, propVarSize).Clear();
                    if (StoreGetValue(readStorePtr, ref activatorKey, pv) >= 0)
                    {
                        try
                        {
                            if (ReadPropVariantClsid(pv) == activatorClsid)
                            {
                                return true;
                            }
                        }
                        finally
                        {
                            _ = JumpListNativeMethods.PropVariantClear(pv);
                        }
                    }
                }
                finally
                {
                    Marshal.Release(readStorePtr);
                }

                int writeHr = JumpListNativeMethods.SHGetPropertyStoreFromParsingName(
                    shortcutPath, IntPtr.Zero, GpsReadWrite, ref iid, out IntPtr writeStorePtr);
                if (writeHr != 0 || writeStorePtr == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    new Span<byte>((void*)pv, propVarSize).Clear();
                    IntPtr guidMem = Marshal.AllocCoTaskMem(16);
                    Marshal.Copy(activatorClsid.ToByteArray(), 0, guidMem, 16);
                    Marshal.WriteInt16(pv, 0, VtClsid);
                    Marshal.WriteIntPtr(pv, 8, guidMem);
                    int setHr;
                    try
                    {
                        setHr = StoreSetValue(writeStorePtr, ref activatorKey, pv);
                    }
                    finally
                    {
                        _ = JumpListNativeMethods.PropVariantClear(pv);
                    }

                    if (setHr < 0 || StoreCommit(writeStorePtr) < 0)
                    {
                        return false;
                    }

                    App.LogVerbose($"[JumpList] Shortcut ToastActivatorCLSID set: {shortcutPath}");
                    return true;
                }
                finally
                {
                    Marshal.Release(writeStorePtr);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(pv);
            }
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[JumpList] Shortcut ToastActivatorCLSID sync skipped: {ex.Message}");
            return false;
        }
    }

    private static Guid? ReadPropVariantClsid(IntPtr pv)
    {
        try
        {
            if (Marshal.ReadInt16(pv) != VtClsid)
            {
                return null;
            }

            IntPtr guidPtr = Marshal.ReadIntPtr(pv, 8);
            if (guidPtr == IntPtr.Zero)
            {
                return null;
            }

            byte[] bytes = new byte[16];
            Marshal.Copy(guidPtr, bytes, 0, 16);
            return new Guid(bytes);
        }
        catch
        {
            return null;
        }
    }

    internal static unsafe bool TryStampShortcutAppUserModelId(string shortcutPath, string appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath) ||
            string.IsNullOrWhiteSpace(appUserModelId) ||
            !File.Exists(shortcutPath))
        {
            return false;
        }

        try
        {
            Guid iid = s_iid_IPropertyStore;
            var key = s_pkey_AppUserModelID;
            int propVarSize = IntPtr.Size == 8 ? 24 : 16;
            IntPtr pv = Marshal.AllocCoTaskMem(propVarSize);
            try
            {
                int readHr = JumpListNativeMethods.SHGetPropertyStoreFromParsingName(
                    shortcutPath, IntPtr.Zero, GpsDefault, ref iid, out IntPtr readStorePtr);
                if (readHr == 0 && readStorePtr != IntPtr.Zero)
                {
                    try
                    {
                        new Span<byte>((void*)pv, propVarSize).Clear();
                        if (StoreGetValue(readStorePtr, ref key, pv) >= 0)
                        {
                            try
                            {
                                string? current = ReadPropVariantString(pv);
                                if (string.Equals(current, appUserModelId, StringComparison.Ordinal))
                                {
                                    return true;
                                }
                            }
                            finally
                            {
                                _ = JumpListNativeMethods.PropVariantClear(pv);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.Release(readStorePtr);
                    }
                }

                int writeHr = JumpListNativeMethods.SHGetPropertyStoreFromParsingName(
                    shortcutPath, IntPtr.Zero, GpsReadWrite, ref iid, out IntPtr writeStorePtr);
                if (writeHr != 0 || writeStorePtr == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    new Span<byte>((void*)pv, propVarSize).Clear();
                    IntPtr strMem = Marshal.StringToCoTaskMemUni(appUserModelId);
                    Marshal.WriteInt16(pv, 0, VtLpwstr);
                    Marshal.WriteIntPtr(pv, 8, strMem);
                    int setHr;
                    try
                    {
                        setHr = StoreSetValue(writeStorePtr, ref key, pv);
                    }
                    finally
                    {
                        _ = JumpListNativeMethods.PropVariantClear(pv);
                    }

                    if (setHr < 0 || StoreCommit(writeStorePtr) < 0)
                    {
                        return false;
                    }

                    App.LogVerbose($"[JumpList] Shortcut AUMID set: {shortcutPath}");
                    return true;
                }
                finally
                {
                    Marshal.Release(writeStorePtr);
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(pv);
            }
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[JumpList] EnsureShortcutAumid skipped: {ex.Message}");
            return false;
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
