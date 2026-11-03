use std::slice;

use windows::{
    Win32::{
        Foundation::RPC_E_CHANGED_MODE,
        Globalization::{CSTR_EQUAL, CompareStringOrdinal},
        Storage::FileSystem::GetFullPathNameW,
        System::{
            Com::{
                CLSCTX_ALL, COINIT_APARTMENTTHREADED, CoCreateInstance, CoInitializeEx,
                CoUninitialize,
            },
            Variant::{VARIANT, VT_BSTR},
        },
        UI::Shell::{FolderItem, FolderItem2, IShellDispatch, Shell},
    },
    core::{BSTR, Interface},
};

use crate::{
    DESKBOX_NATIVE_S_FALSE, DESKBOX_NATIVE_S_OK, DESKBOX_NATIVE_STATUS_COM_INITIALIZATION_FAILED,
    DESKBOX_NATIVE_STATUS_OBJECT_CREATION_FAILED, DESKBOX_NATIVE_STATUS_OK,
    DESKBOX_NATIVE_STATUS_OPERATION_FAILED, DESKBOX_RECYCLE_BIN_OPERATION_RESTORE,
    DESKBOX_RECYCLE_BIN_PHASE_COM_INITIALIZE, DESKBOX_RECYCLE_BIN_PHASE_CREATE_OBJECT,
    DESKBOX_RECYCLE_BIN_PHASE_ENUMERATE, DESKBOX_RECYCLE_BIN_PHASE_INVOKE,
    DESKBOX_RECYCLE_BIN_PHASE_ITEM_NAME, DESKBOX_RECYCLE_BIN_PHASE_ITEMS,
    DESKBOX_RECYCLE_BIN_PHASE_NAMESPACE, DESKBOX_RECYCLE_BIN_PHASE_PROPERTY,
    DeskBoxRecycleBinRequestV1, DeskBoxRecycleBinResultV1,
};

const RECYCLE_BIN_CSIDL: i32 = 10;
const DELETED_FROM_PROPERTY: &str = "System.Recycle.DeletedFrom";
const RESTORE_VERB: &str = "undelete";

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
    request: &DeskBoxRecycleBinRequestV1,
    result: &mut DeskBoxRecycleBinResultV1,
) -> u32 {
    result.attempted_phases |= DESKBOX_RECYCLE_BIN_PHASE_COM_INITIALIZE;
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

    result.attempted_phases |= DESKBOX_RECYCLE_BIN_PHASE_CREATE_OBJECT;
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

    result.attempted_phases |= DESKBOX_RECYCLE_BIN_PHASE_NAMESPACE;
    let namespace_value = VARIANT::from(RECYCLE_BIN_CSIDL);
    // SAFETY: The namespace VARIANT remains alive for the synchronous COM call.
    let recycle_bin = match unsafe { shell.NameSpace(&namespace_value) } {
        Ok(value) => {
            result.namespace_hresult = DESKBOX_NATIVE_S_OK;
            value
        }
        Err(error) => {
            result.namespace_hresult = error.code().0;
            return finish(
                result,
                DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
                error.code().0,
                false,
            );
        }
    };

    result.attempted_phases |= DESKBOX_RECYCLE_BIN_PHASE_ITEMS;
    // SAFETY: The generated Folder wrapper owns the returned collection.
    let items = match unsafe { recycle_bin.Items() } {
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

    result.attempted_phases |= DESKBOX_RECYCLE_BIN_PHASE_ENUMERATE;
    // SAFETY: The collection remains valid for this synchronous enumeration.
    let count = match unsafe { items.Count() } {
        Ok(value) => value.max(0),
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

    // SAFETY: ABI validation guarantees both input slices remain readable.
    let expected_parent = unsafe { input_slice(&request.original_parent) };
    // SAFETY: ABI validation guarantees both input slices remain readable.
    let expected_name = unsafe { input_slice(&request.original_name) };
    let normalized_parent = match normalize_full_path(expected_parent) {
        Ok(value) => value,
        Err(hresult) => {
            result.property_hresult = hresult;
            return finish(
                result,
                DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
                hresult,
                false,
            );
        }
    };

    let mut restore_item: Option<FolderItem> = None;
    for index in 0..count {
        let index_variant = VARIANT::from(index);
        // SAFETY: The index VARIANT remains alive for the synchronous collection call.
        let item = match unsafe { items.Item(&index_variant) } {
            Ok(value) => value,
            Err(error) => {
                // Skip unreadable foreign Recycle Bin entries rather than aborting
                // the entire scan before reaching our target item.
                result.enumerate_hresult = error.code().0;
                continue;
            }
        };

        match unsafe { item_name_matches(&item, expected_name, result) } {
            Ok(true) => {}
            Ok(false) | Err(_) => continue,
        }
        match unsafe { deleted_from_matches(&item, &normalized_parent, result) } {
            Ok(true) => {}
            Ok(false) | Err(_) => continue,
        }

        result.matched_count += 1;
        if request.operation == DESKBOX_RECYCLE_BIN_OPERATION_RESTORE && result.matched_count == 1 {
            restore_item = Some(item);
        }
    }

    result.enumerate_hresult = DESKBOX_NATIVE_S_OK;
    if request.operation == DESKBOX_RECYCLE_BIN_OPERATION_RESTORE {
        if result.matched_count != 1 {
            let hresult = if result.matched_count == 0 {
                windows::core::HRESULT::from_win32(
                    windows::Win32::Foundation::ERROR_FILE_NOT_FOUND.0,
                )
                .0
            } else {
                windows::Win32::Foundation::E_UNEXPECTED.0
            };
            return finish(
                result,
                DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
                hresult,
                false,
            );
        }

        let Some(item) = restore_item else {
            return finish(
                result,
                DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
                windows::Win32::Foundation::E_UNEXPECTED.0,
                false,
            );
        };
        result.attempted_phases |= DESKBOX_RECYCLE_BIN_PHASE_INVOKE;
        let verb = VARIANT::from(BSTR::from(RESTORE_VERB));
        // SAFETY: The verb VARIANT and FolderItem remain alive for the call.
        match unsafe { item.InvokeVerb(&verb) } {
            Ok(()) => {
                result.invoke_hresult = DESKBOX_NATIVE_S_OK;
                result.restored_count = 1;
            }
            Err(error) => {
                result.invoke_hresult = error.code().0;
                return finish(
                    result,
                    DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
                    error.code().0,
                    false,
                );
            }
        }
    }

    finish(result, DESKBOX_NATIVE_STATUS_OK, DESKBOX_NATIVE_S_OK, true)
}

unsafe fn item_name_matches(
    item: &FolderItem,
    expected_name: &[u16],
    result: &mut DeskBoxRecycleBinResultV1,
) -> Result<bool, i32> {
    result.attempted_phases |= DESKBOX_RECYCLE_BIN_PHASE_ITEM_NAME;
    // SAFETY: The generated FolderItem owns the returned BSTR.
    let name = match unsafe { item.Name() } {
        Ok(value) => value,
        Err(error) => {
            result.item_name_hresult = error.code().0;
            return Err(error.code().0);
        }
    };

    result.item_name_hresult = DESKBOX_NATIVE_S_OK;
    // SAFETY: Both slices are readable for their declared lengths.
    if unsafe { CompareStringOrdinal(&name, expected_name, true) == CSTR_EQUAL } {
        return Ok(true);
    }

    // When Explorer's "Hide extensions for known file types" is enabled,
    // FolderItem::Name() omits the file extension while System.FileName keeps it.
    if let Ok(item2) = item.cast::<FolderItem2>() {
        let file_name_prop = BSTR::from("System.FileName");
        if let Ok(prop_val) = unsafe { item2.ExtendedProperty(&file_name_prop) }
            && prop_val.vt() == VT_BSTR
            && let Ok(full_file_name) = BSTR::try_from(&prop_val)
        {
            return Ok(unsafe {
                CompareStringOrdinal(&full_file_name, expected_name, true) == CSTR_EQUAL
            });
        }
    }

    Ok(false)
}

unsafe fn deleted_from_matches(
    item: &FolderItem,
    expected_parent: &[u16],
    result: &mut DeskBoxRecycleBinResultV1,
) -> Result<bool, i32> {
    result.attempted_phases |= DESKBOX_RECYCLE_BIN_PHASE_PROPERTY;
    let item2: FolderItem2 = match item.cast() {
        Ok(value) => value,
        Err(error) => {
            result.property_hresult = error.code().0;
            return Err(error.code().0);
        }
    };
    let property_name = BSTR::from(DELETED_FROM_PROPERTY);
    // SAFETY: The property BSTR remains alive for the synchronous COM call.
    let value = match unsafe { item2.ExtendedProperty(&property_name) } {
        Ok(value) => value,
        Err(error) => {
            result.property_hresult = error.code().0;
            return Err(error.code().0);
        }
    };
    if value.vt() != VT_BSTR {
        result.property_hresult = windows::Win32::Foundation::TYPE_E_TYPEMISMATCH.0;
        return Err(windows::Win32::Foundation::TYPE_E_TYPEMISMATCH.0);
    }
    let deleted_from = match BSTR::try_from(&value) {
        Ok(value) => value,
        Err(error) => {
            result.property_hresult = error.code().0;
            return Err(error.code().0);
        }
    };
    let normalized = match normalize_full_path(&deleted_from) {
        Ok(value) => value,
        Err(hresult) => {
            result.property_hresult = hresult;
            return Err(hresult);
        }
    };

    result.property_hresult = DESKBOX_NATIVE_S_OK;
    Ok(paths_equal(&normalized, expected_parent))
}

unsafe fn input_slice(value: &crate::DeskBoxNativeUtf16StringV1) -> &[u16] {
    if value.length_chars == 0 {
        &[]
    } else {
        // SAFETY: Export validation and the caller contract guarantee readability.
        unsafe { slice::from_raw_parts(value.data, value.length_chars as usize) }
    }
}

fn normalize_full_path(value: &[u16]) -> Result<Vec<u16>, i32> {
    let mut input = Vec::with_capacity(value.len() + 1);
    input.extend_from_slice(value);
    input.push(0);

    let required = unsafe { GetFullPathNameW(windows::core::PCWSTR(input.as_ptr()), None, None) };
    if required == 0 {
        return Err(windows::core::Error::from_thread().code().0);
    }

    let mut buffer = vec![0u16; required as usize];
    let written = unsafe {
        GetFullPathNameW(
            windows::core::PCWSTR(input.as_ptr()),
            Some(&mut buffer),
            None,
        )
    };
    if written == 0 {
        return Err(windows::core::Error::from_thread().code().0);
    }
    if written >= buffer.len() as u32 {
        buffer.resize(written as usize + 1, 0);
        let retried = unsafe {
            GetFullPathNameW(
                windows::core::PCWSTR(input.as_ptr()),
                Some(&mut buffer),
                None,
            )
        };
        if retried == 0 || retried >= buffer.len() as u32 {
            return Err(windows::core::Error::from_thread().code().0);
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

fn finish(
    result: &mut DeskBoxRecycleBinResultV1,
    status: u32,
    operation_hresult: i32,
    succeeded: bool,
) -> u32 {
    result.status = status;
    result.operation_hresult = operation_hresult;
    result.operation_succeeded = u32::from(succeeded);
    status
}
