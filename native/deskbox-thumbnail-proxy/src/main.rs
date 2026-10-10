#[cfg(not(windows))]
fn main() {
    std::process::exit(2);
}

#[cfg(windows)]
mod windows_proxy {
    use std::{
        cell::RefCell,
        ffi::{OsStr, OsString, c_void},
        io::{self, BufRead, Write},
        mem::size_of,
        os::windows::ffi::{OsStrExt, OsStringExt},
        path::{Path, PathBuf},
        sync::{
            Mutex, OnceLock,
            atomic::{AtomicBool, AtomicI8, AtomicIsize, AtomicU32, AtomicUsize, Ordering},
            mpsc,
        },
        time::{Duration, Instant},
    };

    use windows::{
        Win32::{
            Foundation::{HANDLE, HWND, LPARAM, LRESULT, POINT, SIZE, WPARAM},
            Graphics::Gdi::{
                AC_SRC_ALPHA, AC_SRC_OVER, AlphaBlend, BLENDFUNCTION, BI_RGB, BITMAP,
                BITMAPINFO, CreateCompatibleDC, DIB_RGB_COLORS, DeleteDC, DeleteObject, GetDC,
                GetDIBits, GetObjectW, HBITMAP, HGDIOBJ, ReleaseDC, SelectObject,
            },
            Storage::FileSystem::FILE_FLAGS_AND_ATTRIBUTES,
            System::{
                Com::{COINIT_APARTMENTTHREADED, CoInitializeEx, CoUninitialize},
                LibraryLoader::{
                    GetModuleHandleW, GetProcAddress, LOAD_LIBRARY_SEARCH_SYSTEM32,
                    LoadLibraryExW,
                },
                Threading::GetCurrentThreadId,
            },
            UI::{
                Controls::{ILD_TRANSPARENT, IImageList, SetWindowTheme},
                HiDpi::{
                    DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, SetProcessDpiAwarenessContext,
                },
                Input::KeyboardAndMouse::{GetKeyState, VK_CONTROL, VK_ESCAPE, VK_SHIFT},
                Shell::{
                    CMF_EXPLORE, CMF_ITEMMENU, CMF_NORMAL, CMIC_MASK_CONTROL_DOWN,
                    CMIC_MASK_PTINVOKE, CMIC_MASK_SHIFT_DOWN, CMINVOKECOMMANDINFOEX,
                    Common::ITEMIDLIST, GCS_VERBW, IContextMenu, IContextMenu2,
                    IContextMenu3, IShellFolder,
                    IShellItemImageFactory, SHBindToParent, SHCreateItemFromParsingName,
                    SHFILEINFOW, SHGFI_ADDOVERLAYS, SHGFI_ICON, SHGFI_LARGEICON,
                    SHGFI_OVERLAYINDEX, SHGFI_SYSICONINDEX, SHGetFileInfoW, SHGetImageList,
                    SHParseDisplayName, SHIL_EXTRALARGE, SHIL_JUMBO, SIIGBF_BIGGERSIZEOK,
                    SIIGBF_ICONONLY, SIIGBF_SCALEUP, SIIGBF_THUMBNAILONLY,
                },
                WindowsAndMessaging::{
                    AppendMenuW, CallNextHookEx, CreatePopupMenu, CreateWindowExW,
                    DefWindowProcW, DestroyIcon, DestroyMenu, DestroyWindow, DispatchMessageW,
                    GetClassNameW, GetIconInfo, GetMenuItemCount, GetMenuItemInfoW,
                    GetMessageW, HICON,
                    HMENU, ICONINFO, InsertMenuItemW, MENUITEMINFOW, MFT_SEPARATOR,
                    MFT_STRING, MF_SEPARATOR, MIIM_FTYPE, MIIM_ID, MIIM_STRING,
                    MSG, MSLLHOOKSTRUCT, PM_REMOVE, PeekMessageW, PostMessageW,
                    PostThreadMessageW, RegisterClassW, SW_SHOWNORMAL, SetForegroundWindow,
                    SetWindowsHookExW, TPM_RETURNCMD, TrackPopupMenuEx, TranslateMessage,
                    UnhookWindowsHookEx, WH_MOUSE_LL, WINDOW_STYLE, WM_DRAWITEM,
                    WM_INITMENUPOPUP, WM_KEYDOWN, WM_KEYUP, WM_LBUTTONDOWN, WM_MEASUREITEM,
                    WM_MBUTTONDOWN, WM_MENUCHAR, WM_NULL, WM_QUIT, WM_RBUTTONDOWN,
                    WM_XBUTTONDOWN, WNDCLASSW, WS_EX_TOPMOST, WindowFromPoint,
                },
            },
        },
        core::{BOOL, Interface, PCSTR, PCWSTR, PSTR, PWSTR},
    };

    const MIN_THUMBNAIL_SIZE: i32 = 24;
    const MAX_THUMBNAIL_SIZE: i32 = 512;
    const BITMAP_FILE_HEADER_SIZE: usize = 14;
    const BITMAP_V5_HEADER_SIZE: usize = 124;
    const BITMAP_PIXEL_OFFSET: usize = BITMAP_FILE_HEADER_SIZE + BITMAP_V5_HEADER_SIZE;
    const BI_BITFIELDS: u32 = 3;
    const LCS_SRGB: u32 = 0x7352_4742;
    const LCS_GM_IMAGES: u32 = 4;
    const CONTEXT_MENU_EXIT_INVOKED: i32 = 0;
    const CONTEXT_MENU_EXIT_CANCELLED: i32 = 2;
    const CONTEXT_MENU_EXIT_FAILED: i32 = 3;
    // Exit code 4 is reserved for proxy-level failures from main(); menu
    // protocol codes start above it.
    const CONTEXT_MENU_FIRST_COMMAND_ID: u32 = 1;
    const CONTEXT_MENU_LAST_COMMAND_ID: u32 = 0x7000;
    // Host custom menu items live in a reserved id range ABOVE the range the
    // Shell handlers are offered, so a handler can never collide with them
    // (Raymond Chen, "How to host an IContextMenu, part 9 - Adding custom
    // commands"). 0x7001 is "rename", which is a view-host verb no pure
    // IContextMenu surfaces; the host labels and executes it itself.
    const CONTEXT_MENU_CUSTOM_COMMAND_FIRST: u32 = 0x7001;
    const CONTEXT_MENU_CUSTOM_RENAME_COMMAND_ID: u32 = CONTEXT_MENU_CUSTOM_COMMAND_FIRST;
    const CONTEXT_MENU_CUSTOM_LABEL_MAX_LEN: usize = 256;
    const CONTEXT_MENU_EXIT_CUSTOM_COMMAND: i32 = 5;
    // Classified InvokeCommand failures so the host can say more than "the
    // operation did not complete". E_FAIL from InvokeCommand is the documented
    // "this handler does not own that command" signal, not a transport error.
    const CONTEXT_MENU_EXIT_FAILED_UNSUPPORTED: i32 = 6;
    const CONTEXT_MENU_EXIT_FAILED_CANCELLED: i32 = 7;
    const CONTEXT_MENU_EXIT_FAILED_ACCESS_DENIED: i32 = 8;
    const CONTEXT_MENU_INVOKE_GRACE: Duration = Duration::from_millis(2000);
    // windows-rs does not generate this mask; the value is from shobjidl_core.
    const CMIC_MASK_UNICODE: u32 = 0x0004_0000;
    const CONTEXT_MENU_SERVER_MAX_MENUS: u32 = 50;
    const CONTEXT_MENU_SERVER_MAX_CONSECUTIVE_FAILURES: u32 = 3;
    const CONTEXT_MENU_SERVER_POLL: Duration = Duration::from_millis(100);
    const CONTEXT_MENU_WARMUP_PATH: &str = r"C:\";
    // Batch extraction protocol: the client writes a count-prefixed manifest
    // to stdin and the proxy streams one length-prefixed result frame per
    // request to stdout as each worker finishes.
    const EXTRACT_BATCH_MAGIC: u32 = 0x4458_4231; // "1BXD" as bytes, "DXB1" logical
    const EXTRACT_BATCH_VERSION: u32 = 1;
    const EXTRACT_BATCH_MAX_REQUESTS: u32 = 64;
    // Four workers absorb a full host batch (8 requests = 2 waves, well
    // inside the host's 2500 ms batch timeout) while quartering the burst of
    // per-thread CRT data allocations that eager 8-way spawning caused right
    // after sleep/resume on memory-fragmented machines (feedback 226/452/495:
    // the CRT terminated the whole proxy with runtime error R6016).
    const EXTRACT_BATCH_MAX_WORKERS: usize = 4;
    // Explorer and the COM surrogate run the same Shell handler DLLs on
    // 1 MiB STA threads; matching that stack halves the Rust default and
    // keeps the fan-out's reserved address space small.
    const EXTRACT_BATCH_WORKER_STACK_BYTES: usize = 1024 * 1024;
    // The supervisor starts at most one worker per tick while the unclaimed
    // queue stays deeper than the live worker count: lazy start plus
    // staggering, so thread creations never land on a fragmented heap all
    // at once and a batch of fast items never pays for workers it does not
    // need.
    const EXTRACT_BATCH_RAMP_POLL: Duration = Duration::from_millis(15);
    const EXTRACT_BATCH_MAX_PATH_BYTES: usize = 64 * 1024;
    const TPM_LEFTALIGN: u32 = 0x0000;
    const TPM_RIGHTBUTTON: u32 = 0x0002;
    const TPM_VERTICAL: u32 = 0x0040;

    thread_local! {
        static ACTIVE_CONTEXT_MENU: RefCell<Option<ContextMenuMessageHandler>> =
            const { RefCell::new(None) };
    }

    /// Owner window of the menu that is currently on screen (0 when none).
    /// The low-level mouse hook reads it to know where to post the cancel key.
    static ACTIVE_MENU_OWNER: AtomicIsize = AtomicIsize::new(0);
    /// Set by the mouse hook when it, rather than the user, ended the menu.
    static MENU_CANCELLED_BY_OUTSIDE_CLICK: AtomicBool = AtomicBool::new(false);
    /// Whether the last menu reached the foreground and got an input hook.
    static MENU_FOREGROUND_OK: AtomicBool = AtomicBool::new(false);
    static MENU_HOOK_INSTALLED: AtomicBool = AtomicBool::new(false);
    /// HRESULT of the last failed InvokeCommand (0 when none), surfaced in the
    /// server "result" line for diagnostics.
    static LAST_INVOKE_FAILURE_HR: AtomicU32 = AtomicU32::new(0);

    /// Window class of every Win32 menu, including the Shell menu we host.
    const MENU_WINDOW_CLASS: &str = "#32768";

    // Classic Win32 menus can only be themed through uxtheme's undocumented
    // ordinal exports; Microsoft ships no supported API for this and uses the
    // same private entry points for File Explorer's own dark menus. DeskBox
    // targets Windows 10 22621 and later, where these ordinals are stable.
    const UXTHEME_PREFERRED_APP_MODE_ORDINAL: usize = 135;
    const UXTHEME_ALLOW_DARK_MODE_FOR_WINDOW_ORDINAL: usize = 133;
    const UXTHEME_FLUSH_MENU_THEMES_ORDINAL: usize = 136;
    const UXTHEME_REFRESH_COLOR_POLICY_ORDINAL: usize = 104;
    const PREFERRED_APP_MODE_FORCE_DARK: i32 = 2;
    const PREFERRED_APP_MODE_FORCE_LIGHT: i32 = 3;
    const MENU_THEME_DARK: &str = "DarkMode_Explorer";
    const MENU_THEME_LIGHT: &str = "Explorer";

    type SetPreferredAppModeFn = unsafe extern "system" fn(i32) -> i32;
    type AllowDarkModeForWindowFn = unsafe extern "system" fn(HWND, BOOL) -> BOOL;
    type FlushMenuThemesFn = unsafe extern "system" fn();
    type RefreshColorPolicyFn = unsafe extern "system" fn();

    struct MenuThemeApi {
        set_preferred_app_mode: SetPreferredAppModeFn,
        allow_dark_mode_for_window: Option<AllowDarkModeForWindowFn>,
        flush_menu_themes: Option<FlushMenuThemesFn>,
        refresh_color_policy: Option<RefreshColorPolicyFn>,
    }

    static MENU_THEME_API: OnceLock<Option<MenuThemeApi>> = OnceLock::new();
    /// Last process-wide menu theme applied: -1 unknown, 0 light, 1 dark.
    static APPLIED_MENU_THEME: AtomicI8 = AtomicI8::new(-1);
    static MENU_THEME_UNAVAILABLE_LOGGED: AtomicBool = AtomicBool::new(false);

    #[derive(Clone, Copy, Debug)]
    enum ExtractionMode {
        Thumbnail,
        Icon,
        IconWithOverlays,
    }

    #[derive(Debug)]
    enum ProxyRequest {
        SelfTest,
        Extract {
            path: PathBuf,
            size: i32,
            mode: ExtractionMode,
        },
        ExtractBatch,
        ContextMenu {
            path: PathBuf,
            screen_x: i32,
            screen_y: i32,
            dark: bool,
            rename_label: Option<String>,
        },
        ContextMenuServer {
            dark: bool,
        },
    }

    #[derive(Clone)]
    enum ContextMenuMessageHandler {
        ContextMenu2(IContextMenu2),
        ContextMenu3(IContextMenu3),
    }

    impl ContextMenuMessageHandler {
        unsafe fn handle(&self, message: u32, w_param: WPARAM, l_param: LPARAM) -> Option<LRESULT> {
            match self {
                Self::ContextMenu3(context_menu) => {
                    if message != WM_MENUCHAR && !is_context_menu2_message(message, w_param) {
                        return None;
                    }

                    let mut result = LRESULT(0);
                    // SAFETY: The interface and all menu messages stay on the
                    // same STA thread that created the Shell context menu.
                    unsafe {
                        context_menu
                            .HandleMenuMsg2(message, w_param, l_param, Some(&mut result))
                            .ok()?;
                    }
                    Some(result)
                }
                Self::ContextMenu2(context_menu) => {
                    if !is_context_menu2_message(message, w_param) {
                        return None;
                    }

                    // SAFETY: See the IContextMenu3 branch above.
                    unsafe { context_menu.HandleMenuMsg(message, w_param, l_param).ok()? };
                    Some(LRESULT(0))
                }
            }
        }
    }

    struct MenuGuard(windows::Win32::UI::WindowsAndMessaging::HMENU);

    impl Drop for MenuGuard {
        fn drop(&mut self) {
            // SAFETY: CreatePopupMenu returned this handle to the proxy.
            unsafe {
                let _ = DestroyMenu(self.0);
            }
        }
    }

    /// Owns the menu mouse hook from a dedicated thread. Low-level hook
    /// callbacks are serviced by the installing thread's message pump, so that
    /// thread must never block: while it does, the system stops delivering
    /// mouse input to the hook and the whole desktop input pipeline stutters
    /// (measured: injected events stop reaching the callback and the first
    /// pending call waits for the entire blocked span).
    struct MouseHookGuard {
        thread_id: u32,
        done: mpsc::Receiver<()>,
    }

    impl Drop for MouseHookGuard {
        fn drop(&mut self) {
            // SAFETY: Posting WM_QUIT to the hook thread ends its pump; the
            // thread unhooks itself before acknowledging.
            unsafe {
                let _ =
                    PostThreadMessageW(self.thread_id, WM_QUIT, WPARAM(0), LPARAM(0));
            }
            let _ = self.done.recv_timeout(Duration::from_secs(2));
        }
    }

    /// Ends the hosted menu on any mouse press outside it. The Shell menu only
    /// dismisses itself through the activation and capture machinery, which
    /// DesktopBox's deliberately topmost, non-activating widget windows do not
    /// trigger, so the proxy watches input directly while a menu is up.
    unsafe extern "system" fn menu_mouse_hook_proc(
        code: i32,
        w_param: WPARAM,
        l_param: LPARAM,
    ) -> LRESULT {
        if code >= 0 {
            let message = w_param.0 as u32;
            let is_button_down = matches!(
                message,
                WM_LBUTTONDOWN | WM_RBUTTONDOWN | WM_MBUTTONDOWN | WM_XBUTTONDOWN
            );
            let owner = ACTIVE_MENU_OWNER.load(Ordering::Relaxed);
            if is_button_down && owner != 0 {
                // SAFETY: The hook delivers a fully populated MSLLHOOKSTRUCT.
                let info = unsafe { &*(l_param.0 as *const MSLLHOOKSTRUCT) };
                if !is_point_inside_hosted_menu(info.pt) {
                    MENU_CANCELLED_BY_OUTSIDE_CLICK.store(true, Ordering::Relaxed);
                    dismiss_open_menu(owner);
                }
            }
        }

        // SAFETY: Unhandled input must always continue down the hook chain.
        unsafe { CallNextHookEx(None, code, w_param, l_param) }
    }

    fn is_point_inside_hosted_menu(point: POINT) -> bool {
        let mut class_name = [0u16; 32];
        // SAFETY: WindowFromPoint returns the window under the point, or null.
        let window = unsafe { WindowFromPoint(point) };
        if window.0.is_null() {
            return false;
        }

        // SAFETY: The buffer is sized for the class name of a Win32 menu.
        let length = unsafe { GetClassNameW(window, &mut class_name) };
        if length <= 0 {
            return false;
        }

        let name = String::from_utf16_lossy(&class_name[..length as usize]);
        name == MENU_WINDOW_CLASS
    }

    fn install_menu_mouse_hook() -> Option<MouseHookGuard> {
        let (ready_sender, ready_receiver) = mpsc::channel::<Option<u32>>();
        let (done_sender, done_receiver) = mpsc::channel::<()>();
        std::thread::Builder::new()
            .name("menu-mouse-hook".to_string())
            .spawn(move || {
                let thread_id = unsafe { GetCurrentThreadId() };
                let hook = (|| {
                    // SAFETY: The module handle stays valid for the process
                    // lifetime and the hook procedure lives in it.
                    let module = unsafe { GetModuleHandleW(None) }.ok()?;
                    unsafe {
                        SetWindowsHookExW(
                            WH_MOUSE_LL,
                            Some(menu_mouse_hook_proc),
                            Some(module.into()),
                            0,
                        )
                        .ok()
                    }
                })();
                let _ = ready_sender.send(match hook {
                    Some(_) => Some(thread_id),
                    None => None,
                });

                if let Some(hook) = hook {
                    let mut message = MSG::default();
                    // SAFETY: A blocking GetMessage wait is what services
                    // low-level hook callbacks on this thread; it returns
                    // false only for WM_QUIT, after which the hook is removed.
                    while unsafe { GetMessageW(&mut message, None, 0, 0) }.as_bool() {
                        unsafe { DispatchMessageW(&message) };
                    }
                    // SAFETY: The hook was installed by this thread.
                    unsafe {
                        let _ = UnhookWindowsHookEx(hook);
                    }
                }

                let _ = done_sender.send(());
            })
            .ok()?;

        match ready_receiver.recv_timeout(Duration::from_secs(2)) {
            Ok(Some(thread_id)) => Some(MouseHookGuard {
                thread_id,
                done: done_receiver,
            }),
            _ => None,
        }
    }

    fn menu_theme_api() -> Option<&'static MenuThemeApi> {
        MENU_THEME_API.get_or_init(resolve_menu_theme_api).as_ref()
    }

    fn resolve_menu_theme_api() -> Option<MenuThemeApi> {
        let library: Vec<u16> = "uxtheme.dll"
            .encode_utf16()
            .chain(std::iter::once(0))
            .collect();
        // SAFETY: The library name is a valid wide string that lives for the
        // call; the returned module stays loaded for the process lifetime.
        let module = unsafe {
            LoadLibraryExW(
                PCWSTR(library.as_ptr()),
                None,
                LOAD_LIBRARY_SEARCH_SYSTEM32,
            )
        }
        .ok()?;

        // SAFETY: Each ordinal is resolved to the documented-by-community
        // signature; a missing ordinal simply disables that step.
        unsafe {
            let set_preferred_app_mode = GetProcAddress(
                module,
                PCSTR(UXTHEME_PREFERRED_APP_MODE_ORDINAL as *const u8),
            )?;
            Some(MenuThemeApi {
                set_preferred_app_mode: std::mem::transmute::<
                    unsafe extern "system" fn() -> isize,
                    SetPreferredAppModeFn,
                >(set_preferred_app_mode),
                allow_dark_mode_for_window: GetProcAddress(
                    module,
                    PCSTR(UXTHEME_ALLOW_DARK_MODE_FOR_WINDOW_ORDINAL as *const u8),
                )
                .map(|function| {
                    std::mem::transmute::<
                        unsafe extern "system" fn() -> isize,
                        AllowDarkModeForWindowFn,
                    >(function)
                }),
                flush_menu_themes: GetProcAddress(
                    module,
                    PCSTR(UXTHEME_FLUSH_MENU_THEMES_ORDINAL as *const u8),
                )
                .map(|function| {
                    std::mem::transmute::<unsafe extern "system" fn() -> isize, FlushMenuThemesFn>(
                        function,
                    )
                }),
                refresh_color_policy: GetProcAddress(
                    module,
                    PCSTR(UXTHEME_REFRESH_COLOR_POLICY_ORDINAL as *const u8),
                )
                .map(|function| {
                    std::mem::transmute::<unsafe extern "system" fn() -> isize, RefreshColorPolicyFn>(
                        function,
                    )
                }),
            })
        }
    }

    /// Switches the whole process into (or out of) dark menus.
    fn set_process_menu_theme(dark: bool) {
        let Some(api) = menu_theme_api() else {
            if !MENU_THEME_UNAVAILABLE_LOGGED.swap(true, Ordering::Relaxed) {
                eprintln!("menu theme unsupported: uxtheme ordinals unavailable");
            }
            return;
        };
        let desired: i8 = if dark { 1 } else { 0 };
        if APPLIED_MENU_THEME.load(Ordering::Relaxed) == desired {
            return;
        }

        // SAFETY: The ordinals were resolved from uxtheme and are called with
        // the argument shapes they are documented to take by their use in
        // Windows itself. A live change needs the color policy refresh before
        // the flush, otherwise the cached menu theme survives.
        let desired_mode = if dark {
            PREFERRED_APP_MODE_FORCE_DARK
        } else {
            PREFERRED_APP_MODE_FORCE_LIGHT
        };
        let previous = unsafe {
            if let Some(refresh) = api.refresh_color_policy {
                refresh();
            }
            let previous = (api.set_preferred_app_mode)(desired_mode);
            if let Some(flush) = api.flush_menu_themes {
                flush();
            }
            previous
        };
        // `previous` is the mode that was in force; logging it proves the
        // ordinal still resolves to the real SetPreferredAppMode.
        eprintln!(
            "menu theme applied dark={dark} mode={desired_mode} previous={previous} \
             extras={}{}{}",
            if api.refresh_color_policy.is_some() { "R" } else { "-" },
            if api.flush_menu_themes.is_some() { "F" } else { "-" },
            if api.allow_dark_mode_for_window.is_some() { "W" } else { "-" },
        );

        APPLIED_MENU_THEME.store(desired, Ordering::Relaxed);
    }

    /// Applies the requested theme to the menu owner window and, indirectly, to
    /// the menu popup that window owns.
    fn apply_menu_theme(owner: HWND, dark: bool) {
        set_process_menu_theme(dark);
        let Some(api) = menu_theme_api() else {
            return;
        };

        // SAFETY: The owner window belongs to this process and outlives the
        // menu; both calls accept any window handle.
        unsafe {
            if let Some(allow) = api.allow_dark_mode_for_window {
                let _ = allow(owner, BOOL::from(dark));
            }
            let theme: Vec<u16> = (if dark {
                MENU_THEME_DARK
            } else {
                MENU_THEME_LIGHT
            })
            .encode_utf16()
            .chain(std::iter::once(0))
            .collect();
            let _ = SetWindowTheme(owner, PCWSTR(theme.as_ptr()), PCWSTR::null());
        }
    }

    fn menu_dark_from_token(token: &str) -> bool {
        matches!(token, "dark" | "1" | "true")
    }

    struct WindowGuard(HWND);
    impl Drop for WindowGuard {
        fn drop(&mut self) {
            // SAFETY: CreateWindowExW returned this private, thread-affine
            // hidden window to the proxy.
            unsafe {
                let _ = DestroyWindow(self.0);
            }
        }
    }

    struct ItemIdListGuard(*mut ITEMIDLIST);

    impl Drop for ItemIdListGuard {
        fn drop(&mut self) {
            // SAFETY: SHParseDisplayName allocates the absolute PIDL with the
            // Shell allocator. The child PIDL returned by SHBindToParent points
            // inside this allocation and is deliberately not freed separately.
            unsafe { windows::Win32::UI::Shell::ILFree(Some(self.0)) };
        }
    }

    struct ComGuard;

    impl Drop for ComGuard {
        fn drop(&mut self) {
            // SAFETY: The guard is created only after a successful CoInitializeEx.
            unsafe { CoUninitialize() };
        }
    }

    struct BitmapGuard(HBITMAP);

    impl Drop for BitmapGuard {
        fn drop(&mut self) {
            // SAFETY: IShellItemImageFactory transfers ownership of the HBITMAP.
            unsafe {
                let _ = DeleteObject(HGDIOBJ(self.0.0));
            }
        }
    }

    struct IconGuard(HICON);

    impl Drop for IconGuard {
        fn drop(&mut self) {
            // SAFETY: SHGetFileInfoW returns an owned HICON when SHGFI_ICON is
            // requested. The handle is released after its bitmap was copied.
            unsafe {
                let _ = DestroyIcon(self.0);
            }
        }
    }

    struct IconInfoBitmapGuard(ICONINFO);

    impl Drop for IconInfoBitmapGuard {
        fn drop(&mut self) {
            // SAFETY: GetIconInfo creates both bitmap handles for the caller.
            unsafe {
                if !self.0.hbmColor.is_invalid() {
                    let _ = DeleteObject(HGDIOBJ(self.0.hbmColor.0));
                }
                if !self.0.hbmMask.is_invalid() {
                    let _ = DeleteObject(HGDIOBJ(self.0.hbmMask.0));
                }
            }
        }
    }

    pub fn run() -> Result<i32, String> {
        match parse_request()? {
            ProxyRequest::SelfTest => {
                let pixels = vec![
                    0x00, 0x00, 0xFF, 0xFF, 0x00, 0xFF, 0x00, 0xFF, 0xFF, 0x00, 0x00, 0xFF, 0xFF,
                    0xFF, 0xFF, 0xFF,
                ];
                write_stdout(&encode_bgra_as_bitmap_v5(2, 2, pixels)?)?;
                self_test_batch_manifest()?;
                Ok(0)
            }
            ProxyRequest::Extract { path, size, mode } => {
                extract_shell_image(&path, size, mode)?;
                Ok(0)
            }
            ProxyRequest::ExtractBatch => run_extract_batch(),
            ProxyRequest::ContextMenu {
                path,
                screen_x,
                screen_y,
                dark,
                rename_label,
            } => match show_context_menu(
                &path,
                screen_x,
                screen_y,
                dark,
                rename_label.as_deref(),
            ) {
                Ok(exit_code) => Ok(exit_code),
                Err(error) => {
                    eprintln!("{error}");
                    Ok(CONTEXT_MENU_EXIT_FAILED)
                }
            },
            ProxyRequest::ContextMenuServer { dark } => run_context_menu_server(dark),
        }
    }

    fn parse_request() -> Result<ProxyRequest, String> {
        parse_request_from(std::env::args_os().skip(1))
    }

    fn parse_request_from(
        mut arguments: impl Iterator<Item = OsString>,
    ) -> Result<ProxyRequest, String> {
        let mut first = arguments
            .next()
            .ok_or_else(|| "missing path argument".to_string())?;
        if first == "--self-test" {
            if arguments.next().is_some() {
                return Err("unexpected extra arguments".to_string());
            }
            return Ok(ProxyRequest::SelfTest);
        }
        if first == "--context-menu-server" {
            // The optional theme token selects the menu theme before any
            // window exists; later rounds may switch it.
            let dark = arguments
                .next()
                .map(|value| menu_dark_from_token(&value.to_string_lossy()))
                .unwrap_or(false);
            if arguments.next().is_some() {
                return Err("unexpected extra arguments".to_string());
            }
            return Ok(ProxyRequest::ContextMenuServer { dark });
        }
        if first == "--extract-batch" {
            if arguments.next().is_some() {
                return Err("unexpected extra arguments".to_string());
            }
            return Ok(ProxyRequest::ExtractBatch);
        }
        if first == "--context-menu" {
            let path = arguments
                .next()
                .ok_or_else(|| "missing context menu path argument".to_string())?;
            let screen_x = parse_coordinate(arguments.next(), "x")?;
            let screen_y = parse_coordinate(arguments.next(), "y")?;
            let dark = arguments
                .next()
                .map(|value| menu_dark_from_token(&value.to_string_lossy()))
                .unwrap_or(false);
            // Optional fifth argument: the localized label for the host-owned
            // "rename" entry. Absent or blank means no custom item is added.
            let rename_label = arguments
                .next()
                .map(|value| value.to_string_lossy().into_owned())
                .filter(|value| !value.trim().is_empty());
            if arguments.next().is_some() {
                return Err("unexpected extra arguments".to_string());
            }
            return Ok(ProxyRequest::ContextMenu {
                path: PathBuf::from(path),
                screen_x,
                screen_y,
                dark,
                rename_label,
            });
        }

        let mode = match first.to_string_lossy().as_ref() {
            "--icon-only" => {
                first = arguments
                    .next()
                    .ok_or_else(|| "missing icon path argument".to_string())?;
                ExtractionMode::Icon
            }
            "--icon-with-overlays" => {
                first = arguments
                    .next()
                    .ok_or_else(|| "missing icon path argument".to_string())?;
                ExtractionMode::IconWithOverlays
            }
            _ => ExtractionMode::Thumbnail,
        };

        let size = arguments
            .next()
            .and_then(|value| value.to_string_lossy().parse::<i32>().ok())
            .unwrap_or(256)
            .clamp(MIN_THUMBNAIL_SIZE, MAX_THUMBNAIL_SIZE);
        if arguments.next().is_some() {
            return Err("unexpected extra arguments".to_string());
        }

        Ok(ProxyRequest::Extract {
            path: PathBuf::from(first),
            size,
            mode,
        })
    }

    fn parse_coordinate(value: Option<OsString>, name: &str) -> Result<i32, String> {
        value
            .ok_or_else(|| format!("missing context menu {name} coordinate"))?
            .to_string_lossy()
            .parse::<i32>()
            .map_err(|_| format!("invalid context menu {name} coordinate"))
    }

    fn extract_shell_image(path: &Path, size: i32, mode: ExtractionMode) -> Result<(), String> {
        write_stdout(&extract_shell_image_payload(path, size, mode)?)
    }

    /// Extracts one Shell image and returns its BMPv5 payload. COM is
    /// initialized per call, which stays balanced when a batch worker thread
    /// serves several requests (a repeated STA init returns S_FALSE).
    fn extract_shell_image_payload(
        path: &Path,
        size: i32,
        mode: ExtractionMode,
    ) -> Result<Vec<u8>, String> {
        // IShellItemImageFactory supports both files and folders. Reject only
        // missing paths so directory icons (including desktop.ini overrides)
        // use the same high-resolution path as associated file-type icons.
        if !path.exists() {
            return Err("Shell image source does not exist".to_string());
        }

        let _com_guard = initialize_com()?;

        let parsing_name: Vec<u16> = path
            .as_os_str()
            .encode_wide()
            .chain(std::iter::once(0))
            .collect();
        if matches!(mode, ExtractionMode::IconWithOverlays)
            && let Ok(bytes) = load_shell_icon_with_overlays(&parsing_name)
        {
            return Ok(bytes);
        }

        // SAFETY: The parsing name remains alive for the call and the generic
        // return type supplies the exact requested COM interface IID.
        let factory: IShellItemImageFactory =
            unsafe { SHCreateItemFromParsingName(PCWSTR(parsing_name.as_ptr()), None) }
                .map_err(|error| format!("Shell item creation failed: {error}"))?;
        let flags = match mode {
            // THUMBNAILONLY is important: returning an icon here would make a
            // missing third-party thumbnail indistinguishable from a real preview.
            ExtractionMode::Thumbnail => {
                SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK | SIIGBF_SCALEUP
            }
            // ICONONLY asks the Shell item itself to resolve PIDL/AppUserModelID
            // shortcuts. ADDOVERLAYS is deliberately omitted so DeskBox's
            // "hide shortcut arrows" setting remains effective. Do not request
            // SCALEUP here: Shell can otherwise place a 32/48 px glyph inside
            // a 256 px transparent canvas, which renders as a tiny shortcut
            // icon in the fixed file tile.
            ExtractionMode::Icon => SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK,
            // SHGetFileInfoW above is the API that supports Shell overlays. If
            // it cannot supply an icon, keep the item visible by falling back
            // to the high-resolution base icon from IShellItemImageFactory.
            ExtractionMode::IconWithOverlays => SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK,
        };
        // SAFETY: The returned bitmap is owned by the caller and released by
        // BitmapGuard after its pixels have been copied.
        let bitmap = unsafe { factory.GetImage(SIZE { cx: size, cy: size }, flags) }
            .map_err(|error| format!("Shell image extraction failed: {error}"))?;
        let bitmap_guard = BitmapGuard(bitmap);
        let bytes = bitmap_to_bmp_bytes(bitmap_guard.0)?;
        Ok(bytes)
    }

    fn load_shell_icon_with_overlays(parsing_name: &[u16]) -> Result<Vec<u8>, String> {
        // Index-first through the system image list: the JUMBO slot holds the
        // Shell-rendered base icon at up to 256 px. The overlay index has to
        // be queried through the SHGFI_ICON pairing (SHGFI_OVERLAYINDEX is
        // documented as a modifier of SHGFI_ICON and fills the upper eight
        // bits of iIcon only there), so the 32 px icon that call yields is
        // discarded and only its encoded index is kept. The overlay is then
        // drawn onto the base bitmap explicitly — the same construction the
        // legacy SHGFI_ICON | SHGFI_ADDOVERLAYS request produced, but at full
        // resolution instead of the fixed 32 px LARGEICON cap.
        let mut index_info = SHFILEINFOW::default();
        // SAFETY: parsing_name is zero terminated and index_info is valid for
        // the call. SHGFI_SYSICONINDEX fills iIcon only; no handle is handed
        // back, so there is nothing to release.
        let index_queried = unsafe {
            SHGetFileInfoW(
                PCWSTR(parsing_name.as_ptr()),
                FILE_FLAGS_AND_ATTRIBUTES(0),
                Some(&mut index_info),
                size_of::<SHFILEINFOW>() as u32,
                SHGFI_SYSICONINDEX,
            )
        } != 0;
        // A failed query leaves iIcon at 0, which would silently resolve to
        // the generic file icon in the JUMBO list; a negative index forces
        // the 32 px compatibility fallback below instead.
        let base_index = if index_queried { index_info.iIcon } else { -1 };

        let mut overlay_info = SHFILEINFOW::default();
        // SAFETY: parsing_name is zero terminated and overlay_info is valid
        // for the call; the hIcon ownership is released below.
        let overlay_queried = unsafe {
            SHGetFileInfoW(
                PCWSTR(parsing_name.as_ptr()),
                FILE_FLAGS_AND_ATTRIBUTES(0),
                Some(&mut overlay_info),
                size_of::<SHFILEINFOW>() as u32,
                SHGFI_ICON | SHGFI_LARGEICON | SHGFI_ADDOVERLAYS | SHGFI_OVERLAYINDEX,
            )
        };
        let overlay_index = if overlay_queried != 0 {
            // SHGFI_OVERLAYINDEX encodes the overlay index in the upper
            // eight bits of iIcon (0x020000CA means overlay 2, the shortcut
            // link overlay) — verified against the live API.
            (overlay_info.iIcon >> 24) & 0xFF
        } else {
            0
        };
        if overlay_queried != 0 && !overlay_info.hIcon.is_invalid() {
            // SAFETY: the handle came from SHGetFileInfoW and is not needed.
            let _ = unsafe { DestroyIcon(overlay_info.hIcon) };
        }

        if base_index >= 0 {
            // JUMBO first, then EXTRALARGE: a shortcut whose icon resource
            // lacks a genuine 256 px frame gets a small glyph centered on the
            // full JUMBO canvas (the "padded icon" case — the whole tile then
            // renders as a tiny glyph). Measuring the artwork's share of the
            // canvas detects it, and the 48 px slot serves the native frame.
            for shell_image_list in [SHIL_JUMBO as i32, SHIL_EXTRALARGE as i32] {
                // SAFETY: the image-list interface is released at the end of
                // the iteration and the icon handle is owned by IconGuard.
                let Ok(image_list) =
                    (unsafe { SHGetImageList::<IImageList>(shell_image_list) })
                else {
                    continue;
                };
                let Ok(base_icon) =
                    (unsafe { image_list.GetIcon(base_index, ILD_TRANSPARENT.0) })
                else {
                    continue;
                };
                let base = IconGuard(base_icon);

                if shell_image_list == SHIL_JUMBO as i32
                    && icon_artwork_is_padded(base.0)
                {
                    // Padded JUMBO frame — continue to the 48 px slot.
                    continue;
                }

                if let Some(bytes) =
                    composite_overlay_bitmap(&image_list, base.0, overlay_index)
                {
                    return Ok(bytes);
                }

                if let Ok(bytes) = icon_to_bmp_bytes(base.0) {
                    return Ok(bytes);
                }
            }
        }

        // Compatibility fallback: the direct 32 px overlay icon, used only
        // when the system image list could not serve the item.
        let mut file_info = SHFILEINFOW::default();
        // SAFETY: parsing_name is zero terminated and file_info is valid for
        // the duration of the call. SHGFI_ICON transfers ownership of hIcon.
        let image_list = unsafe {
            SHGetFileInfoW(
                PCWSTR(parsing_name.as_ptr()),
                FILE_FLAGS_AND_ATTRIBUTES(0),
                Some(&mut file_info),
                size_of::<SHFILEINFOW>() as u32,
                SHGFI_ICON | SHGFI_LARGEICON | SHGFI_ADDOVERLAYS,
            )
        };
        if image_list == 0 || file_info.hIcon.is_invalid() {
            return Err("Shell overlay icon extraction failed".to_string());
        }

        let icon = IconGuard(file_info.hIcon);
        icon_to_bmp_bytes(icon.0)
    }

    /// Draws the item's overlay icon (shortcut arrow and friends) onto the
    /// base icon's color bitmap and returns the composed bytes. Returns None
    /// when there is no overlay or drawing is not possible; the caller then
    /// keeps the plain base icon.
    fn composite_overlay_bitmap(
        image_list: &IImageList,
        base_icon: HICON,
        overlay_index: i32,
    ) -> Option<Vec<u8>> {
        if overlay_index <= 0 {
            return None;
        }

        // The overlay mask member itself, resolved to its image-list index.
        // SAFETY: the call only reads the list.
        let overlay_list_index = unsafe { image_list.GetOverlayImage(overlay_index) }.ok()?;
        // SAFETY: the returned icon handle is owned by IconGuard.
        let overlay_icon = unsafe { image_list.GetIcon(overlay_list_index, ILD_TRANSPARENT.0) }
            .map(IconGuard)
            .ok()?;

        // SAFETY: GetIconInfo hands out copies of the overlay icon's bitmaps;
        // the bitmaps are released by IconInfoBitmapGuard.
        let mut overlay_info = ICONINFO::default();
        unsafe { GetIconInfo(overlay_icon.0, &mut overlay_info) }.ok()?;
        let overlay_bitmaps = IconInfoBitmapGuard(overlay_info);
        if overlay_bitmaps.0.hbmColor.is_invalid() {
            return None;
        }

        let mut overlay_bitmap = BITMAP::default();
        // SAFETY: the bitmap handle is valid and outlives the read.
        if unsafe {
            GetObjectW(
                overlay_bitmaps.0.hbmColor.into(),
                size_of::<BITMAP>() as i32,
                Some((&mut overlay_bitmap) as *mut BITMAP as *mut core::ffi::c_void),
            )
        } == 0
            || overlay_bitmap.bmWidth <= 0
            || overlay_bitmap.bmHeight <= 0
        {
            return None;
        }

        // SAFETY: GetIconInfo hands out copies of the icon's bitmaps; the
        // bitmaps are released by IconInfoBitmapGuard.
        let mut icon_info = ICONINFO::default();
        unsafe { GetIconInfo(base_icon, &mut icon_info) }.ok()?;
        let bitmaps = IconInfoBitmapGuard(icon_info);
        if bitmaps.0.hbmColor.is_invalid() {
            return None;
        }

        let mut base = BITMAP::default();
        // SAFETY: the bitmap handle is valid and outlives the read.
        if unsafe {
            GetObjectW(
                bitmaps.0.hbmColor.into(),
                size_of::<BITMAP>() as i32,
                Some((&mut base) as *mut BITMAP as *mut core::ffi::c_void),
            )
        } == 0
        {
            return None;
        }

        // Explorer draws the shortcut arrow in the lower-left corner at
        // roughly a third of the icon's edge. The overlay member lives on a
        // full JUMBO canvas whose actual arrow artwork occupies only a small
        // center patch, so the source rectangle is the artwork's alpha
        // bounding box — blending the whole canvas would shrink the arrow to
        // that patch's share of the target edge.
        let overlay_source = measure_overlay_artwork(&overlay_bitmaps.0.hbmColor)
            .unwrap_or((
                0,
                0,
                overlay_bitmap.bmWidth,
                overlay_bitmap.bmHeight,
            ));
        let overlay_edge = ((base.bmWidth.max(base.bmHeight) as i32) / 3).max(16);
        let x = 0;
        let y = base.bmHeight - overlay_edge;

        // SAFETY: the DC owns nothing but the temporarily selected bitmaps,
        // which are restored before the DCs are deleted.
        let dc = unsafe { GetDC(None) };
        if dc.is_invalid() {
            return None;
        }

        let memory_dc = unsafe { CreateCompatibleDC(Some(dc)) };
        let previous = unsafe { SelectObject(memory_dc, bitmaps.0.hbmColor.into()) };
        let overlay_dc = unsafe { CreateCompatibleDC(Some(dc)) };
        let overlay_previous =
            unsafe { SelectObject(overlay_dc, overlay_bitmaps.0.hbmColor.into()) };
        let blend = BLENDFUNCTION {
            BlendOp: AC_SRC_OVER as u8,
            BlendFlags: 0,
            SourceConstantAlpha: 255,
            AlphaFormat: AC_SRC_ALPHA as u8,
        };
        // SAFETY: both DCs stay alive with their bitmaps selected for the
        // blend call; the source rectangle covers only the arrow artwork.
        let drawn = unsafe {
            AlphaBlend(
                memory_dc,
                x,
                y,
                overlay_edge,
                overlay_edge,
                overlay_dc,
                overlay_source.0,
                overlay_source.1,
                overlay_source.2,
                overlay_source.3,
                blend,
            )
        };
        unsafe {
            SelectObject(overlay_dc, overlay_previous);
            let _ = DeleteDC(overlay_dc);
            SelectObject(memory_dc, previous);
            let _ = DeleteDC(memory_dc);
            ReleaseDC(None, dc);
        }
        if !drawn.as_bool() {
            return None;
        }

        bitmap_to_bmp_bytes(bitmaps.0.hbmColor).ok()
    }

    /// True when the icon's artwork occupies only a small share of its
    /// bitmap canvas — the Shell serves a small native frame centered on a
    /// full-size canvas when the icon resource has no high-resolution entry.
    fn icon_artwork_is_padded(icon: HICON) -> bool {
        // SAFETY: GetIconInfo hands out bitmap copies that the guard frees.
        let mut icon_info = ICONINFO::default();
        if unsafe { GetIconInfo(icon, &mut icon_info) }.is_err() {
            return false;
        }

        let bitmaps = IconInfoBitmapGuard(icon_info);
        if bitmaps.0.hbmColor.is_invalid() {
            return true;
        }

        let Some((left, top, width, height)) =
            measure_overlay_artwork(&bitmaps.0.hbmColor)
        else {
            return true;
        };

        let mut canvas = BITMAP::default();
        // SAFETY: the handle is valid for the duration of the call.
        if unsafe {
            GetObjectW(
                bitmaps.0.hbmColor.into(),
                size_of::<BITMAP>() as i32,
                Some((&mut canvas) as *mut BITMAP as *mut core::ffi::c_void),
            )
        } == 0
            || canvas.bmWidth <= 0
            || canvas.bmHeight <= 0
        {
            return true;
        }

        let _ = (left, top);
        // A genuine 256 px frame fills most of its canvas; the padded frame
        // centers a glyph at roughly a 32/256 share.
        let share = (width as f64 / canvas.bmWidth as f64)
            .min(height as f64 / canvas.bmHeight as f64);
        share < 0.7
    }

    /// Measures the bounding box of the actual artwork on a 32 bpp alpha
    /// bitmap (the overlay member's arrow occupies only a small patch of its
    /// JUMBO canvas). Returns (x, y, width, height), or None when the pixels
    /// cannot be read.
    fn measure_overlay_artwork(bitmap_handle: &HBITMAP) -> Option<(i32, i32, i32, i32)> {
        let mut bitmap = BITMAP::default();
        // SAFETY: the handle is valid and the structure is writable for the
        // duration of the call.
        if unsafe {
            GetObjectW(
                (*bitmap_handle).into(),
                size_of::<BITMAP>() as i32,
                Some((&mut bitmap) as *mut BITMAP as *mut core::ffi::c_void),
            )
        } == 0
            || bitmap.bmWidth <= 0
            || bitmap.bmHeight <= 0
        {
            return None;
        }

        let width = bitmap.bmWidth;
        let height = bitmap.bmHeight;
        let pixel_byte_count = (width as usize)
            .checked_mul(height as usize)?
            .checked_mul(4)?;
        let mut pixels = vec![0u8; pixel_byte_count];
        let mut bitmap_info = BITMAPINFO::default();
        bitmap_info.bmiHeader.biSize =
            size_of::<windows::Win32::Graphics::Gdi::BITMAPINFOHEADER>() as u32;
        bitmap_info.bmiHeader.biWidth = width;
        bitmap_info.bmiHeader.biHeight = -height;
        bitmap_info.bmiHeader.biPlanes = 1;
        bitmap_info.bmiHeader.biBitCount = 32;
        bitmap_info.bmiHeader.biCompression = BI_RGB.0;
        bitmap_info.bmiHeader.biSizeImage = pixel_byte_count as u32;

        // SAFETY: the DC is balanced and pixels/bitmap_info stay valid and
        // correctly sized for the requested top-down 32-bit DIB.
        let dc = unsafe { GetDC(None) };
        if dc.is_invalid() {
            return None;
        }

        let copied_rows = unsafe {
            GetDIBits(
                dc,
                *bitmap_handle,
                0,
                height as u32,
                Some(pixels.as_mut_ptr().cast::<core::ffi::c_void>()),
                &mut bitmap_info,
                DIB_RGB_COLORS,
            )
        };
        unsafe { ReleaseDC(None, dc) };
        if copied_rows != height {
            return None;
        }

        // Top-down BGRA: the alpha channel is every fourth byte. A small
        // threshold skips decoder noise at fully transparent edges.
        const AlphaThreshold: u8 = 8;
        let mut left = width;
        let mut top = height;
        let mut right: i32 = -1;
        let mut bottom: i32 = -1;
        for row in 0..height {
            for column in 0..width {
                let alpha = pixels[(row * width + column) as usize * 4 + 3];
                if alpha >= AlphaThreshold {
                    if column < left {
                        left = column;
                    }
                    if row < top {
                        top = row;
                    }
                    if column > right {
                        right = column;
                    }
                    if row > bottom {
                        bottom = row;
                    }
                }
            }
        }

        if right < left || bottom < top {
            return None;
        }

        Some((left, top, right - left + 1, bottom - top + 1))
    }

    fn icon_to_bmp_bytes(icon: HICON) -> Result<Vec<u8>, String> {
        let mut icon_info = ICONINFO::default();
        // SAFETY: icon is valid and the output structure is initialized.
        unsafe { GetIconInfo(icon, &mut icon_info) }
            .map_err(|error| format!("unable to read Shell overlay icon: {error}"))?;
        let bitmaps = IconInfoBitmapGuard(icon_info);
        if bitmaps.0.hbmColor.is_invalid() {
            return Err("Shell overlay icon has no color bitmap".to_string());
        }

        bitmap_to_bmp_bytes(bitmaps.0.hbmColor)
    }

    fn initialize_com() -> Result<ComGuard, String> {
        // SAFETY: COM is balanced by ComGuard and every Shell interface stays
        // on this single STA thread for the lifetime of the request.
        unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) }
            .ok()
            .map_err(|error| format!("COM initialization failed: {error}"))?;
        Ok(ComGuard)
    }

    struct BatchExtractRequest {
        path: PathBuf,
        size: i32,
        mode: ExtractionMode,
    }

    /// Serves the `--extract-batch` mode: reads a count-prefixed request
    /// manifest from stdin, then extracts every request on up to
    /// EXTRACT_BATCH_MAX_WORKERS STA threads and streams one
    /// length-prefixed result frame per request to stdout as each worker
    /// finishes. One process therefore pays process creation, COM
    /// apartment setup, and Shell handler DLL loads for a whole batch
    /// instead of a single icon. Frame layout (little-endian):
    /// index u32 | status u32 (0 ok / 1 failed) | payload u32 len | payload.
    fn run_extract_batch() -> Result<i32, String> {
        let requests = read_batch_manifest(&mut io::stdin().lock())?;
        if requests.is_empty() {
            return Ok(0);
        }

        // Two measured concurrency hazards in this one process (2026-09-14):
        // 1. The Shell binds its per-process namespace state lazily; folder
        //    requests issued while that first binding is still settling fail
        //    with E_PENDING (0x8000000A) - in every batch process the first
        //    folders failed while later folders and all file requests
        //    succeeded. Prime one directory binding serially before any
        //    worker starts.
        let _ = extract_shell_image_payload(
            Path::new(CONTEXT_MENU_WARMUP_PATH),
            48,
            ExtractionMode::Icon,
        );
        // 2. Folder requests still collide with each other when issued
        //    concurrently (they failed even two at a time), so directory
        //    requests additionally serialize on this lock. File requests
        //    stay fully parallel; they never collided in any measurement.
        let directory_gate = Mutex::new(());
        let next_request = AtomicUsize::new(0);
        let completed = AtomicUsize::new(0);
        let client_gone = AtomicBool::new(false);
        std::thread::scope(|scope| {
            // Workers start lazily (feedback 226/452/495: the eager full
            // fan-out tripped CRT runtime error R6016 right after
            // sleep/resume, because every extra STA thread multiplies the
            // per-thread CRT data the Shell handler DLLs must allocate while
            // the heap is still settling). This supervisor thread adds one
            // worker per poll tick while the unclaimed queue is deeper than
            // the live worker count, never exceeding
            // EXTRACT_BATCH_MAX_WORKERS. spawn_scoped reports OS-level
            // creation failures instead of panicking, so the exhausted
            // state degrades to the live workers draining the queue; if no
            // worker can be created at all, this thread extracts serially
            // so the batch still completes.
            let mut workers: Vec<std::thread::ScopedJoinHandle<'_, ()>> = Vec::new();
            let mut spawning_exhausted = false;
            loop {
                if client_gone.load(Ordering::Relaxed) {
                    break;
                }
                workers.retain(|handle| !handle.is_finished());
                if completed.load(Ordering::Relaxed) >= requests.len() {
                    break;
                }

                let unclaimed = requests.len()
                    - next_request.load(Ordering::Relaxed).min(requests.len());
                if !spawning_exhausted
                    && workers.len() < EXTRACT_BATCH_MAX_WORKERS
                    && unclaimed > workers.len()
                {
                    match std::thread::Builder::new()
                        .name("extract-batch-worker".to_string())
                        .stack_size(EXTRACT_BATCH_WORKER_STACK_BYTES)
                        .spawn_scoped(scope, || {
                            serve_batch_requests(
                                &requests,
                                &directory_gate,
                                &next_request,
                                &completed,
                                &client_gone,
                            );
                        })
                    {
                        Ok(handle) => workers.push(handle),
                        Err(error) => {
                            // Creating one more thread failed (the same
                            // exhausted state that raises R6016 inside Shell
                            // handler DLLs). Live workers keep draining the
                            // queue; give up on further growth only.
                            eprintln!("batch worker spawn failed: {error}");
                            spawning_exhausted = true;
                        }
                    }
                }

                if workers.is_empty() {
                    if spawning_exhausted {
                        serve_batch_requests(
                            &requests,
                            &directory_gate,
                            &next_request,
                            &completed,
                            &client_gone,
                        );
                        break;
                    }

                    // A spawn is still allowed; the queue check above starts
                    // the first worker on the next pass through the loop.
                    // The tick keeps this corner from ever spinning hot.
                    std::thread::sleep(EXTRACT_BATCH_RAMP_POLL);
                    continue;
                }

                std::thread::sleep(EXTRACT_BATCH_RAMP_POLL);
            }
        });

        Ok(0)
    }

    /// Claims requests from the shared index counter until the manifest is
    /// exhausted, serializing directory requests on `directory_gate` and
    /// reporting progress through `completed` so the supervisor can decide
    /// when another worker is useful and when the whole batch is done.
    fn serve_batch_requests(
        requests: &[BatchExtractRequest],
        directory_gate: &Mutex<()>,
        next_request: &AtomicUsize,
        completed: &AtomicUsize,
        client_gone: &AtomicBool,
    ) {
        loop {
            let index = next_request.fetch_add(1, Ordering::Relaxed);
            if index >= requests.len() {
                break;
            }

            let request = &requests[index];
            let directory_guard = if request.path.is_dir() {
                Some(
                    directory_gate
                        .lock()
                        .unwrap_or_else(|poisoned| poisoned.into_inner()),
                )
            } else {
                None
            };
            let payload =
                extract_shell_image_payload(&request.path, request.size, request.mode);
            drop(directory_guard);
            let frame = match payload {
                Ok(bytes) => encode_batch_frame(index as u32, 0, &bytes),
                Err(error) => {
                    eprintln!("batch extract {index} failed: {error}");
                    encode_batch_frame(index as u32, 1, &[])
                }
            };
            if write_batch_frame(&frame).is_err() {
                // The client is gone; remaining frames are moot.
                client_gone.store(true, Ordering::Relaxed);
                return;
            }
            completed.fetch_add(1, Ordering::Relaxed);
        }
    }

    fn read_batch_manifest(input: &mut impl BufRead) -> Result<Vec<BatchExtractRequest>, String> {
        let mut header = [0u8; 12];
        input
            .read_exact(&mut header)
            .map_err(|error| format!("manifest header: {error}"))?;
        let magic = u32::from_le_bytes(header[0..4].try_into().unwrap());
        let version = u32::from_le_bytes(header[4..8].try_into().unwrap());
        let count = u32::from_le_bytes(header[8..12].try_into().unwrap());
        if magic != EXTRACT_BATCH_MAGIC {
            return Err("unsupported batch protocol magic".to_string());
        }
        if version != EXTRACT_BATCH_VERSION {
            return Err("unsupported batch protocol version".to_string());
        }
        if count == 0 || count > EXTRACT_BATCH_MAX_REQUESTS {
            return Err("batch request count out of range".to_string());
        }

        let mut requests = Vec::with_capacity(count as usize);
        for _ in 0..count {
            let mut entry = [0u8; 12];
            input
                .read_exact(&mut entry)
                .map_err(|error| format!("manifest entry: {error}"))?;
            let mode_value = u32::from_le_bytes(entry[0..4].try_into().unwrap());
            let size = u32::from_le_bytes(entry[4..8].try_into().unwrap()) as i32;
            let path_len = u32::from_le_bytes(entry[8..12].try_into().unwrap()) as usize;
            if path_len == 0 || path_len % 2 != 0 || path_len > EXTRACT_BATCH_MAX_PATH_BYTES {
                return Err("batch path length out of range".to_string());
            }

            let mut path_bytes = vec![0u8; path_len];
            input
                .read_exact(&mut path_bytes)
                .map_err(|error| format!("manifest path: {error}"))?;
            let wide: Vec<u16> = path_bytes
                .chunks_exact(2)
                .map(|pair| u16::from_le_bytes([pair[0], pair[1]]))
                .collect();
            let mode = match mode_value {
                0 => ExtractionMode::Thumbnail,
                1 => ExtractionMode::Icon,
                2 => ExtractionMode::IconWithOverlays,
                _ => return Err("unknown batch extraction mode".to_string()),
            };

            requests.push(BatchExtractRequest {
                path: PathBuf::from(OsString::from_wide(&wide)),
                size: size.clamp(MIN_THUMBNAIL_SIZE, MAX_THUMBNAIL_SIZE),
                mode,
            });
        }

        Ok(requests)
    }

    fn encode_batch_frame(index: u32, status: u32, payload: &[u8]) -> Vec<u8> {
        let mut frame = Vec::with_capacity(12 + payload.len());
        frame.extend_from_slice(&index.to_le_bytes());
        frame.extend_from_slice(&status.to_le_bytes());
        frame.extend_from_slice(&(payload.len() as u32).to_le_bytes());
        frame.extend_from_slice(payload);
        frame
    }

    fn write_batch_frame(frame: &[u8]) -> Result<(), String> {
        let stdout = io::stdout();
        let mut lock = stdout.lock();
        lock.write_all(frame)
            .and_then(|()| lock.flush())
            .map_err(|error| format!("batch stdout: {error}"))
    }

    /// Validates the batch manifest parser against a handcrafted two-request
    /// manifest. Runs as part of `--self-test`; uses explicit error returns
    /// instead of assert so a regression surfaces as exit code 4, not a
    /// panic in the shipped binary.
    fn self_test_batch_manifest() -> Result<(), String> {
        let mut manifest: Vec<u8> = Vec::new();
        manifest.extend_from_slice(&EXTRACT_BATCH_MAGIC.to_le_bytes());
        manifest.extend_from_slice(&EXTRACT_BATCH_VERSION.to_le_bytes());
        manifest.extend_from_slice(&2u32.to_le_bytes());

        let first_path: Vec<u16> = OsStr::new(r"C:\a.png").encode_wide().collect();
        manifest.extend_from_slice(&1u32.to_le_bytes()); // Icon
        manifest.extend_from_slice(&48u32.to_le_bytes());
        manifest.extend_from_slice(&((first_path.len() * 2) as u32).to_le_bytes());
        for unit in &first_path {
            manifest.extend_from_slice(&unit.to_le_bytes());
        }

        let second_path: Vec<u16> = OsStr::new(r"C:\b.jpg").encode_wide().collect();
        manifest.extend_from_slice(&0u32.to_le_bytes()); // Thumbnail
        manifest.extend_from_slice(&4096u32.to_le_bytes()); // clamps to MAX
        manifest.extend_from_slice(&((second_path.len() * 2) as u32).to_le_bytes());
        for unit in &second_path {
            manifest.extend_from_slice(&unit.to_le_bytes());
        }

        let requests = read_batch_manifest(&mut manifest.as_slice())?;
        if requests.len() != 2 {
            return Err("self-test batch manifest parsed wrong count".to_string());
        }
        if !matches!(requests[0].mode, ExtractionMode::Icon) || requests[0].size != 48 {
            return Err("self-test batch manifest parsed wrong icon request".to_string());
        }
        if !matches!(requests[1].mode, ExtractionMode::Thumbnail) ||
            requests[1].size != MAX_THUMBNAIL_SIZE
        {
            return Err("self-test batch manifest parsed wrong thumbnail request".to_string());
        }
        if requests[0].path != Path::new(r"C:\a.png") {
            return Err("self-test batch manifest parsed wrong path".to_string());
        }

        Ok(())
    }

    fn show_context_menu(
        path: &Path,
        screen_x: i32,
        screen_y: i32,
        dark: bool,
        rename_label: Option<&str>,
    ) -> Result<i32, String> {
        apply_per_monitor_dpi_awareness();
        set_process_menu_theme(dark);
        let _com_guard = initialize_com()?;
        let owner = create_context_menu_window()?;
        run_menu_on_owner(
            owner.0,
            path,
            screen_x,
            screen_y,
            dark,
            b"ready\n",
            rename_label,
        )
    }

    /// Builds, shows, and invokes the Shell context menu for one item on an
    /// already-created owner window. `announce` is written to stdout right
    /// before the menu opens (the one-shot mode handshakes with "ready", the
    /// server mode reports "shown"); every earlier failure returns Err.
    /// `rename_label`, when present, appends the host-owned rename entry.
    fn run_menu_on_owner(
        owner: HWND,
        path: &Path,
        screen_x: i32,
        screen_y: i32,
        dark: bool,
        announce: &[u8],
        rename_label: Option<&str>,
    ) -> Result<i32, String> {
        if !path.exists() {
            return Err("Shell context menu source does not exist".to_string());
        }

        apply_menu_theme(owner, dark);
        let context_menu = create_shell_context_menu(owner, path)?;
        let context_menu3 = context_menu.cast::<IContextMenu3>().ok();
        let context_menu2 = if context_menu3.is_none() {
            context_menu.cast::<IContextMenu2>().ok()
        } else {
            None
        };
        let message_handler = context_menu3
            .as_ref()
            .map(|menu| ContextMenuMessageHandler::ContextMenu3(menu.clone()))
            .or_else(|| {
                context_menu2
                    .as_ref()
                    .map(|menu| ContextMenuMessageHandler::ContextMenu2(menu.clone()))
            });
        // SAFETY: The menu is owned by MenuGuard and destroyed after all Shell
        // menu interaction is complete.
        let menu = MenuGuard(
            unsafe { CreatePopupMenu() }
                .map_err(|error| format!("Popup menu creation failed: {error}"))?,
        );
        // SAFETY: The COM interface, menu, command range, and STA all remain
        // valid for the duration of this call.
        let query_result = unsafe {
            context_menu.QueryContextMenu(
                menu.0,
                0,
                CONTEXT_MENU_FIRST_COMMAND_ID,
                CONTEXT_MENU_LAST_COMMAND_ID,
                CMF_NORMAL | CMF_EXPLORE | CMF_ITEMMENU,
            )
        };
        query_result
            .ok()
            .map_err(|error| format!("Shell menu population failed: {error}"))?;
        insert_host_rename_item(&context_menu, menu.0, rename_label)?;
        LAST_INVOKE_FAILURE_HR.store(0, Ordering::Relaxed);

        ACTIVE_CONTEXT_MENU.with(|active| {
            *active.borrow_mut() = message_handler;
        });
        struct ActiveMenuReset;
        impl Drop for ActiveMenuReset {
            fn drop(&mut self) {
                ACTIVE_CONTEXT_MENU.with(|active| {
                    *active.borrow_mut() = None;
                });
            }
        }
        let _active_menu_reset = ActiveMenuReset;

        write_stdout(announce)?;
        // TrackPopupMenuEx requires its owner to be the foreground window so an
        // outside click reliably dismisses the menu. The proxy owns this hidden
        // HWND in-process; DeskBox grants it the foreground right explicitly.
        let foreground_ok = unsafe { SetForegroundWindow(owner) }.as_bool();
        MENU_CANCELLED_BY_OUTSIDE_CLICK.store(false, Ordering::Relaxed);
        MENU_FOREGROUND_OK.store(foreground_ok, Ordering::Relaxed);
        ACTIVE_MENU_OWNER.store(owner.0 as isize, Ordering::Relaxed);
        // The hook covers what activation cannot: clicks on DeskBox's own
        // topmost, non-activating widget windows.
        let _mouse_hook = install_menu_mouse_hook();
        MENU_HOOK_INSTALLED.store(_mouse_hook.is_some(), Ordering::Relaxed);
        // TPM_NONOTIFY must NOT be set: it suppresses WM_INITMENUPOPUP, which
        // is what fills the Shell's delay-generated submenus ("Send to",
        // "Open with", archiver menus) - they would open empty. TPM_RETURNCMD
        // already routes the selection through the return value.
        let track_flags =
            TPM_RETURNCMD.0 | TPM_LEFTALIGN | TPM_RIGHTBUTTON | TPM_VERTICAL;
        // SAFETY: The proxy-owned hidden HWND is the in-process owner and its
        // window procedure forwards owner-drawn menu messages to the active
        // Shell interface.
        let selected =
            unsafe { TrackPopupMenuEx(menu.0, track_flags, screen_x, screen_y, owner, None) }.0
                as u32;
        ACTIVE_MENU_OWNER.store(0, Ordering::Relaxed);
        // The menu is closed, so the input hook has no job left. It MUST be
        // gone before InvokeCommand: a low-level hook whose owning thread is
        // blocked stops receiving mouse input and stalls the desktop input
        // pipeline for the whole blocked span.
        drop(_mouse_hook);
        // SAFETY: WM_NULL completes the documented Shell menu teardown pattern.
        let _ = unsafe { PostMessageW(Some(owner), WM_NULL, WPARAM(0), LPARAM(0)) };
        if selected == 0 {
            return Ok(CONTEXT_MENU_EXIT_CANCELLED);
        }
        if selected >= CONTEXT_MENU_CUSTOM_COMMAND_FIRST {
            // Host-owned command (rename today). The host executes it with its
            // own view-host pipeline; nothing to invoke on the Shell side.
            return Ok(CONTEXT_MENU_EXIT_CUSTOM_COMMAND);
        }
        if selected < CONTEXT_MENU_FIRST_COMMAND_ID {
            return Err("Shell returned an invalid menu command".to_string());
        }

        let command_offset = (selected - CONTEXT_MENU_FIRST_COMMAND_ID) as usize;
        // Handlers such as "Git Bash Here" read the working directory; pass the
        // item's parent in both ANSI and Unicode form because the Shell asks
        // hosts to keep valid strings of each kind alongside CMIC_MASK_UNICODE.
        let parent_path = path
            .parent()
            .filter(|parent| !parent.as_os_str().is_empty());
        let parent_wide: Vec<u16> = parent_path.map(encode_wide_path).unwrap_or_default();
        let mut parent_ansi: Vec<u8> = parent_path
            .map(|parent| parent.to_string_lossy().into_owned().into_bytes())
            .unwrap_or_default();
        parent_ansi.push(0);
        // Capture the modifier state at invocation time; Shift+Delete and
        // friends must be reported to the handler, not polled later. The
        // high-order bit of GetKeyState means the key is down.
        let shift_down = unsafe { GetKeyState(VK_SHIFT.0.into()) < 0 };
        let control_down = unsafe { GetKeyState(VK_CONTROL.0.into()) < 0 };
        let mut invoke_mask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE;
        if shift_down {
            invoke_mask |= CMIC_MASK_SHIFT_DOWN;
        }
        if control_down {
            invoke_mask |= CMIC_MASK_CONTROL_DOWN;
        }
        let invoke_info = CMINVOKECOMMANDINFOEX {
            cbSize: size_of::<CMINVOKECOMMANDINFOEX>() as u32,
            fMask: invoke_mask,
            hwnd: owner,
            lpVerb: PCSTR(command_offset as *const u8),
            lpParameters: PCSTR::null(),
            lpDirectory: if parent_ansi.len() > 1 {
                PCSTR(parent_ansi.as_ptr())
            } else {
                PCSTR::null()
            },
            nShow: SW_SHOWNORMAL.0,
            dwHotKey: 0,
            hIcon: HANDLE::default(),
            lpTitle: PCSTR::null(),
            lpVerbW: PCWSTR(command_offset as *const u16),
            lpParametersW: PCWSTR::null(),
            lpDirectoryW: if parent_wide.is_empty() {
                PCWSTR::null()
            } else {
                PCWSTR(parent_wide.as_ptr())
            },
            lpTitleW: PCWSTR::null(),
            ptInvoke: POINT {
                x: screen_x,
                y: screen_y,
            },
        };
        // SAFETY: CMINVOKECOMMANDINFOEX is ABI-compatible with the
        // CMINVOKECOMMANDINFO prefix the interface expects; numeric lpVerb
        // offsets are the documented IContextMenu ABI and the structure lives
        // through the call.
        if let Err(error) = unsafe {
            context_menu.InvokeCommand(
                (&invoke_info as *const CMINVOKECOMMANDINFOEX).cast(),
            )
        } {
            // A refused command is a classified outcome, not a proxy failure:
            // the exit code lets the host say "unsupported"/"cancelled"/
            // "permission" instead of a generic "did not complete", and the
            // HRESULT rides along in the server result line.
            let hresult = error.code().0 as u32;
            LAST_INVOKE_FAILURE_HR.store(hresult, Ordering::Relaxed);
            eprintln!("Shell command invocation failed: {error} (hr=0x{hresult:08X})");
            return Ok(classify_invoke_failure(hresult));
        }

        // Verbs that return before finishing (posted-message completion, STA
        // callbacks, dialogs parented to the owner) need a pumping thread; the
        // grace window lets that work drain instead of dying with the round.
        pump_messages(CONTEXT_MENU_INVOKE_GRACE);
        Ok(CONTEXT_MENU_EXIT_INVOKED)
    }

    /// Inserts the host-owned "rename" entry into a populated Shell menu,
    /// directly above the Shell's Properties entry when that verb can be
    /// located, so the layout matches Explorer's (... delete, rename,
    /// ---, Properties). Falls back to appending at the end when Properties
    /// cannot be identified.
    fn insert_host_rename_item(
        context_menu: &IContextMenu,
        menu: HMENU,
        rename_label: Option<&str>,
    ) -> Result<(), String> {
        insert_host_rename_item_with_verb_probe(menu, rename_label, |command_id| {
            shell_menu_item_verb(context_menu, command_id)
        })
    }

    /// Verb probe for a populated Shell menu command id. GCS_VERBW is the
    /// documented wide-verb query and works on every shell that implements
    /// IContextMenu; handlers that do not implement it simply fail, which the
    /// caller treats as "unknown item".
    fn shell_menu_item_verb(context_menu: &IContextMenu, command_id: u32) -> Option<String> {
        if command_id < CONTEXT_MENU_FIRST_COMMAND_ID || command_id > CONTEXT_MENU_LAST_COMMAND_ID
        {
            return None;
        }

        let mut buffer = [0u16; 64];
        // SAFETY: The buffer outlives the call; GCS_VERBW writes wide
        // characters through the byte-typed parameter, which is the documented
        // ABI for the wide variant.
        let queried = unsafe {
            context_menu.GetCommandString(
                (command_id - CONTEXT_MENU_FIRST_COMMAND_ID) as usize,
                GCS_VERBW,
                None,
                PSTR(buffer.as_mut_ptr().cast()),
                buffer.len() as u32,
            )
        };
        if queried.is_err() {
            return None;
        }

        let length = buffer
            .iter()
            .position(|&wide| wide == 0)
            .unwrap_or(buffer.len());
        Some(String::from_utf16_lossy(&buffer[..length]))
    }

    fn insert_host_rename_item_with_verb_probe(
        menu: HMENU,
        rename_label: Option<&str>,
        verb_of_command_id: impl Fn(u32) -> Option<String>,
    ) -> Result<(), String> {
        let Some(label) = rename_label.map(str::trim) else {
            return Ok(());
        };
        if label.is_empty() || label.len() > CONTEXT_MENU_CUSTOM_LABEL_MAX_LEN {
            return Ok(());
        }

        if let Some(index) = find_properties_item_index(menu, &verb_of_command_id) {
            // Explorer closes the edit group with a separator before
            // Properties; joining that group keeps the native layout instead
            // of stacking rename directly beneath Properties.
            let insert_at = if index > 0 && menu_item_is_separator(menu, index - 1) {
                index - 1
            } else {
                index
            };
            let mut label_buffer: Vec<u16> =
                label.encode_utf16().chain(std::iter::once(0)).collect();
            let item = MENUITEMINFOW {
                cbSize: size_of::<MENUITEMINFOW>() as u32,
                fMask: MIIM_ID | MIIM_STRING | MIIM_FTYPE,
                fType: MFT_STRING,
                wID: CONTEXT_MENU_CUSTOM_RENAME_COMMAND_ID,
                cch: (label_buffer.len().saturating_sub(1)) as u32,
                dwTypeData: PWSTR(label_buffer.as_mut_ptr()),
                ..Default::default()
            };
            // SAFETY: The info struct and label buffer stay alive for the call
            // and the index came from this same menu's item count.
            unsafe { InsertMenuItemW(menu, insert_at, true, &item) }
                .map_err(|error| format!("Host menu rename insert failed: {error}"))?;
            return Ok(());
        }

        // Properties could not be identified (a localized or minimal handler
        // that fails the verb probe, or a menu without it): keep the previous
        // end-of-menu placement.
        append_host_rename_item(menu, Some(label))
    }

    fn find_properties_item_index(
        menu: HMENU,
        verb_of_command_id: &dyn Fn(u32) -> Option<String>,
    ) -> Option<u32> {
        let count = unsafe { GetMenuItemCount(Some(menu)) };
        if count <= 0 {
            return None;
        }

        for index in 0..count as u32 {
            let mut info = MENUITEMINFOW {
                cbSize: size_of::<MENUITEMINFOW>() as u32,
                fMask: MIIM_ID,
                ..Default::default()
            };
            // SAFETY: Read-only query against a valid index of this menu.
            if !unsafe { GetMenuItemInfoW(menu, index, true, &mut info) }.is_ok() {
                continue;
            }

            let is_properties = verb_of_command_id(info.wID)
                .is_some_and(|verb| verb.eq_ignore_ascii_case("properties"));
            if is_properties {
                return Some(index);
            }
        }

        None
    }

    fn menu_item_is_separator(menu: HMENU, index: u32) -> bool {
        let mut info = MENUITEMINFOW {
            cbSize: size_of::<MENUITEMINFOW>() as u32,
            fMask: MIIM_FTYPE,
            ..Default::default()
        };
        // SAFETY: Read-only query against a valid index of this menu.
        unsafe { GetMenuItemInfoW(menu, index, true, &mut info) }.is_ok()
            && info.fType == MFT_SEPARATOR
    }

    /// Appends the host-owned "rename" entry to a populated Shell menu. The id
    /// sits above the range offered to the Shell handlers, so it can never
    /// collide. A plain text item matches the classic Win10 menu look (no
    /// icon, no owner-draw); the trailing separator is skipped when the Shell
    /// menu already ends with one, so no blank gap is doubled.
    fn append_host_rename_item(
        menu: HMENU,
        rename_label: Option<&str>,
    ) -> Result<(), String> {
        let Some(label) = rename_label.map(str::trim) else {
            return Ok(());
        };
        if label.is_empty() || label.len() > CONTEXT_MENU_CUSTOM_LABEL_MAX_LEN {
            return Ok(());
        }

        let mut separator_needed = true;
        // SAFETY: Read-only menu query; the info struct is the documented
        // MENUITEMINFOW for the last position.
        let item_count = unsafe { GetMenuItemCount(Some(menu)) };
        if item_count > 0 {
            let mut info = MENUITEMINFOW {
                cbSize: size_of::<MENUITEMINFOW>() as u32,
                fMask: MIIM_FTYPE,
                ..Default::default()
            };
            // SAFETY: The last item index is valid because item_count > 0 and
            // the menu is not concurrently modified on this STA thread.
            let query = unsafe {
                GetMenuItemInfoW(menu, (item_count - 1) as u32, true, &mut info)
            };
            if query.is_ok() && info.fType == MFT_SEPARATOR {
                separator_needed = false;
            }
        }

        if separator_needed {
            // SAFETY: Appending a separator cannot affect Shell-owned ids.
            unsafe { AppendMenuW(menu, MF_SEPARATOR, 0, PCWSTR::null()) }
                .map_err(|error| format!("Host menu separator append failed: {error}"))?;
        }

        let wide: Vec<u16> = label.encode_utf16().chain(std::iter::once(0)).collect();
        // SAFETY: The label buffer is NUL-terminated and outlives the append.
        unsafe {
            AppendMenuW(
                menu,
                Default::default(),
                CONTEXT_MENU_CUSTOM_RENAME_COMMAND_ID as usize,
                PCWSTR(wide.as_ptr()),
            )
        }
        .map_err(|error| format!("Host menu rename append failed: {error}"))?;
        Ok(())
    }

    /// Maps a failed InvokeCommand HRESULT onto the protocol's classified
    /// exit codes. E_FAIL is the documented "handler does not own this
    /// command" answer; cancelled and permission families get their own codes
    /// so the host can show a specific message instead of a generic failure.
    fn classify_invoke_failure(hresult: u32) -> i32 {
        const E_FAIL: u32 = 0x8000_4005;
        const E_ABORT: u32 = 0x8000_4004;
        const ERROR_CANCELLED_AS_HRESULT: u32 = 0x8007_007A;
        const E_ACCESSDENIED: u32 = 0x8007_0005;
        const ERROR_ELEVATION_REQUIRED_AS_HRESULT: u32 = 0x8007_02E4;
        match hresult {
            E_FAIL => CONTEXT_MENU_EXIT_FAILED_UNSUPPORTED,
            E_ABORT | ERROR_CANCELLED_AS_HRESULT => CONTEXT_MENU_EXIT_FAILED_CANCELLED,
            E_ACCESSDENIED | ERROR_ELEVATION_REQUIRED_AS_HRESULT => {
                CONTEXT_MENU_EXIT_FAILED_ACCESS_DENIED
            }
            _ => CONTEXT_MENU_EXIT_FAILED,
        }
    }

    /// Long-lived context-menu server: one STA apartment and owner window are
    /// reused across right-clicks so third-party handler DLLs load once. The
    /// process exits by itself when DeskBox closes its stdin pipe, retires
    /// after CONTEXT_MENU_SERVER_MAX_MENUS menus, or after repeated failures
    /// that suggest a poisoned apartment.
    fn run_context_menu_server(initial_dark: bool) -> Result<i32, String> {
        apply_per_monitor_dpi_awareness();
        // The process-wide theme should be in place before the first window or
        // menu exists; later rounds re-apply it when DeskBox's theme changes.
        set_process_menu_theme(initial_dark);
        let _com_guard = initialize_com()?;
        let owner = create_context_menu_window()?;
        write_stdout(b"ready\n")?;

        let (command_sender, command_receiver) = mpsc::channel::<String>();
        // The reader thread owns stdin so the STA thread below stays free to
        // dispatch messages between commands; EOF (DeskBox exited) surfaces as
        // a disconnected channel. Cancellation is handled here rather than in
        // the loop because the main thread is blocked inside TrackPopupMenuEx
        // while the menu is open, and only a posted key ends its modal loop.
        let owner_raw = owner.0.0 as isize;
        std::thread::spawn(move || {
            let stdin = io::stdin();
            let mut lines = stdin.lock();
            loop {
                let mut line = String::new();
                match lines.read_line(&mut line) {
                    Ok(0) | Err(_) => break,
                    Ok(_) => {
                        let command = normalize_command_line(&line);
                        if command == "cancel" {
                            dismiss_open_menu(owner_raw);
                            continue;
                        }
                        if command_sender.send(command.to_string()).is_err() {
                            break;
                        }
                    }
                }
            }
        });

        let mut menus_served: u32 = 0;
        let mut consecutive_failures: u32 = 0;
        loop {
            let line = match command_receiver.recv_timeout(CONTEXT_MENU_SERVER_POLL) {
                Ok(line) => line,
                Err(mpsc::RecvTimeoutError::Timeout) => {
                    pump_messages_once();
                    continue;
                }
                Err(mpsc::RecvTimeoutError::Disconnected) => break,
            };
            let command = line;
            if command == "warmup" {
                match warm_context_menu_handlers(owner.0) {
                    Ok(()) => write_stdout(b"warm ok\n")?,
                    Err(error) => {
                        eprintln!("{error}");
                        write_stdout(b"warm err\n")?;
                    }
                }
                continue;
            }
            let Some(payload) = command.strip_prefix("menu\t") else {
                // Third-party handlers may printf into the shared stdout, so
                // unknown input is not fatal. Report it anyway: a dropped
                // protocol line used to fail silently for 15s per attempt.
                if !command.is_empty() {
                    eprintln!("ignored unexpected command line: {command}");
                }
                continue;
            };
            let result_code = match parse_menu_command(payload) {
                Ok((path, screen_x, screen_y, dark, rename_label)) => {
                    match run_menu_on_owner(
                        owner.0,
                        &path,
                        screen_x,
                        screen_y,
                        dark,
                        b"shown\n",
                        rename_label.as_deref(),
                    ) {
                        Ok(code) => code,
                        Err(error) => {
                            eprintln!("{error}");
                            CONTEXT_MENU_EXIT_FAILED
                        }
                    }
                }
                Err(error) => {
                    eprintln!("{error}");
                    CONTEXT_MENU_EXIT_FAILED
                }
            };
            let mut detail = format!(
                "result {result_code} src={} fg={} hook={}",
                if MENU_CANCELLED_BY_OUTSIDE_CLICK.load(Ordering::Relaxed) {
                    "outside-click"
                } else {
                    "menu"
                },
                if MENU_FOREGROUND_OK.load(Ordering::Relaxed) {
                    1
                } else {
                    0
                },
                if MENU_HOOK_INSTALLED.load(Ordering::Relaxed) {
                    1
                } else {
                    0
                },
            );
            if result_code == CONTEXT_MENU_EXIT_CUSTOM_COMMAND {
                detail.push_str(" custom=rename");
            }
            let invoke_hr = LAST_INVOKE_FAILURE_HR.load(Ordering::Relaxed);
            if invoke_hr != 0 {
                detail.push_str(&format!(" hr=0x{invoke_hr:08X}"));
            }
            // The host reads this with a line reader; without the terminating
            // newline the round stalls until the pipe EOFs or the 10-minute
            // interaction budget expires, swallowing every later right-click.
            detail.push('\n');
            write_stdout(detail.as_bytes())?;
            menus_served += 1;
            if result_code == CONTEXT_MENU_EXIT_FAILED {
                consecutive_failures += 1;
            } else {
                consecutive_failures = 0;
            }
            if menus_served >= CONTEXT_MENU_SERVER_MAX_MENUS
                || consecutive_failures >= CONTEXT_MENU_SERVER_MAX_CONSECUTIVE_FAILURES
            {
                write_stdout(b"bye\n")?;
                return Ok(0);
            }
        }
        Ok(0)
    }

    /// Loads and populates one throwaway menu so the shared handler DLLs are
    /// warm before the first real right-click.
    fn warm_context_menu_handlers(owner: HWND) -> Result<(), String> {
        let warm_path = Path::new(CONTEXT_MENU_WARMUP_PATH);
        let context_menu = create_shell_context_menu(owner, warm_path)?;
        let menu = MenuGuard(
            unsafe { CreatePopupMenu() }
                .map_err(|error| format!("Popup menu creation failed: {error}"))?,
        );
        // SAFETY: Same population call as the interactive path; the menu is
        // destroyed immediately instead of being displayed.
        unsafe {
            context_menu.QueryContextMenu(
                menu.0,
                0,
                CONTEXT_MENU_FIRST_COMMAND_ID,
                CONTEXT_MENU_LAST_COMMAND_ID,
                CMF_NORMAL | CMF_EXPLORE | CMF_ITEMMENU,
            )
        }
        .ok()
        .map_err(|error| format!("Shell menu population failed: {error}"))
    }

    fn create_shell_context_menu(owner: HWND, path: &Path) -> Result<IContextMenu, String> {
        let parsing_name: Vec<u16> = encode_wide_path(path);
        let mut absolute_pidl: *mut ITEMIDLIST = std::ptr::null_mut();
        // SAFETY: The zero-terminated parsing name and output storage remain
        // valid for the call.
        unsafe {
            SHParseDisplayName(
                PCWSTR(parsing_name.as_ptr()),
                None,
                &mut absolute_pidl,
                0,
                None,
            )
        }
        .map_err(|error| format!("Shell item parsing failed: {error}"))?;
        if absolute_pidl.is_null() {
            return Err("Shell item parsing returned no item ID list".to_string());
        }
        let _pidl_guard = ItemIdListGuard(absolute_pidl);

        let mut child_pidl: *mut ITEMIDLIST = std::ptr::null_mut();
        // SAFETY: absolute_pidl remains alive through _pidl_guard. The returned
        // child pointer is borrowed from that allocation.
        let shell_folder: IShellFolder =
            unsafe { SHBindToParent(absolute_pidl, Some(&mut child_pidl)) }
                .map_err(|error| format!("Shell parent binding failed: {error}"))?;
        if child_pidl.is_null() {
            return Err("Shell parent binding returned no child item".to_string());
        }
        // SAFETY: child_pidl belongs to absolute_pidl and stays valid until the
        // menu and its interfaces have been released.
        unsafe { shell_folder.GetUIObjectOf(owner, &[child_pidl], None) }
            .map_err(|error| format!("Shell context menu creation failed: {error}"))
    }

    /// Strips transport noise from one protocol line. A leading byte order
    /// mark arrives when a managed writer uses the BOM-emitting UTF-8 encoder;
    /// without this the command is silently discarded as handler noise.
    fn normalize_command_line(line: &str) -> &str {
        line.trim_end_matches(['\r', '\n'])
            .trim_start_matches('\u{feff}')
    }

    /// Ends an open Shell menu from the stdin reader thread. TrackPopupMenuEx
    /// only ends its modal loop when its owner receives input, and DeskBox's
    /// widget windows are non-activating, so an outside click on them never
    /// produces the WM_ACTIVATE(WA_INACTIVE) that would otherwise dismiss the
    /// menu. Posting Escape to the owner is that input.
    fn dismiss_open_menu(owner_raw: isize) {
        let owner = HWND(owner_raw as *mut std::ffi::c_void);
        // SAFETY: The handle belongs to this process and outlives the reader
        // thread; posting a key to a window without an open menu is a no-op.
        unsafe {
            let _ = PostMessageW(Some(owner), WM_KEYDOWN, WPARAM(VK_ESCAPE.0 as usize), LPARAM(0));
            let _ = PostMessageW(Some(owner), WM_KEYUP, WPARAM(VK_ESCAPE.0 as usize), LPARAM(0));
        }
    }

    fn parse_menu_command(
        payload: &str,
    ) -> Result<(PathBuf, i32, i32, bool, Option<String>), String> {
        // "x\ty\tdark\tpath[\trename-label]". Tabs are invalid in Windows
        // paths, so splitting on the next tab cannot truncate a real path.
        let mut fields = payload.splitn(5, '\t');
        let screen_x = fields
            .next()
            .and_then(|value| value.parse::<i32>().ok())
            .ok_or("invalid context menu x coordinate")?;
        let screen_y = fields
            .next()
            .and_then(|value| value.parse::<i32>().ok())
            .ok_or("invalid context menu y coordinate")?;
        let dark = fields
            .next()
            .map(menu_dark_from_token)
            .ok_or("missing context menu theme")?;
        let path = fields
            .next()
            .filter(|value| !value.is_empty())
            .ok_or("missing context menu path")?;
        let rename_label = fields
            .next()
            .map(|value| value.to_string())
            .filter(|value| !value.trim().is_empty());
        Ok((PathBuf::from(path), screen_x, screen_y, dark, rename_label))
    }

    fn encode_wide_path(path: &Path) -> Vec<u16> {
        path.as_os_str()
            .encode_wide()
            .chain(std::iter::once(0))
            .collect()
    }

    fn apply_per_monitor_dpi_awareness() {
        // SAFETY: Called before any window exists in this process. The value
        // matches DeskBox's PerMonitorV2 host so screen coordinates and
        // owner-drawn menu content use the same physical pixels. A failure
        // keeps the system default and only degrades scaled displays.
        unsafe {
            let _ =
                SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
    }

    /// Dispatches pending messages for the STA thread once without blocking.
    fn pump_messages_once() {
        // SAFETY: The message and its windows belong to this STA thread; only
        // messages addressed to the proxy's own windows are dispatched.
        unsafe {
            let mut message = MSG::default();
            while PeekMessageW(&mut message, None, 0, 0, PM_REMOVE).as_bool() {
                let _ = TranslateMessage(&message);
                DispatchMessageW(&message);
            }
        }
    }

    /// Keeps the STA thread dispatching for `duration` so verbs that complete
    /// through posted messages or callbacks after InvokeCommand returns are
    /// not terminated mid-flight.
    fn pump_messages(duration: Duration) {
        let deadline = Instant::now() + duration;
        pump_messages_once();
        while Instant::now() < deadline {
            std::thread::sleep(CONTEXT_MENU_SERVER_POLL);
            pump_messages_once();
        }
    }

    fn create_context_menu_window() -> Result<WindowGuard, String> {
        let class_name: Vec<u16> = "DeskBoxShellContextMenuProxyWindow"
            .encode_utf16()
            .chain(std::iter::once(0))
            .collect();
        // SAFETY: The current module handle is valid for class registration and
        // hidden-window creation in this process.
        let module = unsafe { GetModuleHandleW(None) }
            .map_err(|error| format!("Module handle lookup failed: {error}"))?;
        let window_class = WNDCLASSW {
            lpfnWndProc: Some(context_menu_window_proc),
            hInstance: module.into(),
            lpszClassName: PCWSTR(class_name.as_ptr()),
            ..Default::default()
        };
        // RegisterClassW can legitimately report zero if an earlier request in
        // the same process registered the identical class; CreateWindowExW is
        // the authoritative success check.
        unsafe { RegisterClassW(&window_class) };
        // SAFETY: Class data stays alive for the call. A zero-sized, invisible
        // top-level HWND is sufficient as an in-process context-menu owner.
        // WS_EX_TOPMOST puts the owned menu popup into the TOPMOST band so it
        // renders above DeskBox's deliberately topmost widget surfaces (the
        // stack popover host is permanently topmost).
        let window = unsafe {
            CreateWindowExW(
                WS_EX_TOPMOST,
                PCWSTR(class_name.as_ptr()),
                PCWSTR::null(),
                WINDOW_STYLE::default(),
                0,
                0,
                0,
                0,
                None,
                None,
                Some(module.into()),
                None,
            )
        }
        .map_err(|error| format!("Context menu owner window creation failed: {error}"))?;
        Ok(WindowGuard(window))
    }

    unsafe extern "system" fn context_menu_window_proc(
        hwnd: HWND,
        message: u32,
        w_param: WPARAM,
        l_param: LPARAM,
    ) -> LRESULT {
        let handled = ACTIVE_CONTEXT_MENU.with(|active| {
            active
                .borrow()
                .as_ref()
                .and_then(|handler| unsafe { handler.handle(message, w_param, l_param) })
        });
        if let Some(result) = handled {
            return result;
        }

        // Delay-generated Shell submenus ("Send to", "Open with", archiver
        // menus) are filled during WM_INITMENUPOPUP. Report the resulting item
        // count: an empty flyout with count 0 means the message never reached a
        // populating handler, which is a different bug from a drawing problem.
        if message == WM_INITMENUPOPUP {
            // SAFETY: w_param of WM_INITMENUPOPUP is the submenu handle.
            let count = unsafe { GetMenuItemCount(Some(HMENU(w_param.0 as *mut c_void))) };
            eprintln!("initmenupopup items={count}");
        }

        // SAFETY: Unhandled messages are delegated to the system default window
        // procedure for the proxy-owned window.
        unsafe { DefWindowProcW(hwnd, message, w_param, l_param) }
    }

    fn is_context_menu2_message(message: u32, w_param: WPARAM) -> bool {
        message == WM_INITMENUPOPUP
            || ((message == WM_DRAWITEM || message == WM_MEASUREITEM) && w_param.0 == 0)
    }

    fn bitmap_to_bmp_bytes(bitmap_handle: HBITMAP) -> Result<Vec<u8>, String> {
        let mut bitmap = BITMAP::default();
        // SAFETY: bitmap points to writable BITMAP storage and the handle is
        // valid for the duration of this function.
        let object_size = unsafe {
            GetObjectW(
                HGDIOBJ(bitmap_handle.0),
                size_of::<BITMAP>() as i32,
                Some((&mut bitmap as *mut BITMAP).cast::<c_void>()),
            )
        };
        if object_size != size_of::<BITMAP>() as i32 || bitmap.bmWidth <= 0 || bitmap.bmHeight == 0
        {
            return Err("Shell returned an invalid image bitmap".to_string());
        }

        let width = bitmap.bmWidth;
        let height = bitmap.bmHeight.abs();
        let pixel_byte_count = (width as usize)
            .checked_mul(height as usize)
            .and_then(|value| value.checked_mul(4))
            .ok_or_else(|| "thumbnail dimensions overflowed".to_string())?;
        let mut pixels = vec![0u8; pixel_byte_count];
        let mut bitmap_info = BITMAPINFO::default();
        bitmap_info.bmiHeader.biSize =
            size_of::<windows::Win32::Graphics::Gdi::BITMAPINFOHEADER>() as u32;
        bitmap_info.bmiHeader.biWidth = width;
        bitmap_info.bmiHeader.biHeight = -height;
        bitmap_info.bmiHeader.biPlanes = 1;
        bitmap_info.bmiHeader.biBitCount = 32;
        bitmap_info.bmiHeader.biCompression = BI_RGB.0;
        bitmap_info.bmiHeader.biSizeImage = pixel_byte_count as u32;

        // SAFETY: GetDC/ReleaseDC are balanced. pixels and bitmap_info remain
        // valid and correctly sized for the requested top-down 32-bit DIB.
        let device_context = unsafe { GetDC(None) };
        if device_context.0.is_null() {
            return Err("unable to acquire a screen device context".to_string());
        }
        let copied_rows = unsafe {
            GetDIBits(
                device_context,
                bitmap_handle,
                0,
                height as u32,
                Some(pixels.as_mut_ptr().cast::<c_void>()),
                &mut bitmap_info,
                DIB_RGB_COLORS,
            )
        };
        unsafe {
            let _ = ReleaseDC(None, device_context);
        }
        if copied_rows != height {
            return Err("unable to copy Shell image pixels".to_string());
        }

        encode_bgra_as_bitmap_v5(width, height, pixels)
    }

    fn encode_bgra_as_bitmap_v5(
        width: i32,
        height: i32,
        mut pixels: Vec<u8>,
    ) -> Result<Vec<u8>, String> {
        if width <= 0 || height <= 0 || pixels.len() != width as usize * height as usize * 4 {
            return Err("invalid BGRA thumbnail payload".to_string());
        }

        // Several legacy Shell handlers return an opaque DDB with every alpha
        // byte cleared. Preserve that compatibility only when color data is
        // actually present; an all-zero bitmap is a blank result and must not
        // be promoted to an opaque black image or cached by DeskBox.
        if pixels.chunks_exact(4).all(|pixel| pixel[3] == 0) {
            if !pixels
                .chunks_exact(4)
                .any(|pixel| pixel[0] != 0 || pixel[1] != 0 || pixel[2] != 0)
            {
                return Err("Shell returned an empty transparent bitmap".to_string());
            }

            for pixel in pixels.chunks_exact_mut(4) {
                pixel[3] = 0xFF;
            }
        }

        let total_size = BITMAP_PIXEL_OFFSET
            .checked_add(pixels.len())
            .ok_or_else(|| "thumbnail payload is too large".to_string())?;
        let mut output = Vec::with_capacity(total_size);
        output.extend_from_slice(b"BM");
        write_u32(&mut output, total_size as u32);
        write_u16(&mut output, 0);
        write_u16(&mut output, 0);
        write_u32(&mut output, BITMAP_PIXEL_OFFSET as u32);

        write_u32(&mut output, BITMAP_V5_HEADER_SIZE as u32);
        write_i32(&mut output, width);
        write_i32(&mut output, -height);
        write_u16(&mut output, 1);
        write_u16(&mut output, 32);
        write_u32(&mut output, BI_BITFIELDS);
        write_u32(&mut output, pixels.len() as u32);
        write_i32(&mut output, 0);
        write_i32(&mut output, 0);
        write_u32(&mut output, 0);
        write_u32(&mut output, 0);
        write_u32(&mut output, 0x00FF_0000);
        write_u32(&mut output, 0x0000_FF00);
        write_u32(&mut output, 0x0000_00FF);
        write_u32(&mut output, 0xFF00_0000);
        write_u32(&mut output, LCS_SRGB);
        output.resize(output.len() + 36, 0);
        write_u32(&mut output, 0);
        write_u32(&mut output, 0);
        write_u32(&mut output, 0);
        write_u32(&mut output, LCS_GM_IMAGES);
        write_u32(&mut output, 0);
        write_u32(&mut output, 0);
        write_u32(&mut output, 0);
        debug_assert_eq!(output.len(), BITMAP_PIXEL_OFFSET);
        output.extend_from_slice(&pixels);
        Ok(output)
    }

    fn write_stdout(bytes: &[u8]) -> Result<(), String> {
        let stdout = io::stdout();
        let mut handle = stdout.lock();
        handle
            .write_all(bytes)
            .and_then(|_| handle.flush())
            .map_err(|error| format!("unable to write thumbnail payload: {error}"))
    }

    fn write_u16(output: &mut Vec<u8>, value: u16) {
        output.extend_from_slice(&value.to_le_bytes());
    }

    fn write_u32(output: &mut Vec<u8>, value: u32) {
        output.extend_from_slice(&value.to_le_bytes());
    }

    fn write_i32(output: &mut Vec<u8>, value: i32) {
        output.extend_from_slice(&value.to_le_bytes());
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        #[test]
        fn context_menu_request_accepts_folder_and_signed_coordinates() {
            let request = parse_request_from(
                ["--context-menu", r"C:\Desk Box", "-120", "845", "dark"]
                    .into_iter()
                    .map(OsString::from),
            )
            .expect("context menu request");

            match request {
                ProxyRequest::ContextMenu {
                    path,
                    screen_x,
                    screen_y,
                    dark,
                    ..
                } => {
                    assert_eq!(path, PathBuf::from(r"C:\Desk Box"));
                    assert_eq!(screen_x, -120);
                    assert_eq!(screen_y, 845);
                    assert!(dark);
                }
                _ => panic!("unexpected proxy request"),
            }
        }

        #[test]
        fn context_menu_request_defaults_to_light_theme() {
            let request = parse_request_from(
                ["--context-menu", r"C:\Desk Box", "1", "2"]
                    .into_iter()
                    .map(OsString::from),
            )
            .expect("context menu request");

            match request {
                ProxyRequest::ContextMenu { dark, .. } => assert!(!dark),
                _ => panic!("unexpected proxy request"),
            }
        }

        #[test]
        fn context_menu_request_rejects_missing_coordinate() {
            let error = match parse_request_from(
                ["--context-menu", r"C:\Desk Box", "120"]
                    .into_iter()
                    .map(OsString::from),
            ) {
                Err(error) => error,
                Ok(_) => panic!("missing y coordinate must be rejected"),
            };

            assert!(error.contains("missing context menu y coordinate"));
        }

        #[test]
        fn context_menu_server_request_parses_without_extra_arguments() {
            let request = parse_request_from(
                ["--context-menu-server", "dark"]
                    .into_iter()
                    .map(OsString::from),
            )
            .expect("context menu server request");

            match request {
                ProxyRequest::ContextMenuServer { dark } => assert!(dark),
                _ => panic!("unexpected proxy request"),
            }

            let light =
                parse_request_from(["--context-menu-server"].into_iter().map(OsString::from))
                    .expect("default theme");
            match light {
                ProxyRequest::ContextMenuServer { dark } => assert!(!dark),
                _ => panic!("unexpected proxy request"),
            }

            let error = parse_request_from(
                ["--context-menu-server", "dark", "extra"]
                    .into_iter()
                    .map(OsString::from),
            )
            .expect_err("extra arguments must be rejected");
            assert!(error.contains("unexpected extra arguments"));
        }

        #[test]
        fn command_line_normalization_strips_crlf_and_byte_order_mark() {
            assert_eq!(normalize_command_line("warmup\r\n"), "warmup");
            assert_eq!(normalize_command_line("\u{feff}warmup\n"), "warmup");
            assert_eq!(
                normalize_command_line("\u{feff}menu\t1\t2\tdark\tC:/file\n"),
                "menu\t1\t2\tdark\tC:/file"
            );
            assert_eq!(normalize_command_line(""), "");
        }

        #[test]
        fn menu_command_payload_accepts_signed_coordinates_and_spaced_paths() {
            let (path, screen_x, screen_y, dark, rename_label) =
                parse_menu_command("120\t-845\tdark\tC:\\Desk Box\\my file.txt")
                    .expect("menu command payload");

            assert_eq!(path, PathBuf::from(r"C:\Desk Box\my file.txt"));
            assert_eq!(screen_x, 120);
            assert_eq!(screen_y, -845);
            assert!(dark);
            assert!(rename_label.is_none(), "no label field means no custom item");
        }

        #[test]
        fn menu_command_payload_accepts_optional_rename_label() {
            let (path, screen_x, screen_y, dark, rename_label) = parse_menu_command(
                "120\t-845\tlight\tC:\\file.txt\t重命名 (&R)",
            )
            .expect("menu command payload with label");

            assert_eq!(path, PathBuf::from(r"C:\file.txt"));
            assert_eq!(screen_x, 120);
            assert_eq!(screen_y, -845);
            assert!(!dark);
            assert_eq!(
                rename_label.as_deref(),
                Some("重命名 (&R)"),
                "label may be any localized text"
            );

            let (_, _, _, _, blank) =
                parse_menu_command("120\t-845\tlight\tC:\\file.txt\t   ")
                    .expect("whitespace-only label still parses");
            assert!(blank.is_none(), "blank label means no custom item");
        }

        #[test]
        fn menu_command_payload_rejects_missing_path_and_bad_coordinates() {
            assert!(parse_menu_command("120\t845").is_err());
            assert!(parse_menu_command("x\t845\tlight\tC:\\file").is_err());
            assert!(parse_menu_command("120\t845\tlight\t").is_err());
        }

        #[test]
        fn one_shot_context_menu_request_accepts_optional_rename_label() {
            let request = parse_request_from(
                [
                    "--context-menu",
                    r"C:\file.txt",
                    "120",
                    "-845",
                    "dark",
                    "Rename",
                ]
                .into_iter()
                .map(OsString::from),
            )
            .expect("one-shot request with label");

            match request {
                ProxyRequest::ContextMenu {
                    path,
                    screen_x,
                    screen_y,
                    dark,
                    rename_label,
                } => {
                    assert_eq!(path, PathBuf::from(r"C:\file.txt"));
                    assert_eq!(screen_x, 120);
                    assert_eq!(screen_y, -845);
                    assert!(dark);
                    assert_eq!(rename_label.as_deref(), Some("Rename"));
                }
                _ => panic!("unexpected proxy request"),
            }

            let unlabeled = parse_request_from(
                ["--context-menu", r"C:\file.txt", "120", "-845", "light"]
                    .into_iter()
                    .map(OsString::from),
            )
            .expect("one-shot request without label");
            match unlabeled {
                ProxyRequest::ContextMenu { rename_label, .. } => {
                    assert!(rename_label.is_none());
                }
                _ => panic!("unexpected proxy request"),
            }

            let error = parse_request_from(
                [
                    "--context-menu",
                    r"C:\file.txt",
                    "120",
                    "-845",
                    "light",
                    "Rename",
                    "extra",
                ]
                .into_iter()
                .map(OsString::from),
            )
            .expect_err("extra arguments must still be rejected");
            assert!(error.contains("unexpected extra arguments"));
        }

        #[test]
        fn invoke_command_failures_are_classified_into_protocol_exit_codes() {
            assert_eq!(
                classify_invoke_failure(0x8000_4005),
                CONTEXT_MENU_EXIT_FAILED_UNSUPPORTED,
                "E_FAIL is the documented unrecognized-verb answer"
            );
            assert_eq!(classify_invoke_failure(0x8000_4004), CONTEXT_MENU_EXIT_FAILED_CANCELLED);
            assert_eq!(classify_invoke_failure(0x8007_007A), CONTEXT_MENU_EXIT_FAILED_CANCELLED);
            assert_eq!(classify_invoke_failure(0x8007_0005), CONTEXT_MENU_EXIT_FAILED_ACCESS_DENIED);
            assert_eq!(
                classify_invoke_failure(0x8007_02E4),
                CONTEXT_MENU_EXIT_FAILED_ACCESS_DENIED,
                "elevation required reads as a permission problem"
            );
            assert_eq!(classify_invoke_failure(0xC000_0005), CONTEXT_MENU_EXIT_FAILED);
            assert_eq!(classify_invoke_failure(0x8007_0057), CONTEXT_MENU_EXIT_FAILED);
            // The custom command id must stay outside the Shell handler range
            // so a handler can never mint it.
            assert!(CONTEXT_MENU_CUSTOM_RENAME_COMMAND_ID > CONTEXT_MENU_LAST_COMMAND_ID);
            assert_eq!(CONTEXT_MENU_EXIT_CUSTOM_COMMAND, 5);
        }

        #[test]
        fn host_rename_item_inserts_above_properties_joining_the_edit_group() {
            let menu = MenuGuard(unsafe { CreatePopupMenu() }.expect("popup menu"));
            let mut push_item = |label: &str, id: u32| {
                let wide: Vec<u16> = label.encode_utf16().chain(std::iter::once(0)).collect();
                unsafe {
                    AppendMenuW(menu.0, Default::default(), id as usize, PCWSTR(wide.as_ptr()))
                }
                .expect("seed item");
            };
            unsafe {
                AppendMenuW(menu.0, MF_SEPARATOR, 0, PCWSTR::null())
                    .expect("seed separator");
            }
            push_item("Cut", 1);
            push_item("Copy", 2);
            unsafe {
                AppendMenuW(menu.0, MF_SEPARATOR, 0, PCWSTR::null())
                    .expect("separator before properties");
            }
            push_item("Properties", 3);

            // Verb probe answers only for the Properties command id.
            insert_host_rename_item_with_verb_probe(menu.0, Some("Rename"), |command_id| {
                (command_id == 3).then(|| "properties".to_string())
            })
            .expect("rename inserted");

            let count = unsafe { GetMenuItemCount(Some(menu.0)) };
            assert_eq!(count, 6, "rename joined without adding items");
            let mut info = MENUITEMINFOW {
                cbSize: size_of::<MENUITEMINFOW>() as u32,
                fMask: MIIM_ID,
                ..Default::default()
            };
            // SAFETY: Read-only queries over valid indexes of this menu.
            unsafe { GetMenuItemInfoW(menu.0, 3, true, &mut info) }.expect("item at 3");
            assert_eq!(
                info.wID, CONTEXT_MENU_CUSTOM_RENAME_COMMAND_ID,
                "rename sits above the separator that closes the edit group"
            );
            unsafe { GetMenuItemInfoW(menu.0, 5, true, &mut info) }.expect("item at 5");
            assert_eq!(info.wID, 3, "properties stays last");
        }

        #[test]
        fn host_rename_item_inserts_directly_above_properties_without_separator() {
            let menu = MenuGuard(unsafe { CreatePopupMenu() }.expect("popup menu"));
            let wide: Vec<u16> = "Properties".encode_utf16().chain(std::iter::once(0)).collect();
            unsafe {
                AppendMenuW(menu.0, Default::default(), 7, PCWSTR(wide.as_ptr()))
                    .expect("seed properties");
            }

            insert_host_rename_item_with_verb_probe(menu.0, Some("Rename"), |command_id| {
                (command_id == 7).then(|| "Properties".to_string())
            })
            .expect("rename inserted");

            let mut info = MENUITEMINFOW {
                cbSize: size_of::<MENUITEMINFOW>() as u32,
                fMask: MIIM_ID,
                ..Default::default()
            };
            // SAFETY: Read-only query over a valid index of this menu.
            unsafe { GetMenuItemInfoW(menu.0, 0, true, &mut info) }.expect("item at 0");
            assert_eq!(
                info.wID, CONTEXT_MENU_CUSTOM_RENAME_COMMAND_ID,
                "rename lands directly above properties when no separator divides them"
            );
        }

        #[test]
        fn host_rename_item_falls_back_to_append_when_verb_probe_fails() {
            let menu = MenuGuard(unsafe { CreatePopupMenu() }.expect("popup menu"));
            let wide: Vec<u16> = "Open".encode_utf16().chain(std::iter::once(0)).collect();
            unsafe {
                AppendMenuW(menu.0, Default::default(), 1, PCWSTR(wide.as_ptr()))
                    .expect("seed item");
            }

            insert_host_rename_item_with_verb_probe(menu.0, Some("Rename"), |_| None)
                .expect("fallback append");
            let count = unsafe { GetMenuItemCount(Some(menu.0)) };
            assert_eq!(count, 3, "separator + rename appended at the end");

            let mut info = MENUITEMINFOW {
                cbSize: size_of::<MENUITEMINFOW>() as u32,
                fMask: MIIM_ID,
                ..Default::default()
            };
            // SAFETY: Read-only query over a valid index of this menu.
            unsafe { GetMenuItemInfoW(menu.0, 2, true, &mut info) }.expect("item at 2");
            assert_eq!(info.wID, CONTEXT_MENU_CUSTOM_RENAME_COMMAND_ID);
        }

        #[test]
        fn host_rename_item_appends_after_shell_items_with_separator_guard() {
            let menu = MenuGuard(unsafe { CreatePopupMenu() }.expect("popup menu"));
            // Two plain Shell-style items: separator logic must see the last
            // one is not a separator and add its own.
            unsafe {
                AppendMenuW(menu.0, MF_SEPARATOR, 0, PCWSTR::null())
                    .expect("seed separator");
                let item: Vec<u16> = "Open".encode_utf16().chain(std::iter::once(0)).collect();
                AppendMenuW(menu.0, Default::default(), 1, PCWSTR(item.as_ptr()))
                    .expect("seed item");
            }

            append_host_rename_item(menu.0, Some("Rename"))
                .expect("rename item appended");
            let count = unsafe { GetMenuItemCount(Some(menu.0)) };
            assert_eq!(count, 4, "separator + rename appended after two items");

            // Trailing separator already present: no second separator added.
            let menu2 = MenuGuard(unsafe { CreatePopupMenu() }.expect("popup menu"));
            unsafe {
                AppendMenuW(menu2.0, MF_SEPARATOR, 0, PCWSTR::null())
                    .expect("seed trailing separator");
            }
            append_host_rename_item(menu2.0, Some("Rename"))
                .expect("rename item appended");
            let count2 = unsafe { GetMenuItemCount(Some(menu2.0)) };
            assert_eq!(count2, 2, "existing trailing separator is reused");

            // No label: the menu must be untouched.
            let menu3 = MenuGuard(unsafe { CreatePopupMenu() }.expect("popup menu"));
            append_host_rename_item(menu3.0, None).expect("no label is a no-op");
            append_host_rename_item(menu3.0, Some("   ")).expect("blank label is a no-op");
            let count3 = unsafe { GetMenuItemCount(Some(menu3.0)) };
            assert_eq!(count3, 0, "no custom item without a label");
        }


        #[test]
        fn icon_with_overlays_request_has_a_distinct_extraction_mode() {
            let request = parse_request_from(
                ["--icon-with-overlays", r"C:\Desk Box\Steam.url", "256"]
                    .into_iter()
                    .map(OsString::from),
            )
            .expect("overlay icon request");

            match request {
                ProxyRequest::Extract { path, size, mode } => {
                    assert_eq!(path, PathBuf::from(r"C:\Desk Box\Steam.url"));
                    assert_eq!(size, 256);
                    assert!(matches!(mode, ExtractionMode::IconWithOverlays));
                }
                _ => panic!("unexpected proxy request"),
            }
        }

        #[test]
        fn context_menu2_message_filter_rejects_non_menu_and_control_draw_messages() {
            assert!(is_context_menu2_message(WM_INITMENUPOPUP, WPARAM(0)));
            assert!(is_context_menu2_message(WM_DRAWITEM, WPARAM(0)));
            assert!(is_context_menu2_message(WM_MEASUREITEM, WPARAM(0)));
            assert!(!is_context_menu2_message(WM_DRAWITEM, WPARAM(1)));
            assert!(!is_context_menu2_message(WM_MEASUREITEM, WPARAM(1)));
            assert!(!is_context_menu2_message(WM_MENUCHAR, WPARAM(0)));
        }

        #[test]
        fn bitmap_v5_payload_has_alpha_header_and_opaque_legacy_fallback() {
            let payload = encode_bgra_as_bitmap_v5(1, 1, vec![0x11, 0x22, 0x33, 0x00])
                .expect("bitmap payload");

            assert_eq!(&payload[0..2], b"BM");
            assert_eq!(
                u32::from_le_bytes(payload[10..14].try_into().unwrap()) as usize,
                BITMAP_PIXEL_OFFSET,
            );
            assert_eq!(
                u32::from_le_bytes(payload[54..58].try_into().unwrap()),
                0x00FF_0000,
            );
            assert_eq!(payload[BITMAP_PIXEL_OFFSET + 3], 0xFF);
        }

        #[test]
        fn bitmap_v5_payload_rejects_empty_transparent_result() {
            let error = encode_bgra_as_bitmap_v5(1, 1, vec![0x00, 0x00, 0x00, 0x00])
                .expect_err("empty transparent bitmap must be rejected");

            assert!(error.contains("empty transparent bitmap"));
        }
    }
}

#[cfg(windows)]
fn main() {
    match windows_proxy::run() {
        Ok(exit_code) => std::process::exit(exit_code),
        Err(error) => {
            eprintln!("{error}");
            std::process::exit(4);
        }
    }
}
