#[cfg(not(windows))]
fn main() {
    std::process::exit(2);
}

#[cfg(windows)]
mod windows_proxy {
    use std::{
        cell::RefCell,
        ffi::{OsString, c_void},
        io::{self, Write},
        mem::size_of,
        os::windows::ffi::OsStrExt,
        path::{Path, PathBuf},
    };

    use windows::{
        Win32::{
            Foundation::{HANDLE, HWND, LPARAM, LRESULT, SIZE, WPARAM},
            Graphics::Gdi::{
                BI_RGB, BITMAP, BITMAPINFO, DIB_RGB_COLORS, DeleteObject, GetDC, GetDIBits,
                GetObjectW, HBITMAP, HGDIOBJ, ReleaseDC,
            },
            System::{
                Com::{COINIT_APARTMENTTHREADED, CoInitializeEx, CoUninitialize},
                LibraryLoader::GetModuleHandleW,
            },
            UI::{
                Shell::{
                    CMF_EXPLORE, CMF_ITEMMENU, CMF_NORMAL, CMINVOKECOMMANDINFO, Common::ITEMIDLIST,
                    IContextMenu, IContextMenu2, IContextMenu3, IShellFolder,
                    IShellItemImageFactory, SHBindToParent, SHCreateItemFromParsingName,
                    SHParseDisplayName, SIIGBF_BIGGERSIZEOK, SIIGBF_ICONONLY, SIIGBF_SCALEUP,
                    SIIGBF_THUMBNAILONLY,
                },
                WindowsAndMessaging::{
                    CreatePopupMenu, CreateWindowExW, DefWindowProcW, DestroyMenu, DestroyWindow,
                    PostMessageW, RegisterClassW, SW_SHOWNORMAL, SetForegroundWindow, TPM_NONOTIFY,
                    TPM_RETURNCMD, TrackPopupMenuEx, WINDOW_EX_STYLE, WINDOW_STYLE, WM_DRAWITEM,
                    WM_INITMENUPOPUP, WM_MEASUREITEM, WM_MENUCHAR, WM_NULL, WNDCLASSW,
                },
            },
        },
        core::{Interface, PCSTR, PCWSTR},
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
    const CONTEXT_MENU_FIRST_COMMAND_ID: u32 = 1;
    const CONTEXT_MENU_LAST_COMMAND_ID: u32 = 0x7000;
    const TPM_LEFTALIGN: u32 = 0x0000;
    const TPM_RIGHTBUTTON: u32 = 0x0002;
    const TPM_VERTICAL: u32 = 0x0040;

    thread_local! {
        static ACTIVE_CONTEXT_MENU: RefCell<Option<ContextMenuMessageHandler>> =
            const { RefCell::new(None) };
    }

    #[derive(Clone, Copy)]
    enum ExtractionMode {
        Thumbnail,
        Icon,
    }

    enum ProxyRequest {
        SelfTest,
        Extract {
            path: PathBuf,
            size: i32,
            mode: ExtractionMode,
        },
        ContextMenu {
            path: PathBuf,
            screen_x: i32,
            screen_y: i32,
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

    pub fn run() -> Result<i32, String> {
        match parse_request()? {
            ProxyRequest::SelfTest => {
                let pixels = vec![
                    0x00, 0x00, 0xFF, 0xFF, 0x00, 0xFF, 0x00, 0xFF, 0xFF, 0x00, 0x00, 0xFF, 0xFF,
                    0xFF, 0xFF, 0xFF,
                ];
                write_stdout(&encode_bgra_as_bitmap_v5(2, 2, pixels)?)?;
                Ok(0)
            }
            ProxyRequest::Extract { path, size, mode } => {
                extract_shell_image(&path, size, mode)?;
                Ok(0)
            }
            ProxyRequest::ContextMenu {
                path,
                screen_x,
                screen_y,
            } => match show_context_menu(&path, screen_x, screen_y) {
                Ok(exit_code) => Ok(exit_code),
                Err(error) => {
                    eprintln!("{error}");
                    Ok(CONTEXT_MENU_EXIT_FAILED)
                }
            },
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
        if first == "--context-menu" {
            let path = arguments
                .next()
                .ok_or_else(|| "missing context menu path argument".to_string())?;
            let screen_x = parse_coordinate(arguments.next(), "x")?;
            let screen_y = parse_coordinate(arguments.next(), "y")?;
            if arguments.next().is_some() {
                return Err("unexpected extra arguments".to_string());
            }
            return Ok(ProxyRequest::ContextMenu {
                path: PathBuf::from(path),
                screen_x,
                screen_y,
            });
        }

        let mode = if first == "--icon-only" {
            first = arguments
                .next()
                .ok_or_else(|| "missing icon path argument".to_string())?;
            ExtractionMode::Icon
        } else {
            ExtractionMode::Thumbnail
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
        if !path.is_file() {
            return Err("Shell image source is not a file".to_string());
        }

        let _com_guard = initialize_com()?;

        let parsing_name: Vec<u16> = path
            .as_os_str()
            .encode_wide()
            .chain(std::iter::once(0))
            .collect();
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
            // "hide shortcut arrows" setting remains effective.
            ExtractionMode::Icon => SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK | SIIGBF_SCALEUP,
        };
        // SAFETY: The returned bitmap is owned by the caller and released by
        // BitmapGuard after its pixels have been copied.
        let bitmap = unsafe { factory.GetImage(SIZE { cx: size, cy: size }, flags) }
            .map_err(|error| format!("Shell image extraction failed: {error}"))?;
        let bitmap_guard = BitmapGuard(bitmap);
        let bytes = bitmap_to_bmp_bytes(bitmap_guard.0)?;
        write_stdout(&bytes)
    }

    fn initialize_com() -> Result<ComGuard, String> {
        // SAFETY: COM is balanced by ComGuard and every Shell interface stays
        // on this single STA thread for the lifetime of the request.
        unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) }
            .ok()
            .map_err(|error| format!("COM initialization failed: {error}"))?;
        Ok(ComGuard)
    }

    fn show_context_menu(path: &Path, screen_x: i32, screen_y: i32) -> Result<i32, String> {
        if !path.exists() {
            return Err("Shell context menu source does not exist".to_string());
        }

        let _com_guard = initialize_com()?;
        let owner = create_context_menu_window()?;
        let parsing_name: Vec<u16> = path
            .as_os_str()
            .encode_wide()
            .chain(std::iter::once(0))
            .collect();
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
        let context_menu: IContextMenu =
            unsafe { shell_folder.GetUIObjectOf(owner.0, &[child_pidl], None) }
                .map_err(|error| format!("Shell context menu creation failed: {error}"))?;
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

        write_stdout(b"ready\n")?;
        // TrackPopupMenuEx requires its owner to be the foreground window so an
        // outside click reliably dismisses the menu. The proxy was launched by
        // the user's menu click and owns this hidden HWND in the same process.
        let _ = unsafe { SetForegroundWindow(owner.0) };
        let track_flags =
            TPM_RETURNCMD.0 | TPM_NONOTIFY.0 | TPM_LEFTALIGN | TPM_RIGHTBUTTON | TPM_VERTICAL;
        // SAFETY: The proxy-owned hidden HWND is the in-process owner and its
        // window procedure forwards owner-drawn menu messages to the active
        // Shell interface.
        let selected =
            unsafe { TrackPopupMenuEx(menu.0, track_flags, screen_x, screen_y, owner.0, None) }.0
                as u32;
        // SAFETY: WM_NULL completes the documented Shell menu teardown pattern.
        let _ = unsafe { PostMessageW(Some(owner.0), WM_NULL, WPARAM(0), LPARAM(0)) };
        if selected == 0 {
            return Ok(CONTEXT_MENU_EXIT_CANCELLED);
        }
        if selected < CONTEXT_MENU_FIRST_COMMAND_ID {
            return Err("Shell returned an invalid menu command".to_string());
        }

        let command_offset = (selected - CONTEXT_MENU_FIRST_COMMAND_ID) as usize;
        let invoke_info = CMINVOKECOMMANDINFO {
            cbSize: size_of::<CMINVOKECOMMANDINFO>() as u32,
            fMask: 0,
            hwnd: owner.0,
            lpVerb: PCSTR(command_offset as *const u8),
            lpParameters: PCSTR::null(),
            lpDirectory: PCSTR::null(),
            nShow: SW_SHOWNORMAL.0,
            dwHotKey: 0,
            hIcon: HANDLE::default(),
        };
        // SAFETY: Numeric lpVerb offsets are the documented IContextMenu ABI;
        // the structure is complete and lives through the call.
        unsafe { context_menu.InvokeCommand(&invoke_info) }
            .map_err(|error| format!("Shell command invocation failed: {error}"))?;
        Ok(CONTEXT_MENU_EXIT_INVOKED)
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
        let window = unsafe {
            CreateWindowExW(
                WINDOW_EX_STYLE::default(),
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
                ["--context-menu", r"C:\Desk Box", "-120", "845"]
                    .into_iter()
                    .map(OsString::from),
            )
            .expect("context menu request");

            match request {
                ProxyRequest::ContextMenu {
                    path,
                    screen_x,
                    screen_y,
                } => {
                    assert_eq!(path, PathBuf::from(r"C:\Desk Box"));
                    assert_eq!(screen_x, -120);
                    assert_eq!(screen_y, 845);
                }
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
