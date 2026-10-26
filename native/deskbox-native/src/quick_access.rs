use std::slice;

use windows::{
    Win32::{
        Foundation::{E_POINTER, RPC_E_CHANGED_MODE, TYPE_E_TYPEMISMATCH},
        Globalization::{CSTR_EQUAL, CompareStringOrdinal},
        Storage::FileSystem::GetFullPathNameW,
        System::{
            Com::{
                CLSCTX_ALL, COINIT_APARTMENTTHREADED, CoCreateInstance, CoInitializeEx,
                CoUninitialize,
            },
            Variant::{VARIANT, VT_BOOL, VT_BSTR, VT_I4},
        },
        UI::Shell::{FolderItem, FolderItem2, IShellDispatch, Shell},
    },
    core::{BSTR, Error, Interface, PCWSTR},
};

use crate::{
    DESKBOX_NATIVE_E_INVALIDARG, DESKBOX_NATIVE_S_FALSE, DESKBOX_NATIVE_S_OK,
    DESKBOX_NATIVE_STATUS_COM_INITIALIZATION_FAILED, DESKBOX_NATIVE_STATUS_INVALID_ARGUMENT,
    DESKBOX_NATIVE_STATUS_OBJECT_CREATION_FAILED, DESKBOX_NATIVE_STATUS_OK,
    DESKBOX_NATIVE_STATUS_OPERATION_FAILED, DESKBOX_QUICK_ACCESS_OPERATION_PIN,
    DESKBOX_QUICK_ACCESS_OPERATION_QUERY_PIN_STATE, DESKBOX_QUICK_ACCESS_OPERATION_UNPIN,
    DESKBOX_QUICK_ACCESS_PHASE_COM_INITIALIZE, DESKBOX_QUICK_ACCESS_PHASE_CREATE_OBJECT,
    DESKBOX_QUICK_ACCESS_PHASE_ENUMERATE, DESKBOX_QUICK_ACCESS_PHASE_INVOKE,
    DESKBOX_QUICK_ACCESS_PHASE_ITEM_PATH, DESKBOX_QUICK_ACCESS_PHASE_ITEMS,
    DESKBOX_QUICK_ACCESS_PHASE_PARENT_NAMESPACE, DESKBOX_QUICK_ACCESS_PHASE_PARSE_NAME,
    DESKBOX_QUICK_ACCESS_PHASE_PROPERTY, DESKBOX_QUICK_ACCESS_PHASE_QUICK_NAMESPACE,
    DESKBOX_QUICK_ACCESS_PIN_STATE_NOT_PINNED, DESKBOX_QUICK_ACCESS_PIN_STATE_PINNED,
    DESKBOX_QUICK_ACCESS_PIN_STATE_UNKNOWN, DeskBoxNativeUtf16StringV1,
    DeskBoxQuickAccessRequestV1, DeskBoxQuickAccessResultV1,
};

const QUICK_ACCESS_NAMESPACE: &str = "shell:::{679F85CB-0220-4080-B29B-5540CC05AAB6}";
const PIN_VERB: &str = "pintohome";
const UNPIN_VERB: &str = "unpinfromhome";
const IS_PINNED_PROPERTY: &str = "System.IsPinnedToNameSpaceTree";

struct ComUninitializeGuard {
    active: bool,
}

impl Drop for ComUninitializeGuard {
    fn drop(&mut self) {
        if self.active {
            // SAFETY: Each active guard represents one successful CoInitializeEx call.
            unsafe { CoUninitialize() };
        }
    }
}

pub(crate) unsafe fn execute(
    request: &DeskBoxQuickAccessRequestV1,
    result: &mut DeskBoxQuickAccessResultV1,
) -> u32 {
    result.attempted_phases |= DESKBOX_QUICK_ACCESS_PHASE_COM_INITIALIZE;
    // SAFETY: COM initialization is balanced by the guard for S_OK and S_FALSE.
    let com_hresult = unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) };
    result.com_hresult = com_hresult.0;
    let should_uninitialize =
        com_hresult.0 == DESKBOX_NATIVE_S_OK || com_hresult.0 == DESKBOX_NATIVE_S_FALSE;
    if !should_uninitialize && com_hresult.0 != RPC_E_CHANGED_MODE.0 {
        return finish(
            result,
            DESKBOX_NATIVE_STATUS_COM_INITIALIZATION_FAILED,
            com_hresult.0,
            false,
        );
    }
    let _com_guard = ComUninitializeGuard {
        active: should_uninitialize,
    };

    result.attempted_phases |= DESKBOX_QUICK_ACCESS_PHASE_CREATE_OBJECT;
    // SAFETY: Shell is the documented Shell.Application coclass and the requested
    // interface is generated from the Windows Shell type library.
    let shell: IShellDispatch = match unsafe { CoCreateInstance(&Shell, None, CLSCTX_ALL) } {
        Ok(value) => {
            result.create_hresult = DESKBOX_NATIVE_S_OK;
            value
        }
        Err(error) => {
            result.create_hresult = error.code().0;
            return finish(
                result,
                DESKBOX_NATIVE_STATUS_OBJECT_CREATION_FAILED,
                error.code().0,
                false,
            );
        }
    };

    match request.operation {
        DESKBOX_QUICK_ACCESS_OPERATION_QUERY_PIN_STATE => {
            // SAFETY: ABI validation guarantees folder_path remains readable.
            unsafe { query_pin_state(&shell, input_slice(&request.folder_path), result) }
        }
        DESKBOX_QUICK_ACCESS_OPERATION_PIN => {
            // SAFETY: ABI validation guarantees both folder item inputs remain readable.
            unsafe {
                invoke_parent_item_verb(
                    &shell,
                    input_slice(&request.parent_path),
                    input_slice(&request.folder_name),
                    PIN_VERB,
                    result,
                )
            }
        }
        DESKBOX_QUICK_ACCESS_OPERATION_UNPIN => {
            // SAFETY: ABI validation guarantees all three inputs remain readable.
            unsafe {
                unpin_folder(
                    &shell,
                    input_slice(&request.folder_path),
                    input_slice(&request.parent_path),
                    input_slice(&request.folder_name),
                    result,
                )
            }
        }
        // Deep review R17 (G2 audit): the library is loaded in-process with
        // panic = "abort" — a validate/dispatch drift here killed the whole
        // host app instead of reporting an error. Defensive return instead.
        _ => finish(
            result,
            DESKBOX_NATIVE_STATUS_INVALID_ARGUMENT,
            DESKBOX_NATIVE_E_INVALIDARG,
            false,
        ),
    }
}

unsafe fn query_pin_state(
    shell: &IShellDispatch,
    target_path: &[u16],
    result: &mut DeskBoxQuickAccessResultV1,
) -> u32 {
    result.attempted_phases |= DESKBOX_QUICK_ACCESS_PHASE_QUICK_NAMESPACE;
    // SAFETY: The namespace VARIANT remains alive for the synchronous COM call.
    let quick_access = match unsafe { shell_namespace(shell, QUICK_ACCESS_NAMESPACE) } {
        Ok(value) => {
            result.quick_namespace_hresult = DESKBOX_NATIVE_S_OK;
            value
        }
        Err(error) => {
            result.quick_namespace_hresult = error.code().0;
            return finish(
                result,
                DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
                error.code().0,
                false,
            );
        }
    };

    result.attempted_phases |= DESKBOX_QUICK_ACCESS_PHASE_ITEMS;
    // SAFETY: The generated Folder wrapper owns the returned collection.
    let items = match unsafe { quick_access.Items() } {
        Ok(value) => {
            result.items_hresult = DESKBOX_NATIVE_S_OK;
            value
        }
        Err(error) => {
            result.items_hresult = error.code().0;
            return finish(
                result,
                DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
                error.code().0,
                false,
            );
        }
    };

    result.attempted_phases |= DESKBOX_QUICK_ACCESS_PHASE_ENUMERATE;
    // SAFETY: The collection is valid for the duration of this call.
    let count = match unsafe { items.Count() } {
        Ok(value) => value,
        Err(error) => {
            result.enumerate_hresult = error.code().0;
            return finish(
                result,
                DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
                error.code().0,
                false,
            );
        }
    };

    for index in 0..count.max(0) {
        let index_variant = VARIANT::from(index);
        // SAFETY: The index VARIANT remains alive for the synchronous collection call.
        let item = match unsafe { items.Item(&index_variant) } {
            Ok(value) => value,
            Err(error) => {
                result.enumerate_hresult = error.code().0;
                return finish(
                    result,
                    DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
                    error.code().0,
                    false,
                );
            }
        };

        // The C# oracle skips individual items whose Path cannot be read or normalized.
        if !unsafe { item_matches_path(&item, target_path, result) } {
            continue;
        }

        result.enumerate_hresult = DESKBOX_NATIVE_S_OK;
        result.matched_item = 1;
        result.attempted_phases |= DESKBOX_QUICK_ACCESS_PHASE_PROPERTY;
        let item2: FolderItem2 = match item.cast() {
            Ok(value) => value,
            Err(error) => {
                result.property_hresult = error.code().0;
                return finish(result, DESKBOX_NATIVE_STATUS_OK, DESKBOX_NATIVE_S_OK, true);
            }
        };

        let property_name = BSTR::from(IS_PINNED_PROPERTY);
        // SAFETY: The property BSTR remains alive for the synchronous COM call.
        let value = match unsafe { item2.ExtendedProperty(&property_name) } {
            Ok(value) => value,
            Err(error) => {
                result.property_hresult = error.code().0;
                return finish(result, DESKBOX_NATIVE_STATUS_OK, DESKBOX_NATIVE_S_OK, true);
            }
        };

        match parse_pinned_variant(&value) {
            Some(true) => {
                result.property_hresult = DESKBOX_NATIVE_S_OK;
                result.pin_state = DESKBOX_QUICK_ACCESS_PIN_STATE_PINNED;
            }
            Some(false) => {
                result.property_hresult = DESKBOX_NATIVE_S_OK;
                result.pin_state = DESKBOX_QUICK_ACCESS_PIN_STATE_NOT_PINNED;
            }
            None => {
                result.property_hresult = TYPE_E_TYPEMISMATCH.0;
                result.pin_state = DESKBOX_QUICK_ACCESS_PIN_STATE_UNKNOWN;
            }
        }

        return finish(result, DESKBOX_NATIVE_STATUS_OK, DESKBOX_NATIVE_S_OK, true);
    }

    result.enumerate_hresult = DESKBOX_NATIVE_S_OK;
    result.pin_state = DESKBOX_QUICK_ACCESS_PIN_STATE_NOT_PINNED;
    finish(result, DESKBOX_NATIVE_STATUS_OK, DESKBOX_NATIVE_S_OK, true)
}

unsafe fn unpin_folder(
    shell: &IShellDispatch,
    target_path: &[u16],
    parent_path: &[u16],
    folder_name: &[u16],
    result: &mut DeskBoxQuickAccessResultV1,
) -> u32 {
    result.attempted_phases |= DESKBOX_QUICK_ACCESS_PHASE_QUICK_NAMESPACE;
    // SAFETY: The namespace VARIANT remains alive for the synchronous COM call.
    match unsafe { shell_namespace(shell, QUICK_ACCESS_NAMESPACE) } {
        Ok(quick_access) => {
            result.quick_namespace_hresult = DESKBOX_NATIVE_S_OK;
            result.attempted_phases |= DESKBOX_QUICK_ACCESS_PHASE_ITEMS;
            // SAFETY: The generated Folder wrapper owns the returned collection.
            let items = match unsafe { quick_access.Items() } {
                Ok(value) => {
                    result.items_hresult = DESKBOX_NATIVE_S_OK;
                    value
                }
                Err(error) => {
                    result.items_hresult = error.code().0;
                    return finish(
                        result,
                        DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
                        error.code().0,
                        false,
                    );
                }
            };

            result.attempted_phases |= DESKBOX_QUICK_ACCESS_PHASE_ENUMERATE;
            // SAFETY: The collection is valid for the duration of this call.
            let count = match unsafe { items.Count() } {
                Ok(value) => value,
                Err(error) => {
                    result.enumerate_hresult = error.code().0;
                    return finish(
                        result,
                        DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
                        error.code().0,
                        false,
                    );
                }
            };

            for index in 0..count.max(0) {
                let index_variant = VARIANT::from(index);
                // SAFETY: The index VARIANT remains alive for the synchronous collection call.
                let item = match unsafe { items.Item(&index_variant) } {
                    Ok(value) => value,
                    Err(error) => {
                        result.enumerate_hresult = error.code().0;
                        return finish(
                            result,
                            DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
                            error.code().0,
                            false,
                        );
                    }
                };

                // The C# oracle skips individual items whose Path cannot be read.
                if !unsafe { item_matches_path(&item, target_path, result) } {
                    continue;
                }

                result.enumerate_hresult = DESKBOX_NATIVE_S_OK;
                result.matched_item = 1;
                return unsafe { invoke_item_verb(&item, UNPIN_VERB, result) };
            }

            result.enumerate_hresult = DESKBOX_NATIVE_S_OK;
        }
        Err(error) if error.code() == E_POINTER => {
            // Shell.Application returns a null Folder when Quick Access is unavailable;
            // the generated projection represents that null as E_POINTER. The C# oracle
            // falls back to invoking the verb on the parent folder item in this case.
            result.quick_namespace_hresult = error.code().0;
        }
        Err(error) => {
            result.quick_namespace_hresult = error.code().0;
            return finish(
                result,
                DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
                error.code().0,
                false,
            );
        }
    }

    result.fallback_used = 1;
    // SAFETY: ABI validation guarantees both folder item inputs remain readable.
    unsafe { invoke_parent_item_verb(shell, parent_path, folder_name, UNPIN_VERB, result) }
}

unsafe fn invoke_parent_item_verb(
    shell: &IShellDispatch,
    parent_path: &[u16],
    folder_name: &[u16],
    verb: &str,
    result: &mut DeskBoxQuickAccessResultV1,
) -> u32 {
    result.attempted_phases |= DESKBOX_QUICK_ACCESS_PHASE_PARENT_NAMESPACE;
    // SAFETY: The namespace VARIANT remains alive for the synchronous COM call.
    let parent = match unsafe { shell_namespace_wide(shell, parent_path) } {
        Ok(value) => {
            result.parent_namespace_hresult = DESKBOX_NATIVE_S_OK;
            value
        }
        Err(error) => {
            result.parent_namespace_hresult = error.code().0;
            return finish(
                result,
                DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
                error.code().0,
                false,
            );
        }
    };

    result.attempted_phases |= DESKBOX_QUICK_ACCESS_PHASE_PARSE_NAME;
    let name = BSTR::from_wide(folder_name);
    // SAFETY: The name BSTR remains alive for the synchronous COM call.
    let item = match unsafe { parent.ParseName(&name) } {
        Ok(value) => {
            result.parse_name_hresult = DESKBOX_NATIVE_S_OK;
            value
        }
        Err(error) => {
            result.parse_name_hresult = error.code().0;
            return finish(
                result,
                DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
                error.code().0,
                false,
            );
        }
    };

    // SAFETY: The generated FolderItem owns the COM interface for this call.
    unsafe { invoke_item_verb(&item, verb, result) }
}

unsafe fn invoke_item_verb(
    item: &FolderItem,
    verb: &str,
    result: &mut DeskBoxQuickAccessResultV1,
) -> u32 {
    result.attempted_phases |= DESKBOX_QUICK_ACCESS_PHASE_INVOKE;
    let verb_variant = VARIANT::from(BSTR::from(verb));
    // SAFETY: The verb VARIANT remains alive for the synchronous COM call.
    match unsafe { item.InvokeVerb(&verb_variant) } {
        Ok(()) => {
            result.invoke_hresult = DESKBOX_NATIVE_S_OK;
            finish(result, DESKBOX_NATIVE_STATUS_OK, DESKBOX_NATIVE_S_OK, true)
        }
        Err(error) => {
            result.invoke_hresult = error.code().0;
            finish(
                result,
                DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
                error.code().0,
                false,
            )
        }
    }
}

unsafe fn item_matches_path(
    item: &FolderItem,
    target_path: &[u16],
    result: &mut DeskBoxQuickAccessResultV1,
) -> bool {
    result.attempted_phases |= DESKBOX_QUICK_ACCESS_PHASE_ITEM_PATH;
    // SAFETY: The generated FolderItem owns the returned BSTR.
    let item_path = match unsafe { item.Path() } {
        Ok(value) => value,
        Err(error) => {
            result.item_path_hresult = error.code().0;
            return false;
        }
    };

    let normalized = match normalize_full_path(&item_path) {
        Ok(value) => value,
        Err(hresult) => {
            result.item_path_hresult = hresult;
            return false;
        }
    };

    result.item_path_hresult = DESKBOX_NATIVE_S_OK;
    paths_equal(&normalized, target_path)
}

unsafe fn shell_namespace(
    shell: &IShellDispatch,
    namespace: &str,
) -> windows::core::Result<windows::Win32::UI::Shell::Folder> {
    let value = VARIANT::from(BSTR::from(namespace));
    // SAFETY: The VARIANT is valid and remains alive for the call.
    unsafe { shell.NameSpace(&value) }
}

unsafe fn shell_namespace_wide(
    shell: &IShellDispatch,
    namespace: &[u16],
) -> windows::core::Result<windows::Win32::UI::Shell::Folder> {
    let value = VARIANT::from(BSTR::from_wide(namespace));
    // SAFETY: The VARIANT is valid and remains alive for the call.
    unsafe { shell.NameSpace(&value) }
}

fn normalize_full_path(path: &[u16]) -> Result<Vec<u16>, i32> {
    let mut terminated = Vec::with_capacity(path.len() + 1);
    terminated.extend_from_slice(path);
    terminated.push(0);

    // SAFETY: terminated is a readable, NUL-terminated UTF-16 buffer.
    let required = unsafe { GetFullPathNameW(PCWSTR(terminated.as_ptr()), None, None) };
    if required == 0 {
        return Err(Error::from_thread().code().0);
    }

    let mut buffer = vec![0_u16; required as usize];
    // SAFETY: terminated remains alive and buffer is writable for its full capacity.
    let written = unsafe {
        GetFullPathNameW(
            PCWSTR(terminated.as_ptr()),
            Some(buffer.as_mut_slice()),
            None,
        )
    };
    if written == 0 {
        return Err(Error::from_thread().code().0);
    }
    if written as usize >= buffer.len() {
        buffer.resize(written as usize + 1, 0);
        // SAFETY: terminated remains alive and the resized buffer is writable.
        let retried = unsafe {
            GetFullPathNameW(
                PCWSTR(terminated.as_ptr()),
                Some(buffer.as_mut_slice()),
                None,
            )
        };
        if retried == 0 || retried as usize >= buffer.len() {
            return Err(Error::from_thread().code().0);
        }
        buffer.truncate(retried as usize);
        return Ok(buffer);
    }

    buffer.truncate(written as usize);
    Ok(buffer)
}

fn paths_equal(left: &[u16], right: &[u16]) -> bool {
    let left = trim_directory_separators(left);
    let right = trim_directory_separators(right);
    // SAFETY: Both slices are readable for their declared lengths.
    unsafe { CompareStringOrdinal(left, right, true) == CSTR_EQUAL }
}

fn trim_directory_separators(mut value: &[u16]) -> &[u16] {
    while value
        .last()
        .is_some_and(|last| *last == b'\\' as u16 || *last == b'/' as u16)
    {
        value = &value[..value.len() - 1];
    }
    value
}

pub(crate) fn parse_pinned_variant(value: &VARIANT) -> Option<bool> {
    match value.vt() {
        VT_BOOL => bool::try_from(value).ok(),
        VT_I4 => i32::try_from(value).ok().map(|number| number != 0),
        VT_BSTR => {
            let text = BSTR::try_from(value).ok()?;
            let text = String::from_utf16(&text).ok()?;
            let trimmed = text.trim();
            if trimmed.eq_ignore_ascii_case("true") {
                Some(true)
            } else if trimmed.eq_ignore_ascii_case("false") {
                Some(false)
            } else {
                None
            }
        }
        _ => None,
    }
}

unsafe fn input_slice(value: &DeskBoxNativeUtf16StringV1) -> &[u16] {
    if value.length_chars == 0 {
        &[]
    } else {
        // SAFETY: Export validation requires a readable pointer for every non-empty input.
        unsafe { slice::from_raw_parts(value.data, value.length_chars as usize) }
    }
}

fn finish(
    result: &mut DeskBoxQuickAccessResultV1,
    status: u32,
    operation_hresult: i32,
    operation_succeeded: bool,
) -> u32 {
    result.status = status;
    result.operation_hresult = operation_hresult;
    result.operation_succeeded = u32::from(operation_succeeded);
    status
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn pinned_property_preserves_bool_int_and_string_oracle_semantics() {
        assert_eq!(parse_pinned_variant(&VARIANT::from(true)), Some(true));
        assert_eq!(parse_pinned_variant(&VARIANT::from(false)), Some(false));
        assert_eq!(parse_pinned_variant(&VARIANT::from(7_i32)), Some(true));
        assert_eq!(parse_pinned_variant(&VARIANT::from(0_i32)), Some(false));
        assert_eq!(
            parse_pinned_variant(&VARIANT::from(BSTR::from("  TrUe  "))),
            Some(true)
        );
        assert_eq!(
            parse_pinned_variant(&VARIANT::from(BSTR::from("FALSE"))),
            Some(false)
        );
        assert_eq!(parse_pinned_variant(&VARIANT::from(BSTR::from("1"))), None);
        assert_eq!(parse_pinned_variant(&VARIANT::from(1_u32)), None);
    }

    #[test]
    fn path_comparison_is_case_insensitive_and_ignores_trailing_separators() {
        let left: Vec<u16> = r"C:\DeskBox\Managed".encode_utf16().collect();
        let right: Vec<u16> = r"c:\deskbox\managed\\".encode_utf16().collect();
        let different: Vec<u16> = r"C:\DeskBox\Other".encode_utf16().collect();

        assert!(paths_equal(&left, &right));
        assert!(!paths_equal(&left, &different));
    }

    #[test]
    fn fresh_result_keeps_unattempted_hresult_and_unknown_state() {
        let result = crate::empty_quick_access_result();

        assert_eq!(result.pin_state, DESKBOX_QUICK_ACCESS_PIN_STATE_UNKNOWN);
        assert_eq!(
            result.com_hresult,
            crate::DESKBOX_NATIVE_HRESULT_NOT_ATTEMPTED
        );
        assert_eq!(result.operation_succeeded, 0);
    }
}
