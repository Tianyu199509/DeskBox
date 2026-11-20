use std::slice;

use windows::{
    Win32::{
        Foundation::{HWND, RPC_E_CHANGED_MODE},
        Storage::FileSystem::{GetShortPathNameW, WIN32_FIND_DATAW},
        System::Com::{
            CLSCTX_INPROC_SERVER, COINIT_APARTMENTTHREADED, CoCreateInstance, CoInitializeEx,
            CoTaskMemFree, CoUninitialize, IPersistFile, STGM_READ,
        },
        UI::Shell::{Common::ITEMIDLIST, ILFree, IShellLinkW, SHParseDisplayName, ShellLink},
    },
    core::{GUID, Interface, PCWSTR, PWSTR},
};

use crate::{
    DESKBOX_NATIVE_E_INVALIDARG, DESKBOX_NATIVE_HRESULT_INSUFFICIENT_BUFFER,
    DESKBOX_NATIVE_S_FALSE, DESKBOX_NATIVE_S_OK, DESKBOX_NATIVE_STATUS_BUFFER_TOO_SMALL,
    DESKBOX_NATIVE_STATUS_COM_INITIALIZATION_FAILED, DESKBOX_NATIVE_STATUS_INVALID_ARGUMENT,
    DESKBOX_NATIVE_STATUS_LOAD_FAILED, DESKBOX_NATIVE_STATUS_OBJECT_CREATION_FAILED,
    DESKBOX_NATIVE_STATUS_OK, DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
    DESKBOX_SHORTCUT_FIELD_ARGUMENTS, DESKBOX_SHORTCUT_FIELD_DESCRIPTION,
    DESKBOX_SHORTCUT_FIELD_ICON_PATH, DESKBOX_SHORTCUT_FIELD_TARGET_PATH,
    DESKBOX_SHORTCUT_FIELD_WORKING_DIRECTORY, DESKBOX_SHORTCUT_PHASE_COM_INITIALIZE,
    DESKBOX_SHORTCUT_PHASE_CREATE_OBJECT, DESKBOX_SHORTCUT_PHASE_LOAD,
    DESKBOX_SHORTCUT_PHASE_RESOLVE, DESKBOX_SHORTCUT_PHASE_SAVE,
    DESKBOX_SHORTCUT_READ_MODE_EFFECTIVE_DIAGNOSTIC, DESKBOX_SHORTCUT_READ_MODE_STORED_RAW,
    DESKBOX_SHORTCUT_WRITE_FLAG_SHELL_NAMESPACE_TARGET, DeskBoxNativeUtf16BufferV1,
    DeskBoxNativeUtf16StringV1, DeskBoxShortcutReadRequestV2, DeskBoxShortcutReadResultV2,
    DeskBoxShortcutUiResolveRequestV2, DeskBoxShortcutUiResolveResultV2,
    DeskBoxShortcutWriteRequestV2, DeskBoxShortcutWriteResultV2,
};

const STORED_FIELD_CAPACITY: usize = 260;
const ARGUMENT_FIELD_CAPACITY: usize = 4096;
const DIAGNOSTIC_ARGUMENT_CAPACITY: usize = ARGUMENT_FIELD_CAPACITY;
const SLGP_RAWPATH: u32 = 0x0004;
const SLR_NO_UI: u32 = 0x0001;
const SLR_UPDATE: u32 = 0x0004;
const SLR_NOSEARCH: u32 = 0x0010;
const SLR_OFFER_DELETE_WITHOUT_FILE: u32 = 0x0200;
const WIN32_PATH_THRESHOLD: usize = 248;
const SHELL_LINK_LONG_PATH_THRESHOLD: usize = 258;
const VT_LPWSTR: u16 = 31;

const IID_IPROPERTY_STORE: GUID = GUID::from_u128(0x886d8eeb_8cf2_4446_8d02_cdba1dbdcf99);
const FMTID_LINK_TARGET_PARSING_PATH: GUID =
    GUID::from_u128(0xb9b4b3fc_2b51_4a42_b5d8_324146afcf25);

#[repr(C)]
struct PropertyKey {
    fmtid: GUID,
    pid: u32,
}

#[repr(C)]
struct PropVariantLpwstr {
    vt: u16,
    reserved1: u16,
    reserved2: u16,
    reserved3: u16,
    ptr: *const u16,
    padding: usize,
}

struct ComRawGuard(*mut core::ffi::c_void);

impl Drop for ComRawGuard {
    fn drop(&mut self) {
        if !self.0.is_null() {
            // SAFETY: COM vtable slot 2 is always IUnknown::Release.
            unsafe {
                let vtbl = *(self.0 as *const *const *const core::ffi::c_void);
                let release: unsafe extern "system" fn(*mut core::ffi::c_void) -> u32 =
                    core::mem::transmute(*vtbl.add(2));
                let _ = release(self.0);
            }
        }
    }
}

fn to_extended_dos_path(nul_terminated: &[u16]) -> Option<Vec<u16>> {
    let body = nul_terminated.strip_suffix(&[0]).unwrap_or(nul_terminated);
    if body.len() < 3 {
        return None;
    }

    // Already \\?\ prefixed.
    if body.len() >= 4
        && body[0] == b'\\' as u16
        && body[1] == b'\\' as u16
        && body[2] == b'?' as u16
        && body[3] == b'\\' as u16
    {
        return None;
    }

    // Drive-letter absolute path: X:\... -> \\?\X:\...
    let is_drive = ((body[0] >= b'A' as u16 && body[0] <= b'Z' as u16)
        || (body[0] >= b'a' as u16 && body[0] <= b'z' as u16))
        && body[1] == b':' as u16
        && (body[2] == b'\\' as u16 || body[2] == b'/' as u16);
    if is_drive {
        let mut out = Vec::with_capacity(4 + body.len() + 1);
        out.extend_from_slice(&[b'\\' as u16, b'\\' as u16, b'?' as u16, b'\\' as u16]);
        for &ch in body {
            out.push(if ch == b'/' as u16 { b'\\' as u16 } else { ch });
        }
        out.push(0);
        return Some(out);
    }

    // UNC path: \\server\share -> \\?\UNC\server\share
    if body[0] == b'\\' as u16 && body[1] == b'\\' as u16 && body[2] != b'\\' as u16 {
        let mut out = Vec::with_capacity(8 + body.len() + 1);
        out.extend_from_slice(&[
            b'\\' as u16,
            b'\\' as u16,
            b'?' as u16,
            b'\\' as u16,
            b'U' as u16,
            b'N' as u16,
            b'C' as u16,
            b'\\' as u16,
        ]);
        for &ch in &body[2..] {
            out.push(if ch == b'/' as u16 { b'\\' as u16 } else { ch });
        }
        out.push(0);
        return Some(out);
    }

    None
}

unsafe fn try_load_persist_file(persist_file: &IPersistFile, path: &[u16]) -> windows::core::HRESULT {
    // SAFETY: Caller passes valid COM interface and NUL-terminated path slice.
    let hr = unsafe {
        (Interface::vtable(persist_file).Load)(
            Interface::as_raw(persist_file),
            PCWSTR(path.as_ptr()),
            STGM_READ,
        )
    };
    if hresult_succeeded(hr.0) {
        return hr;
    }

    let char_len = path.len().saturating_sub(1);
    if char_len >= WIN32_PATH_THRESHOLD
        && let Some(extended) = to_extended_dos_path(path)
    {
        // SAFETY: Extended path buffer is NUL-terminated and alive for the call.
        return unsafe {
            (Interface::vtable(persist_file).Load)(
                Interface::as_raw(persist_file),
                PCWSTR(extended.as_ptr()),
                STGM_READ,
            )
        };
    }

    hr
}

unsafe fn try_stamp_long_target_parsing_property(shell_link: &IShellLinkW, target_path: &[u16]) {
    let raw_link = Interface::as_raw(shell_link);
    let mut prop_store: *mut core::ffi::c_void = core::ptr::null_mut();
    // SAFETY: Vtable slot 0 is IUnknown::QueryInterface on IShellLinkW.
    let qi_hr: i32 = unsafe {
        let vtbl = *(raw_link as *const *const *const core::ffi::c_void);
        let qi: unsafe extern "system" fn(
            *mut core::ffi::c_void,
            *const GUID,
            *mut *mut core::ffi::c_void,
        ) -> i32 = core::mem::transmute(*vtbl);
        qi(raw_link, &IID_IPROPERTY_STORE, &mut prop_store)
    };
    if qi_hr < 0 || prop_store.is_null() {
        return;
    }

    let _guard = ComRawGuard(prop_store);
    let key = PropertyKey {
        fmtid: FMTID_LINK_TARGET_PARSING_PATH,
        pid: 2,
    };
    let pv = PropVariantLpwstr {
        vt: VT_LPWSTR,
        reserved1: 0,
        reserved2: 0,
        reserved3: 0,
        ptr: target_path.as_ptr(),
        padding: 0,
    };
    // SAFETY: IPropertyStore slot 6 is SetValue and slot 7 is Commit.
    unsafe {
        let vtbl = *(prop_store as *const *const *const core::ffi::c_void);
        let set_value: unsafe extern "system" fn(
            *mut core::ffi::c_void,
            *const PropertyKey,
            *const PropVariantLpwstr,
        ) -> i32 = core::mem::transmute(*vtbl.add(6));
        if set_value(prop_store, &key, &pv) >= 0 {
            let commit: unsafe extern "system" fn(*mut core::ffi::c_void) -> i32 =
                core::mem::transmute(*vtbl.add(7));
            let _ = commit(prop_store);
        }
    }
}

unsafe fn try_read_long_target_parsing_property(shell_link: &IShellLinkW) -> Option<Vec<u16>> {
    const MAX_LONG_TARGET_CHARS: usize = 32_768;

    let raw_link = Interface::as_raw(shell_link);
    let mut prop_store: *mut core::ffi::c_void = core::ptr::null_mut();
    // SAFETY: Vtable slot 0 is IUnknown::QueryInterface on IShellLinkW.
    let qi_hr: i32 = unsafe {
        let vtbl = *(raw_link as *const *const *const core::ffi::c_void);
        let qi: unsafe extern "system" fn(
            *mut core::ffi::c_void,
            *const GUID,
            *mut *mut core::ffi::c_void,
        ) -> i32 = core::mem::transmute(*vtbl);
        qi(raw_link, &IID_IPROPERTY_STORE, &mut prop_store)
    };
    if qi_hr < 0 || prop_store.is_null() {
        return None;
    }

    let _guard = ComRawGuard(prop_store);
    let key = PropertyKey {
        fmtid: FMTID_LINK_TARGET_PARSING_PATH,
        pid: 2,
    };
    let mut pv = PropVariantLpwstr {
        vt: 0,
        reserved1: 0,
        reserved2: 0,
        reserved3: 0,
        ptr: core::ptr::null(),
        padding: 0,
    };
    // SAFETY: IPropertyStore slot 5 is GetValue.
    let get_hr: i32 = unsafe {
        let vtbl = *(prop_store as *const *const *const core::ffi::c_void);
        let get_value: unsafe extern "system" fn(
            *mut core::ffi::c_void,
            *const PropertyKey,
            *mut PropVariantLpwstr,
        ) -> i32 = core::mem::transmute(*vtbl.add(5));
        get_value(prop_store, &key, &mut pv)
    };
    if get_hr < 0 {
        return None;
    }
    if pv.vt != VT_LPWSTR || pv.ptr.is_null() {
        #[link(name = "ole32")]
        unsafe extern "system" {
            fn PropVariantClear(pvar: *mut PropVariantLpwstr) -> i32;
        }
        unsafe {
            let _ = PropVariantClear(&mut pv);
        }
        return None;
    }

    let mut len = 0_usize;
    // SAFETY: VT_LPWSTR is a NUL-terminated UTF-16 buffer allocated via CoTaskMemAlloc.
    unsafe {
        while len < MAX_LONG_TARGET_CHARS && *pv.ptr.add(len) != 0 {
            len += 1;
        }
    }

    let out = if len >= SHELL_LINK_LONG_PATH_THRESHOLD && len < MAX_LONG_TARGET_CHARS {
        let mut buf = Vec::with_capacity(len + 2);
        // SAFETY: Validated `len` UTF-16 code units before the NUL terminator.
        let chars = unsafe { slice::from_raw_parts(pv.ptr, len) };
        buf.extend_from_slice(chars);
        // Append two NULs so `capture_field` observes `source_length < source.len() - 1`
        // and knows the value was not truncated by a fixed source buffer.
        buf.push(0);
        buf.push(0);
        Some(buf)
    } else {
        None
    };

    // SAFETY: Free the CoTaskMemAlloc-backed LPWSTR returned in PROPVARIANT.
    unsafe { CoTaskMemFree(Some(pv.ptr.cast())) };
    out
}

unsafe fn try_get_short_path(path: &[u16]) -> Option<Vec<u16>> {
    let extended = to_extended_dos_path(path);
    let query_ptr = extended
        .as_ref()
        .map_or(path.as_ptr(), |buf| buf.as_ptr());
    let mut short_buf = [0_u16; 320];
    // SAFETY: Both input and output buffers are NUL-terminated / sized accurately.
    let written = unsafe { GetShortPathNameW(PCWSTR(query_ptr), Some(&mut short_buf)) };
    if written > 0 && (written as usize) < short_buf.len() {
        let mut out = short_buf[..written as usize].to_vec();
        out.push(0);
        return Some(out);
    }

    None
}

struct ComUninitializeGuard {
    active: bool,
}

struct ItemIdListGuard(*mut ITEMIDLIST);

impl Drop for ItemIdListGuard {
    fn drop(&mut self) {
        if !self.0.is_null() {
            // SAFETY: SHParseDisplayName allocated this PIDL for the caller.
            unsafe { ILFree(Some(self.0)) };
        }
    }
}

impl Drop for ComUninitializeGuard {
    fn drop(&mut self) {
        if self.active {
            // SAFETY: Each active guard represents one successful CoInitializeEx call.
            unsafe { CoUninitialize() };
        }
    }
}

#[derive(Clone, Copy)]
enum ShortcutField {
    Target,
    Description,
    Arguments,
    WorkingDirectory,
    Icon,
}

impl ShortcutField {
    const fn mask(self) -> u32 {
        match self {
            Self::Target => DESKBOX_SHORTCUT_FIELD_TARGET_PATH,
            Self::Description => DESKBOX_SHORTCUT_FIELD_DESCRIPTION,
            Self::Arguments => DESKBOX_SHORTCUT_FIELD_ARGUMENTS,
            Self::WorkingDirectory => DESKBOX_SHORTCUT_FIELD_WORKING_DIRECTORY,
            Self::Icon => DESKBOX_SHORTCUT_FIELD_ICON_PATH,
        }
    }

    fn output(self, request: &DeskBoxShortcutReadRequestV2) -> &DeskBoxNativeUtf16BufferV1 {
        match self {
            Self::Target => &request.target_path,
            Self::Description => &request.description,
            Self::Arguments => &request.arguments,
            Self::WorkingDirectory => &request.working_directory,
            Self::Icon => &request.icon_path,
        }
    }

    fn set_hresult(self, result: &mut DeskBoxShortcutReadResultV2, hresult: i32) {
        match self {
            Self::Target => result.target_hresult = hresult,
            Self::Description => result.description_hresult = hresult,
            Self::Arguments => result.arguments_hresult = hresult,
            Self::WorkingDirectory => result.working_directory_hresult = hresult,
            Self::Icon => result.icon_hresult = hresult,
        }
    }

    fn set_write_hresult(self, result: &mut DeskBoxShortcutWriteResultV2, hresult: i32) {
        match self {
            Self::Target => result.target_hresult = hresult,
            Self::Description => result.description_hresult = hresult,
            Self::Arguments => result.arguments_hresult = hresult,
            Self::WorkingDirectory => result.working_directory_hresult = hresult,
            Self::Icon => result.icon_hresult = hresult,
        }
    }

    fn set_required_chars(self, result: &mut DeskBoxShortcutReadResultV2, required_chars: u32) {
        match self {
            Self::Target => result.target_required_chars = required_chars,
            Self::Description => result.description_required_chars = required_chars,
            Self::Arguments => result.arguments_required_chars = required_chars,
            Self::WorkingDirectory => result.working_directory_required_chars = required_chars,
            Self::Icon => result.icon_required_chars = required_chars,
        }
    }
}

pub(crate) unsafe fn read_shortcut(
    request: &DeskBoxShortcutReadRequestV2,
    result: &mut DeskBoxShortcutReadResultV2,
) -> u32 {
    // SAFETY: The exported ABI validated every pointer and buffer in the request.
    unsafe { execute_shortcut(request, result, None) }
}

pub(crate) unsafe fn resolve_shortcut(
    request: &DeskBoxShortcutReadRequestV2,
    result: &mut DeskBoxShortcutReadResultV2,
    timeout_ms: u32,
) -> u32 {
    // SAFETY: The exported ABI validated every pointer and buffer in the request.
    unsafe { execute_shortcut(request, result, Some(timeout_ms)) }
}

pub(crate) unsafe fn resolve_shortcut_with_ui(
    request: &DeskBoxShortcutUiResolveRequestV2,
    result: &mut DeskBoxShortcutUiResolveResultV2,
) -> u32 {
    // SAFETY: The exported ABI validated the input pointer and length.
    let shortcut_path = match unsafe { nul_terminated_input(&request.shortcut_path) } {
        Ok(value) => value,
        Err(hresult) => {
            return finish_ui_resolve(result, DESKBOX_NATIVE_STATUS_INVALID_ARGUMENT, hresult);
        }
    };

    result.resolve_flags = resolve_with_ui_flags();
    result.attempted_phases |= DESKBOX_SHORTCUT_PHASE_COM_INITIALIZE;
    // SAFETY: COM initialization is balanced by the guard for S_OK and S_FALSE.
    let com_hresult = unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) };
    result.com_hresult = com_hresult.0;
    let should_uninitialize = hresult_succeeded(com_hresult.0);
    if !should_uninitialize && com_hresult.0 != RPC_E_CHANGED_MODE.0 {
        return finish_ui_resolve(
            result,
            DESKBOX_NATIVE_STATUS_COM_INITIALIZATION_FAILED,
            com_hresult.0,
        );
    }

    let _com_guard = ComUninitializeGuard {
        active: should_uninitialize,
    };

    result.attempted_phases |= DESKBOX_SHORTCUT_PHASE_CREATE_OBJECT;
    // SAFETY: The CLSID and requested interface are generated by windows-rs.
    let shell_link: IShellLinkW =
        match unsafe { CoCreateInstance(&ShellLink, None, CLSCTX_INPROC_SERVER) } {
            Ok(value) => value,
            Err(error) => {
                result.create_hresult = error.code().0;
                return finish_ui_resolve(
                    result,
                    DESKBOX_NATIVE_STATUS_OBJECT_CREATION_FAILED,
                    error.code().0,
                );
            }
        };

    let persist_file: IPersistFile = match shell_link.cast() {
        Ok(value) => value,
        Err(error) => {
            result.create_hresult = error.code().0;
            return finish_ui_resolve(
                result,
                DESKBOX_NATIVE_STATUS_OBJECT_CREATION_FAILED,
                error.code().0,
            );
        }
    };
    result.create_hresult = DESKBOX_NATIVE_S_OK;

    result.attempted_phases |= DESKBOX_SHORTCUT_PHASE_LOAD;
    // SAFETY: The path is NUL-terminated and remains alive for the call.
    let load_hresult = unsafe { try_load_persist_file(&persist_file, &shortcut_path) };
    result.load_hresult = load_hresult.0;
    if !hresult_succeeded(load_hresult.0) {
        return finish_ui_resolve(result, DESKBOX_NATIVE_STATUS_LOAD_FAILED, load_hresult.0);
    }

    result.attempted_phases |= DESKBOX_SHORTCUT_PHASE_RESOLVE;
    // SAFETY: Resolve is synchronous. The caller owns the HWND and guarantees
    // it is suitable for the duration of this call; zero remains permitted for
    // compatibility with the Win32 API.
    let resolve_hresult = unsafe {
        (Interface::vtable(&shell_link).Resolve)(
            Interface::as_raw(&shell_link),
            HWND(request.owner_hwnd as *mut core::ffi::c_void),
            result.resolve_flags,
        )
    };
    result.resolve_hresult = resolve_hresult.0;
    if !hresult_succeeded(resolve_hresult.0) {
        return finish_ui_resolve(
            result,
            DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
            resolve_hresult.0,
        );
    }

    finish_ui_resolve(result, DESKBOX_NATIVE_STATUS_OK, DESKBOX_NATIVE_S_OK)
}

pub(crate) unsafe fn write_shortcut(
    request: &DeskBoxShortcutWriteRequestV2,
    result: &mut DeskBoxShortcutWriteResultV2,
) -> u32 {
    // SAFETY: The exported ABI validated every input pointer and length.
    let shortcut_path = match unsafe { nul_terminated_input(&request.shortcut_path) } {
        Ok(value) => value,
        Err(hresult) => {
            return finish_write(result, DESKBOX_NATIVE_STATUS_INVALID_ARGUMENT, hresult);
        }
    };
    // SAFETY: The exported ABI validated every input pointer and length.
    let target_path = match unsafe { nul_terminated_input(&request.target_path) } {
        Ok(value) => value,
        Err(hresult) => {
            return finish_write(result, DESKBOX_NATIVE_STATUS_INVALID_ARGUMENT, hresult);
        }
    };
    // SAFETY: The exported ABI validated every input pointer and length.
    let description = match unsafe { nul_terminated_input(&request.description) } {
        Ok(value) => value,
        Err(hresult) => {
            return finish_write(result, DESKBOX_NATIVE_STATUS_INVALID_ARGUMENT, hresult);
        }
    };
    // SAFETY: The exported ABI validated every input pointer and length.
    let arguments = match unsafe { nul_terminated_input(&request.arguments) } {
        Ok(value) => value,
        Err(hresult) => {
            return finish_write(result, DESKBOX_NATIVE_STATUS_INVALID_ARGUMENT, hresult);
        }
    };
    // SAFETY: The exported ABI validated every input pointer and length.
    let working_directory = match unsafe { nul_terminated_input(&request.working_directory) } {
        Ok(value) => value,
        Err(hresult) => {
            return finish_write(result, DESKBOX_NATIVE_STATUS_INVALID_ARGUMENT, hresult);
        }
    };
    // SAFETY: The exported ABI validated every input pointer and length.
    let icon_path = match unsafe { nul_terminated_input(&request.icon_path) } {
        Ok(value) => value,
        Err(hresult) => {
            return finish_write(result, DESKBOX_NATIVE_STATUS_INVALID_ARGUMENT, hresult);
        }
    };

    result.attempted_phases |= DESKBOX_SHORTCUT_PHASE_COM_INITIALIZE;
    // SAFETY: COM initialization is balanced by the guard for S_OK and S_FALSE.
    let com_hresult = unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) };
    result.com_hresult = com_hresult.0;
    let should_uninitialize = hresult_succeeded(com_hresult.0);
    if !should_uninitialize && com_hresult.0 != RPC_E_CHANGED_MODE.0 {
        return finish_write(
            result,
            DESKBOX_NATIVE_STATUS_COM_INITIALIZATION_FAILED,
            com_hresult.0,
        );
    }

    let _com_guard = ComUninitializeGuard {
        active: should_uninitialize,
    };

    result.attempted_phases |= DESKBOX_SHORTCUT_PHASE_CREATE_OBJECT;
    // SAFETY: The CLSID and requested interface are generated by windows-rs.
    let shell_link: IShellLinkW =
        match unsafe { CoCreateInstance(&ShellLink, None, CLSCTX_INPROC_SERVER) } {
            Ok(value) => value,
            Err(error) => {
                result.create_hresult = error.code().0;
                return finish_write(
                    result,
                    DESKBOX_NATIVE_STATUS_OBJECT_CREATION_FAILED,
                    error.code().0,
                );
            }
        };

    let persist_file: IPersistFile = match shell_link.cast() {
        Ok(value) => value,
        Err(error) => {
            result.create_hresult = error.code().0;
            return finish_write(
                result,
                DESKBOX_NATIVE_STATUS_OBJECT_CREATION_FAILED,
                error.code().0,
            );
        }
    };
    result.create_hresult = DESKBOX_NATIVE_S_OK;

    let shell_namespace_target =
        request.flags & DESKBOX_SHORTCUT_WRITE_FLAG_SHELL_NAMESPACE_TARGET != 0;
    let is_long_target =
        !shell_namespace_target && target_path.len().saturating_sub(1) >= SHELL_LINK_LONG_PATH_THRESHOLD;
    let target_hresult = if shell_namespace_target {
        let mut item_id_list: *mut ITEMIDLIST = std::ptr::null_mut();
        // SAFETY: The parsing name is NUL-terminated and the output pointer is
        // valid for the duration of the synchronous Shell call.
        match unsafe {
            SHParseDisplayName(
                PCWSTR(target_path.as_ptr()),
                None,
                &mut item_id_list,
                0,
                None,
            )
        } {
            Ok(()) if !item_id_list.is_null() => {
                let _item_id_list_guard = ItemIdListGuard(item_id_list);
                // SAFETY: The generated vtable signature matches
                // IShellLinkW::SetIDList and the PIDL remains alive for the call.
                unsafe {
                    (Interface::vtable(&shell_link).SetIDList)(
                        Interface::as_raw(&shell_link),
                        item_id_list,
                    )
                }
            }
            Ok(()) => windows::core::HRESULT(DESKBOX_NATIVE_E_INVALIDARG),
            Err(error) => error.code(),
        }
    } else if is_long_target {
        let mut item_id_list: *mut ITEMIDLIST = std::ptr::null_mut();
        // SAFETY: Both target_path and extended buffer are NUL-terminated for SHParseDisplayName.
        let parsed = unsafe {
            SHParseDisplayName(
                PCWSTR(target_path.as_ptr()),
                None,
                &mut item_id_list,
                0,
                None,
            )
        };
        if (parsed.is_err() || item_id_list.is_null())
            && let Some(extended_target) = to_extended_dos_path(&target_path)
        {
            item_id_list = std::ptr::null_mut();
            let _ = unsafe {
                SHParseDisplayName(
                    PCWSTR(extended_target.as_ptr()),
                    None,
                    &mut item_id_list,
                    0,
                    None,
                )
            };
        }

        if !item_id_list.is_null() {
            let _item_id_list_guard = ItemIdListGuard(item_id_list);
            // SAFETY: IShellLinkW::SetIDList accepts the PIDL parsed above.
            let id_hr = unsafe {
                (Interface::vtable(&shell_link).SetIDList)(
                    Interface::as_raw(&shell_link),
                    item_id_list,
                )
            };
            if hresult_succeeded(id_hr.0) {
                unsafe { try_stamp_long_target_parsing_property(&shell_link, &target_path) };
            }
            id_hr
        } else {
            let path_hr = unsafe {
                (Interface::vtable(&shell_link).SetPath)(
                    Interface::as_raw(&shell_link),
                    PCWSTR(target_path.as_ptr()),
                )
            };
            if hresult_succeeded(path_hr.0) {
                unsafe { try_stamp_long_target_parsing_property(&shell_link, &target_path) };
                path_hr
            } else if let Some(extended_target) = to_extended_dos_path(&target_path) {
                let ext_hr = unsafe {
                    (Interface::vtable(&shell_link).SetPath)(
                        Interface::as_raw(&shell_link),
                        PCWSTR(extended_target.as_ptr()),
                    )
                };
                if hresult_succeeded(ext_hr.0) {
                    unsafe { try_stamp_long_target_parsing_property(&shell_link, &target_path) };
                }
                ext_hr
            } else {
                path_hr
            }
        }
    } else {
        // SAFETY: The target path is NUL-terminated and alive for the call.
        unsafe {
            (Interface::vtable(&shell_link).SetPath)(
                Interface::as_raw(&shell_link),
                PCWSTR(target_path.as_ptr()),
            )
        }
    };
    if let Err(hresult) = record_write_field(result, ShortcutField::Target, target_hresult.0) {
        return finish_write(result, DESKBOX_NATIVE_STATUS_OPERATION_FAILED, hresult);
    }

    // SAFETY: Each UTF-16 value is NUL-terminated and alive for the call.
    let description_hresult = unsafe {
        (Interface::vtable(&shell_link).SetDescription)(
            Interface::as_raw(&shell_link),
            PCWSTR(description.as_ptr()),
        )
    };
    if let Err(hresult) =
        record_write_field(result, ShortcutField::Description, description_hresult.0)
    {
        return finish_write(result, DESKBOX_NATIVE_STATUS_OPERATION_FAILED, hresult);
    }

    if !shell_namespace_target {
        // SAFETY: Each UTF-16 value is NUL-terminated and alive for the call.
        let arguments_hresult = unsafe {
            (Interface::vtable(&shell_link).SetArguments)(
                Interface::as_raw(&shell_link),
                PCWSTR(arguments.as_ptr()),
            )
        };
        if let Err(hresult) =
            record_write_field(result, ShortcutField::Arguments, arguments_hresult.0)
        {
            return finish_write(result, DESKBOX_NATIVE_STATUS_OPERATION_FAILED, hresult);
        }

        // SAFETY: Each UTF-16 value is NUL-terminated and alive for the call.
        let working_directory_hresult = if working_directory.len().saturating_sub(1)
            >= SHELL_LINK_LONG_PATH_THRESHOLD
        {
            let short_dir = unsafe { try_get_short_path(&working_directory) };
            let candidate = short_dir.as_ref().unwrap_or(&working_directory);
            let hr = unsafe {
                (Interface::vtable(&shell_link).SetWorkingDirectory)(
                    Interface::as_raw(&shell_link),
                    PCWSTR(candidate.as_ptr()),
                )
            };
            if hresult_succeeded(hr.0) {
                hr
            } else {
                let empty = [0_u16; 1];
                unsafe {
                    (Interface::vtable(&shell_link).SetWorkingDirectory)(
                        Interface::as_raw(&shell_link),
                        PCWSTR(empty.as_ptr()),
                    )
                }
            }
        } else {
            unsafe {
                (Interface::vtable(&shell_link).SetWorkingDirectory)(
                    Interface::as_raw(&shell_link),
                    PCWSTR(working_directory.as_ptr()),
                )
            }
        };
        if let Err(hresult) = record_write_field(
            result,
            ShortcutField::WorkingDirectory,
            working_directory_hresult.0,
        ) {
            return finish_write(result, DESKBOX_NATIVE_STATUS_OPERATION_FAILED, hresult);
        }

        // An empty icon path means "no custom icon": skip SetIconLocation so
        // the system renders the target's own icon with the shortcut overlay
        // (the Explorer-native default). Calling SetIconLocation with an
        // empty string instead relies on undocumented reset semantics.
        if icon_path.len() > 1 {
            // SAFETY: Each UTF-16 value is NUL-terminated and alive for the call.
            let icon_hresult = unsafe {
                (Interface::vtable(&shell_link).SetIconLocation)(
                    Interface::as_raw(&shell_link),
                    PCWSTR(icon_path.as_ptr()),
                    request.icon_index,
                )
            };
            if let Err(hresult) = record_write_field(result, ShortcutField::Icon, icon_hresult.0) {
                return finish_write(result, DESKBOX_NATIVE_STATUS_OPERATION_FAILED, hresult);
            }
        }
    }

    result.attempted_phases |= DESKBOX_SHORTCUT_PHASE_SAVE;
    // SAFETY: The save path is NUL-terminated and alive for the call.
    let mut save_hresult = unsafe {
        (Interface::vtable(&persist_file).Save)(
            Interface::as_raw(&persist_file),
            PCWSTR(shortcut_path.as_ptr()),
            true.into(),
        )
    };
    if save_hresult.0 != DESKBOX_NATIVE_S_OK
        && shortcut_path.len().saturating_sub(1) >= WIN32_PATH_THRESHOLD
        && let Some(extended_shortcut) = to_extended_dos_path(&shortcut_path)
    {
        // SAFETY: Extended save path is NUL-terminated and alive for the call.
        save_hresult = unsafe {
            (Interface::vtable(&persist_file).Save)(
                Interface::as_raw(&persist_file),
                PCWSTR(extended_shortcut.as_ptr()),
                true.into(),
            )
        };
    }
    result.save_hresult = save_hresult.0;
    if save_hresult.0 != DESKBOX_NATIVE_S_OK {
        return finish_write(
            result,
            DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
            save_hresult.0,
        );
    }

    finish_write(result, DESKBOX_NATIVE_STATUS_OK, DESKBOX_NATIVE_S_OK)
}

unsafe fn nul_terminated_input(value: &DeskBoxNativeUtf16StringV1) -> Result<Vec<u16>, i32> {
    let source = if value.length_chars == 0 {
        &[][..]
    } else {
        // SAFETY: The exported ABI validated the pointer and declared length.
        unsafe { slice::from_raw_parts(value.data, value.length_chars as usize) }
    };
    if source.contains(&0) {
        return Err(DESKBOX_NATIVE_E_INVALIDARG);
    }

    let mut terminated = Vec::with_capacity(source.len() + 1);
    terminated.extend_from_slice(source);
    terminated.push(0);
    Ok(terminated)
}

fn record_write_field(
    result: &mut DeskBoxShortcutWriteResultV2,
    field: ShortcutField,
    hresult: i32,
) -> Result<(), i32> {
    result.attempted_fields |= field.mask();
    field.set_write_hresult(result, hresult);
    if !hresult_succeeded(hresult) {
        return Err(hresult);
    }

    result.succeeded_fields |= field.mask();
    Ok(())
}

fn finish_write(result: &mut DeskBoxShortcutWriteResultV2, status: u32, hresult: i32) -> u32 {
    result.status = status;
    result.operation_hresult = hresult;
    status
}

fn finish_ui_resolve(
    result: &mut DeskBoxShortcutUiResolveResultV2,
    status: u32,
    hresult: i32,
) -> u32 {
    result.status = status;
    result.operation_hresult = hresult;
    status
}

unsafe fn execute_shortcut(
    request: &DeskBoxShortcutReadRequestV2,
    result: &mut DeskBoxShortcutReadResultV2,
    resolve_timeout_ms: Option<u32>,
) -> u32 {
    // SAFETY: The exported ABI requires the path pointer to be readable for its declared length.
    let path = unsafe {
        slice::from_raw_parts(
            request.shortcut_path,
            request.shortcut_path_length_chars as usize,
        )
    };
    if path.contains(&0) {
        return finish(
            result,
            DESKBOX_NATIVE_STATUS_INVALID_ARGUMENT,
            DESKBOX_NATIVE_E_INVALIDARG,
        );
    }

    let mut nul_terminated_path = Vec::with_capacity(path.len() + 1);
    nul_terminated_path.extend_from_slice(path);
    nul_terminated_path.push(0);

    result.attempted_phases |= DESKBOX_SHORTCUT_PHASE_COM_INITIALIZE;
    // SAFETY: COM initialization is balanced by the guard for S_OK and S_FALSE.
    let com_hresult = unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) };
    result.com_hresult = com_hresult.0;
    let should_uninitialize = hresult_succeeded(com_hresult.0);
    if !should_uninitialize && com_hresult.0 != RPC_E_CHANGED_MODE.0 {
        return finish(
            result,
            DESKBOX_NATIVE_STATUS_COM_INITIALIZATION_FAILED,
            com_hresult.0,
        );
    }

    let _com_guard = ComUninitializeGuard {
        active: should_uninitialize,
    };

    result.attempted_phases |= DESKBOX_SHORTCUT_PHASE_CREATE_OBJECT;
    // SAFETY: The CLSID and requested interface are generated by windows-rs.
    let shell_link: IShellLinkW =
        match unsafe { CoCreateInstance(&ShellLink, None, CLSCTX_INPROC_SERVER) } {
            Ok(value) => value,
            Err(error) => {
                result.create_hresult = error.code().0;
                return finish(
                    result,
                    DESKBOX_NATIVE_STATUS_OBJECT_CREATION_FAILED,
                    error.code().0,
                );
            }
        };

    let persist_file: IPersistFile = match shell_link.cast() {
        Ok(value) => value,
        Err(error) => {
            result.create_hresult = error.code().0;
            return finish(
                result,
                DESKBOX_NATIVE_STATUS_OBJECT_CREATION_FAILED,
                error.code().0,
            );
        }
    };
    result.create_hresult = DESKBOX_NATIVE_S_OK;

    result.attempted_phases |= DESKBOX_SHORTCUT_PHASE_LOAD;
    // SAFETY: The path is NUL-terminated and remains alive for the call.
    let load_hresult = unsafe { try_load_persist_file(&persist_file, &nul_terminated_path) };
    result.load_hresult = load_hresult.0;
    if !hresult_succeeded(load_hresult.0) {
        return finish(result, DESKBOX_NATIVE_STATUS_LOAD_FAILED, load_hresult.0);
    }

    if let Some(timeout_ms) = resolve_timeout_ms {
        result.attempted_phases |= DESKBOX_SHORTCUT_PHASE_RESOLVE;
        // SAFETY: Resolve is synchronous, uses no owner window, and the caller
        // validated the timeout as a 16-bit value before this function.
        let resolve_hresult = unsafe {
            (Interface::vtable(&shell_link).Resolve)(
                Interface::as_raw(&shell_link),
                HWND::default(),
                resolve_flags(timeout_ms),
            )
        };
        result.resolve_hresult = resolve_hresult.0;
        // Compatibility rule: a failed Resolve does not prevent reading the
        // metadata already loaded from the shortcut.
    }

    let read_result = match request.mode {
        DESKBOX_SHORTCUT_READ_MODE_STORED_RAW => {
            // SAFETY: The COM object and all caller buffers are valid for this synchronous call.
            unsafe { read_stored_raw(&shell_link, request, result) }
        }
        DESKBOX_SHORTCUT_READ_MODE_EFFECTIVE_DIAGNOSTIC => {
            // SAFETY: The COM object and all caller buffers are valid for this synchronous call.
            unsafe { read_effective_diagnostic(&shell_link, request, result) }
        }
        _ => Err(DESKBOX_NATIVE_E_INVALIDARG),
    };

    if let Err(hresult) = read_result {
        return finish(result, DESKBOX_NATIVE_STATUS_OPERATION_FAILED, hresult);
    }

    if result.caller_buffer_too_small_fields != 0 {
        finish(
            result,
            DESKBOX_NATIVE_STATUS_BUFFER_TOO_SMALL,
            DESKBOX_NATIVE_HRESULT_INSUFFICIENT_BUFFER,
        )
    } else {
        finish(result, DESKBOX_NATIVE_STATUS_OK, DESKBOX_NATIVE_S_OK)
    }
}

pub(crate) const fn resolve_flags(timeout_ms: u32) -> u32 {
    SLR_NO_UI | SLR_NOSEARCH | (timeout_ms << 16)
}

pub(crate) const fn resolve_with_ui_flags() -> u32 {
    SLR_UPDATE | SLR_NOSEARCH | SLR_OFFER_DELETE_WITHOUT_FILE
}

unsafe fn read_stored_raw(
    shell_link: &IShellLinkW,
    request: &DeskBoxShortcutReadRequestV2,
    result: &mut DeskBoxShortcutReadResultV2,
) -> Result<(), i32> {
    let mut target = [0_u16; STORED_FIELD_CAPACITY];
    let mut find_data = WIN32_FIND_DATAW::default();
    attempt_field(result, ShortcutField::Target);
    // SAFETY: The generated vtable signature matches IShellLinkW::GetPath.
    let target_hresult = unsafe {
        (Interface::vtable(shell_link).GetPath)(
            Interface::as_raw(shell_link),
            PWSTR(target.as_mut_ptr()),
            target.len() as i32,
            &mut find_data,
            SLGP_RAWPATH,
        )
    };
    // SAFETY: Queries optional PKEY_Link_TargetParsingPath from the loaded shell link;
    // returns Some(...) only when a stamped >260-char target property exists, preventing
    // Windows 8.3 short-path PIDLs from masking full long target paths.
    let long_target_fallback = unsafe { try_read_long_target_parsing_property(shell_link) };
    if let Some(long_target) = long_target_fallback.as_deref() {
        // SAFETY: The caller-owned output buffer passed ABI validation.
        unsafe {
            capture_field(
                ShortcutField::Target,
                request,
                result,
                long_target,
                DESKBOX_NATIVE_S_OK,
                false,
            )
        }?;
    } else {
        // SAFETY: The caller-owned output buffer passed ABI validation.
        unsafe {
            capture_field(
                ShortcutField::Target,
                request,
                result,
                &target,
                target_hresult.0,
                false,
            )
        }?;
    }

    let mut description = [0_u16; STORED_FIELD_CAPACITY];
    attempt_field(result, ShortcutField::Description);
    // SAFETY: The generated vtable signature matches IShellLinkW::GetDescription.
    let description_hresult = unsafe {
        (Interface::vtable(shell_link).GetDescription)(
            Interface::as_raw(shell_link),
            PWSTR(description.as_mut_ptr()),
            description.len() as i32,
        )
    };
    // SAFETY: The caller-owned output buffer passed ABI validation.
    unsafe {
        capture_field(
            ShortcutField::Description,
            request,
            result,
            &description,
            description_hresult.0,
            false,
        )
    }?;

    let mut arguments = [0_u16; ARGUMENT_FIELD_CAPACITY];
    attempt_field(result, ShortcutField::Arguments);
    // SAFETY: The generated vtable signature matches IShellLinkW::GetArguments.
    let arguments_hresult = unsafe {
        (Interface::vtable(shell_link).GetArguments)(
            Interface::as_raw(shell_link),
            PWSTR(arguments.as_mut_ptr()),
            arguments.len() as i32,
        )
    };
    // SAFETY: The caller-owned output buffer passed ABI validation.
    unsafe {
        capture_field(
            ShortcutField::Arguments,
            request,
            result,
            &arguments,
            arguments_hresult.0,
            false,
        )
    }?;

    let mut working_directory = [0_u16; STORED_FIELD_CAPACITY];
    attempt_field(result, ShortcutField::WorkingDirectory);
    // SAFETY: The generated vtable signature matches IShellLinkW::GetWorkingDirectory.
    let working_directory_hresult = unsafe {
        (Interface::vtable(shell_link).GetWorkingDirectory)(
            Interface::as_raw(shell_link),
            PWSTR(working_directory.as_mut_ptr()),
            working_directory.len() as i32,
        )
    };
    // SAFETY: The caller-owned output buffer passed ABI validation.
    unsafe {
        capture_field(
            ShortcutField::WorkingDirectory,
            request,
            result,
            &working_directory,
            working_directory_hresult.0,
            false,
        )
    }?;

    let mut icon_path = [0_u16; STORED_FIELD_CAPACITY];
    let mut icon_index = 0_i32;
    attempt_field(result, ShortcutField::Icon);
    // SAFETY: The generated vtable signature matches IShellLinkW::GetIconLocation.
    let icon_hresult = unsafe {
        (Interface::vtable(shell_link).GetIconLocation)(
            Interface::as_raw(shell_link),
            PWSTR(icon_path.as_mut_ptr()),
            icon_path.len() as i32,
            &mut icon_index,
        )
    };
    // SAFETY: The caller-owned output buffer passed ABI validation.
    unsafe {
        capture_field(
            ShortcutField::Icon,
            request,
            result,
            &icon_path,
            icon_hresult.0,
            false,
        )
    }?;
    result.icon_index = icon_index;

    Ok(())
}

unsafe fn read_effective_diagnostic(
    shell_link: &IShellLinkW,
    request: &DeskBoxShortcutReadRequestV2,
    result: &mut DeskBoxShortcutReadResultV2,
) -> Result<(), i32> {
    let mut target = [0_u16; STORED_FIELD_CAPACITY];
    let mut find_data = WIN32_FIND_DATAW::default();
    attempt_field(result, ShortcutField::Target);
    // SAFETY: The generated vtable signature matches IShellLinkW::GetPath.
    let target_hresult = unsafe {
        (Interface::vtable(shell_link).GetPath)(
            Interface::as_raw(shell_link),
            PWSTR(target.as_mut_ptr()),
            target.len() as i32,
            &mut find_data,
            0,
        )
    };
    // SAFETY: Queries optional PKEY_Link_TargetParsingPath from the loaded shell link;
    // returns Some(...) only when a stamped >260-char target property exists, preventing
    // Windows 8.3 short-path PIDLs from masking full long target paths.
    let long_target_fallback = unsafe { try_read_long_target_parsing_property(shell_link) };
    if let Some(long_target) = long_target_fallback.as_deref() {
        // SAFETY: The caller-owned output buffer passed ABI validation.
        unsafe {
            capture_field(
                ShortcutField::Target,
                request,
                result,
                long_target,
                DESKBOX_NATIVE_S_OK,
                true,
            )
        }?;
    } else {
        // SAFETY: The caller-owned output buffer passed ABI validation.
        unsafe {
            capture_field(
                ShortcutField::Target,
                request,
                result,
                &target,
                target_hresult.0,
                true,
            )
        }?;
    }

    let mut arguments = [0_u16; DIAGNOSTIC_ARGUMENT_CAPACITY];
    attempt_field(result, ShortcutField::Arguments);
    // SAFETY: The generated vtable signature matches IShellLinkW::GetArguments.
    let arguments_hresult = unsafe {
        (Interface::vtable(shell_link).GetArguments)(
            Interface::as_raw(shell_link),
            PWSTR(arguments.as_mut_ptr()),
            arguments.len() as i32,
        )
    };
    // SAFETY: The caller-owned output buffer passed ABI validation.
    unsafe {
        capture_field(
            ShortcutField::Arguments,
            request,
            result,
            &arguments,
            arguments_hresult.0,
            true,
        )
    }?;

    if result.present_fields & DESKBOX_SHORTCUT_FIELD_TARGET_PATH == 0 {
        return Err(DESKBOX_NATIVE_S_FALSE);
    }

    Ok(())
}

fn attempt_field(result: &mut DeskBoxShortcutReadResultV2, field: ShortcutField) {
    result.attempted_fields |= field.mask();
}

unsafe fn capture_field(
    field: ShortcutField,
    request: &DeskBoxShortcutReadRequestV2,
    result: &mut DeskBoxShortcutReadResultV2,
    source: &[u16],
    hresult: i32,
    trim_whitespace: bool,
) -> Result<(), i32> {
    field.set_hresult(result, hresult);
    if !hresult_succeeded(hresult) {
        return Err(hresult);
    }

    let first_nul = source.iter().position(|value| *value == 0);
    let source_length = first_nul.unwrap_or(source.len());
    if first_nul.is_none() || source_length >= source.len().saturating_sub(1) {
        result.source_truncated_fields |= field.mask();
    }

    let mut start = 0;
    let mut end = source_length;
    if trim_whitespace {
        while start < end && is_dotnet_whitespace(source[start]) {
            start += 1;
        }
        while end > start && is_dotnet_whitespace(source[end - 1]) {
            end -= 1;
        }
    }

    let value = &source[start..end];
    let required_chars = (value.len() + 1) as u32;
    field.set_required_chars(result, required_chars);
    result.succeeded_fields |= field.mask();
    if !value.is_empty() {
        result.present_fields |= field.mask();
    }

    let output = field.output(request);
    if output.capacity_chars < required_chars {
        result.caller_buffer_too_small_fields |= field.mask();
        return Ok(());
    }

    // SAFETY: ABI validation requires a non-null writable pointer whenever capacity is nonzero.
    unsafe {
        std::ptr::copy_nonoverlapping(value.as_ptr(), output.data, value.len());
        output.data.add(value.len()).write(0);
    }
    Ok(())
}

const fn hresult_succeeded(hresult: i32) -> bool {
    hresult >= 0
}

fn finish(result: &mut DeskBoxShortcutReadResultV2, status: u32, hresult: i32) -> u32 {
    result.status = status;
    result.operation_hresult = hresult;
    status
}

const fn is_dotnet_whitespace(value: u16) -> bool {
    matches!(
        value,
        0x0009..=0x000D
            | 0x0020
            | 0x0085
            | 0x00A0
            | 0x1680
            | 0x2000..=0x200A
            | 0x2028
            | 0x2029
            | 0x202F
            | 0x205F
            | 0x3000
    )
}
