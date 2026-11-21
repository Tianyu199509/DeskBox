use std::slice;

use windows::{
    Win32::{
        Foundation::RPC_E_CHANGED_MODE,
        System::{
            Com::{
                CLSCTX_ALL, COINIT_APARTMENTTHREADED, CoAllowSetForegroundWindow, CoCreateInstance,
                CoInitializeEx, CoUninitialize,
            },
            Variant::VARIANT,
        },
        UI::Shell::{
            IShellDispatch, IShellDispatch2, IShellFolderViewDual, IShellWindows, IWebBrowser,
            SWC_DESKTOP, SWFO_NEEDDISPATCH, Shell,
        },
    },
    core::{BSTR, Interface},
};

use crate::{
    DESKBOX_EXPLORER_SHELL_LAUNCH_PHASE_APPLICATION,
    DESKBOX_EXPLORER_SHELL_LAUNCH_PHASE_COM_INITIALIZE,
    DESKBOX_EXPLORER_SHELL_LAUNCH_PHASE_CREATE_OBJECT, DESKBOX_EXPLORER_SHELL_LAUNCH_PHASE_DESKTOP,
    DESKBOX_EXPLORER_SHELL_LAUNCH_PHASE_DOCUMENT, DESKBOX_EXPLORER_SHELL_LAUNCH_PHASE_EXECUTE,
    DESKBOX_EXPLORER_SHELL_LAUNCH_PHASE_WINDOWS, DESKBOX_NATIVE_S_FALSE, DESKBOX_NATIVE_S_OK,
    DESKBOX_NATIVE_STATUS_COM_INITIALIZATION_FAILED, DESKBOX_NATIVE_STATUS_OBJECT_CREATION_FAILED,
    DESKBOX_NATIVE_STATUS_OK, DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
    DeskBoxExplorerShellLaunchRequestV1, DeskBoxExplorerShellLaunchResultV1,
    DeskBoxNativeUtf16StringV1,
};

const SHOW_NORMAL: i32 = 1;

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
    request: &DeskBoxExplorerShellLaunchRequestV1,
    result: &mut DeskBoxExplorerShellLaunchResultV1,
) -> u32 {
    result.attempted_phases |= DESKBOX_EXPLORER_SHELL_LAUNCH_PHASE_COM_INITIALIZE;
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

    result.attempted_phases |= DESKBOX_EXPLORER_SHELL_LAUNCH_PHASE_CREATE_OBJECT;
    // SAFETY: Shell is the documented Shell.Application coclass and the requested
    // interface is generated from the Windows Shell type library.
    let local_shell: IShellDispatch = match unsafe { CoCreateInstance(&Shell, None, CLSCTX_ALL) } {
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

    result.attempted_phases |= DESKBOX_EXPLORER_SHELL_LAUNCH_PHASE_WINDOWS;
    // SAFETY: The generated IShellDispatch method returns an owned IDispatch.
    let shell_windows_dispatch = match unsafe { local_shell.Windows() } {
        Ok(value) => value,
        Err(error) => {
            result.windows_hresult = error.code().0;
            return finish(
                result,
                DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
                error.code().0,
                false,
            );
        }
    };
    let shell_windows: IShellWindows = match shell_windows_dispatch.cast() {
        Ok(value) => {
            result.windows_hresult = DESKBOX_NATIVE_S_OK;
            value
        }
        Err(error) => {
            result.windows_hresult = error.code().0;
            return finish(
                result,
                DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
                error.code().0,
                false,
            );
        }
    };

    result.attempted_phases |= DESKBOX_EXPLORER_SHELL_LAUNCH_PHASE_DESKTOP;
    let desktop_location = VARIANT::default();
    let desktop_root = VARIANT::from(0_i32);
    let mut desktop_hwnd = 0_i32;
    // SAFETY: Both variants and the HWND output remain valid for this synchronous call.
    let desktop_dispatch = match unsafe {
        shell_windows.FindWindowSW(
            &desktop_location,
            &desktop_root,
            SWC_DESKTOP,
            &mut desktop_hwnd,
            SWFO_NEEDDISPATCH,
        )
    } {
        Ok(value) => value,
        Err(error) => {
            result.desktop_hresult = error.code().0;
            return finish(
                result,
                DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
                error.code().0,
                false,
            );
        }
    };
    let desktop: IWebBrowser = match desktop_dispatch.cast() {
        Ok(value) => {
            result.desktop_hresult = DESKBOX_NATIVE_S_OK;
            value
        }
        Err(error) => {
            result.desktop_hresult = error.code().0;
            return finish(
                result,
                DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
                error.code().0,
                false,
            );
        }
    };

    result.attempted_phases |= DESKBOX_EXPLORER_SHELL_LAUNCH_PHASE_DOCUMENT;
    // SAFETY: The desktop IWebBrowser object exposes its current document synchronously.
    let document_dispatch = match unsafe { desktop.Document() } {
        Ok(value) => value,
        Err(error) => {
            result.document_hresult = error.code().0;
            return finish(
                result,
                DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
                error.code().0,
                false,
            );
        }
    };
    let document: IShellFolderViewDual = match document_dispatch.cast() {
        Ok(value) => {
            result.document_hresult = DESKBOX_NATIVE_S_OK;
            value
        }
        Err(error) => {
            result.document_hresult = error.code().0;
            return finish(
                result,
                DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
                error.code().0,
                false,
            );
        }
    };

    result.attempted_phases |= DESKBOX_EXPLORER_SHELL_LAUNCH_PHASE_APPLICATION;
    // SAFETY: The desktop document's Application property is the Shell object hosted
    // by the running Explorer process.
    let explorer_shell_dispatch = match unsafe { document.Application() } {
        Ok(value) => value,
        Err(error) => {
            result.application_hresult = error.code().0;
            return finish(
                result,
                DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
                error.code().0,
                false,
            );
        }
    };
    let explorer_shell: IShellDispatch2 = match explorer_shell_dispatch.cast() {
        Ok(value) => {
            result.application_hresult = DESKBOX_NATIVE_S_OK;
            value
        }
        Err(error) => {
            result.application_hresult = error.code().0;
            return finish(
                result,
                DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
                error.code().0,
                false,
            );
        }
    };

    // Foreground transfer is best-effort: a launch must still proceed when Windows
    // declines the privilege, but an explicit widget click should let Explorer hand
    // activation to the application it is about to launch.
    let _ = unsafe { CoAllowSetForegroundWindow(&explorer_shell, None) };

    result.attempted_phases |= DESKBOX_EXPLORER_SHELL_LAUNCH_PHASE_EXECUTE;
    // SAFETY: ABI validation guarantees all three UTF-16 slices remain readable.
    let path = BSTR::from_wide(unsafe { input_slice(&request.path) });
    // Pass VT_EMPTY when working_directory or verb is omitted so IShellDispatch2::ShellExecute
    // executes the true Desktop double-click default verb rather than an explicit "open" verb
    // that fails silently on default-verb-only classes (.cpl, .ttf, .otf, custom ProgIds).
    let working_directory = if request.working_directory.length_chars == 0 {
        VARIANT::default()
    } else {
        // SAFETY: ABI validation guarantees all three UTF-16 slices remain readable.
        VARIANT::from(BSTR::from_wide(unsafe {
            input_slice(&request.working_directory)
        }))
    };
    let verb = if request.verb.length_chars == 0 {
        VARIANT::default()
    } else {
        // SAFETY: ABI validation guarantees all three UTF-16 slices remain readable.
        VARIANT::from(BSTR::from_wide(unsafe { input_slice(&request.verb) }))
    };
    let arguments = VARIANT::default();
    let show = VARIANT::from(SHOW_NORMAL);
    // SAFETY: Every BSTR/VARIANT remains alive for the duration of the synchronous call.
    match unsafe {
        explorer_shell.ShellExecute(&path, &arguments, &working_directory, &verb, &show)
    } {
        Ok(()) => {
            result.execute_hresult = DESKBOX_NATIVE_S_OK;
            finish(result, DESKBOX_NATIVE_STATUS_OK, DESKBOX_NATIVE_S_OK, true)
        }
        Err(error) => {
            result.execute_hresult = error.code().0;
            finish(
                result,
                DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
                error.code().0,
                false,
            )
        }
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
    result: &mut DeskBoxExplorerShellLaunchResultV1,
    status: u32,
    operation_hresult: i32,
    operation_succeeded: bool,
) -> u32 {
    result.status = status;
    result.operation_hresult = operation_hresult;
    result.operation_succeeded = u32::from(operation_succeeded);
    status
}
