use std::slice;

use windows::{
    Win32::{
        Foundation::{CloseHandle, HANDLE, RPC_E_CHANGED_MODE},
        Media::Audio::{
            Endpoints::IAudioEndpointVolume, IAudioSessionControl2, IAudioSessionManager2,
            IMMDevice, IMMDeviceEnumerator, ISimpleAudioVolume, MMDeviceEnumerator, eConsole,
            eMultimedia, eRender,
        },
        System::{
            Com::{
                CLSCTX_ALL, COINIT_MULTITHREADED, CoCreateInstance, CoInitializeEx, CoTaskMemFree,
                CoUninitialize,
            },
            Threading::{
                OpenProcess, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION,
                QueryFullProcessImageNameW,
            },
        },
    },
    core::{GUID, Interface, PWSTR},
};

use crate::{
    DESKBOX_MUSIC_VOLUME_MATCH_APP_ID_PROCESS, DESKBOX_MUSIC_VOLUME_MATCH_DISPLAY_NAME,
    DESKBOX_MUSIC_VOLUME_MATCH_IDENTIFIER_APP_ID,
    DESKBOX_MUSIC_VOLUME_MATCH_IDENTIFIER_DISPLAY_NAME, DESKBOX_MUSIC_VOLUME_MATCH_INSTANCE_APP_ID,
    DESKBOX_MUSIC_VOLUME_MATCH_NONE, DESKBOX_MUSIC_VOLUME_MATCH_PROCESS_DISPLAY_NAME,
    DESKBOX_MUSIC_VOLUME_MATCH_SINGLE_FALLBACK, DESKBOX_MUSIC_VOLUME_OPERATION_GET_SNAPSHOT,
    DESKBOX_MUSIC_VOLUME_OPERATION_GET_SYSTEM, DESKBOX_MUSIC_VOLUME_OPERATION_SET_SESSION,
    DESKBOX_MUSIC_VOLUME_OPERATION_SET_SYSTEM, DESKBOX_MUSIC_VOLUME_PHASE_COM_INITIALIZE,
    DESKBOX_MUSIC_VOLUME_PHASE_CREATE_ENUMERATOR, DESKBOX_MUSIC_VOLUME_PHASE_ENUMERATE_SESSIONS,
    DESKBOX_MUSIC_VOLUME_PHASE_GET_DEVICE, DESKBOX_MUSIC_VOLUME_PHASE_SESSION_VOLUME,
    DESKBOX_MUSIC_VOLUME_PHASE_SYSTEM_VOLUME, DESKBOX_NATIVE_E_INVALIDARG, DESKBOX_NATIVE_S_FALSE,
    DESKBOX_NATIVE_S_OK, DESKBOX_NATIVE_STATUS_COM_INITIALIZATION_FAILED,
    DESKBOX_NATIVE_STATUS_INVALID_ARGUMENT, DESKBOX_NATIVE_STATUS_OBJECT_CREATION_FAILED,
    DESKBOX_NATIVE_STATUS_OK, DESKBOX_NATIVE_STATUS_OPERATION_FAILED, DeskBoxMusicVolumeRequestV1,
    DeskBoxMusicVolumeResultV1, DeskBoxNativeUtf16StringV1,
};

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

struct HandleGuard(HANDLE);

impl Drop for HandleGuard {
    fn drop(&mut self) {
        // SAFETY: The guard owns a successful OpenProcess result.
        let _ = unsafe { CloseHandle(self.0) };
    }
}

struct SessionCandidate {
    volume: ISimpleAudioVolume,
    process_id: u32,
    identity: SessionIdentity,
}

struct SessionIdentity {
    is_system_sounds: bool,
    process_name: String,
    display_name: String,
    session_identifier: String,
    session_instance_identifier: String,
}

pub(crate) unsafe fn execute(
    request: &DeskBoxMusicVolumeRequestV1,
    result: &mut DeskBoxMusicVolumeResultV1,
) -> u32 {
    result.attempted_phases |= DESKBOX_MUSIC_VOLUME_PHASE_COM_INITIALIZE;
    // SAFETY: COM initialization is balanced by the guard for S_OK and S_FALSE.
    let com_hresult = unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) };
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

    result.attempted_phases |= DESKBOX_MUSIC_VOLUME_PHASE_CREATE_ENUMERATOR;
    // SAFETY: The CLSID and requested interface are fixed Core Audio contracts.
    let device_enumerator: IMMDeviceEnumerator =
        match unsafe { CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL) } {
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

    result.attempted_phases |= DESKBOX_MUSIC_VOLUME_PHASE_GET_DEVICE;
    // SAFETY: The fixed flow and role fallback match DeskBox's render endpoint contract.
    let device = match unsafe { default_render_endpoint(&device_enumerator) } {
        Ok(value) => {
            result.device_hresult = DESKBOX_NATIVE_S_OK;
            value
        }
        Err(error) => {
            result.device_hresult = error.code().0;
            return finish(
                result,
                DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
                error.code().0,
                false,
            );
        }
    };

    match request.operation {
        DESKBOX_MUSIC_VOLUME_OPERATION_GET_SNAPSHOT => {
            read_system_volume(&device, result);
            // SAFETY: The exported ABI validated both UTF-16 inputs.
            let source_app = unsafe { decode_input(&request.source_app_user_model_id) };
            // SAFETY: The exported ABI validated both UTF-16 inputs.
            let source_display_name = unsafe { decode_input(&request.source_display_name) };
            read_session_volume(&device, &source_app, &source_display_name, result);
            finish(result, DESKBOX_NATIVE_STATUS_OK, DESKBOX_NATIVE_S_OK, true)
        }
        DESKBOX_MUSIC_VOLUME_OPERATION_GET_SYSTEM => {
            read_system_volume(&device, result);
            finish(result, DESKBOX_NATIVE_STATUS_OK, DESKBOX_NATIVE_S_OK, true)
        }
        DESKBOX_MUSIC_VOLUME_OPERATION_SET_SYSTEM => {
            set_system_volume(&device, request.volume, result)
        }
        DESKBOX_MUSIC_VOLUME_OPERATION_SET_SESSION => {
            // SAFETY: The exported ABI validated both UTF-16 inputs.
            let source_app = unsafe { decode_input(&request.source_app_user_model_id) };
            // SAFETY: The exported ABI validated both UTF-16 inputs.
            let source_display_name = unsafe { decode_input(&request.source_display_name) };
            set_session_volume(
                &device,
                &source_app,
                &source_display_name,
                request.volume,
                result,
            )
        }
        _ => finish(
            result,
            DESKBOX_NATIVE_STATUS_INVALID_ARGUMENT,
            DESKBOX_NATIVE_E_INVALIDARG,
            false,
        ),
    }
}

unsafe fn default_render_endpoint(
    device_enumerator: &IMMDeviceEnumerator,
) -> windows::core::Result<IMMDevice> {
    // SAFETY: Fixed Core Audio flow and role values on a valid enumerator.
    unsafe { device_enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia) }
        .or_else(|_| unsafe { device_enumerator.GetDefaultAudioEndpoint(eRender, eConsole) })
}

fn read_system_volume(device: &IMMDevice, result: &mut DeskBoxMusicVolumeResultV1) {
    result.attempted_phases |= DESKBOX_MUSIC_VOLUME_PHASE_SYSTEM_VOLUME;
    // SAFETY: The endpoint is the current default render IMMDevice.
    let endpoint: IAudioEndpointVolume = match unsafe { device.Activate(CLSCTX_ALL, None) } {
        Ok(value) => value,
        Err(error) => {
            result.system_hresult = error.code().0;
            return;
        }
    };

    // SAFETY: The endpoint interface is valid for this call.
    match unsafe { endpoint.GetMasterVolumeLevelScalar() } {
        Ok(value) => {
            result.system_hresult = DESKBOX_NATIVE_S_OK;
            result.system_volume = normalize_volume(value as f64);
        }
        Err(error) => result.system_hresult = error.code().0,
    }
}

fn set_system_volume(
    device: &IMMDevice,
    volume: f64,
    result: &mut DeskBoxMusicVolumeResultV1,
) -> u32 {
    result.attempted_phases |= DESKBOX_MUSIC_VOLUME_PHASE_SYSTEM_VOLUME;
    // SAFETY: The endpoint is the current default render IMMDevice.
    let endpoint: IAudioEndpointVolume = match unsafe { device.Activate(CLSCTX_ALL, None) } {
        Ok(value) => value,
        Err(error) => {
            result.system_hresult = error.code().0;
            return finish(
                result,
                DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
                error.code().0,
                false,
            );
        }
    };

    let event_context = GUID::zeroed();
    // SAFETY: The endpoint interface is valid and event_context remains alive for the call.
    match unsafe {
        endpoint.SetMasterVolumeLevelScalar(normalize_volume(volume) as f32, &event_context)
    } {
        Ok(()) => {
            result.system_hresult = DESKBOX_NATIVE_S_OK;
            result.system_volume = normalize_volume(volume);
            finish(result, DESKBOX_NATIVE_STATUS_OK, DESKBOX_NATIVE_S_OK, true)
        }
        Err(error) => {
            result.system_hresult = error.code().0;
            finish(
                result,
                DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
                error.code().0,
                false,
            )
        }
    }
}

fn read_session_volume(
    device: &IMMDevice,
    source_app: &str,
    source_display_name: &str,
    result: &mut DeskBoxMusicVolumeResultV1,
) {
    let Some((volume, match_kind)) = find_session(device, source_app, source_display_name, result)
    else {
        return;
    };

    result.match_kind = match_kind;
    result.attempted_phases |= DESKBOX_MUSIC_VOLUME_PHASE_SESSION_VOLUME;
    // SAFETY: The volume interface remains alive for this call.
    match unsafe { volume.GetMasterVolume() } {
        Ok(value) => {
            result.session_hresult = DESKBOX_NATIVE_S_OK;
            result.session_volume = normalize_volume(value as f64);
            result.has_session_volume = 1;
        }
        Err(error) => result.session_hresult = error.code().0,
    }
}

fn set_session_volume(
    device: &IMMDevice,
    source_app: &str,
    source_display_name: &str,
    requested_volume: f64,
    result: &mut DeskBoxMusicVolumeResultV1,
) -> u32 {
    let Some((volume, match_kind)) = find_session(device, source_app, source_display_name, result)
    else {
        let hresult = result.session_hresult;
        return finish(
            result,
            DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
            if hresult < 0 {
                hresult
            } else {
                DESKBOX_NATIVE_S_FALSE
            },
            false,
        );
    };

    result.match_kind = match_kind;
    result.attempted_phases |= DESKBOX_MUSIC_VOLUME_PHASE_SESSION_VOLUME;
    let event_context = GUID::zeroed();
    // SAFETY: The volume interface and event_context remain alive for the call.
    match unsafe {
        volume.SetMasterVolume(normalize_volume(requested_volume) as f32, &event_context)
    } {
        Ok(()) => {
            result.session_hresult = DESKBOX_NATIVE_S_OK;
            result.session_volume = normalize_volume(requested_volume);
            result.has_session_volume = 1;
            finish(result, DESKBOX_NATIVE_STATUS_OK, DESKBOX_NATIVE_S_OK, true)
        }
        Err(error) => {
            result.session_hresult = error.code().0;
            finish(
                result,
                DESKBOX_NATIVE_STATUS_OPERATION_FAILED,
                error.code().0,
                false,
            )
        }
    }
}

fn find_session(
    device: &IMMDevice,
    source_app: &str,
    source_display_name: &str,
    result: &mut DeskBoxMusicVolumeResultV1,
) -> Option<(ISimpleAudioVolume, u32)> {
    result.attempted_phases |= DESKBOX_MUSIC_VOLUME_PHASE_ENUMERATE_SESSIONS;
    // SAFETY: The endpoint is the current default render IMMDevice.
    let manager: IAudioSessionManager2 = match unsafe { device.Activate(CLSCTX_ALL, None) } {
        Ok(value) => value,
        Err(error) => {
            result.session_hresult = error.code().0;
            return None;
        }
    };
    // SAFETY: The session manager interface is valid for this call.
    let enumerator = match unsafe { manager.GetSessionEnumerator() } {
        Ok(value) => value,
        Err(error) => {
            result.session_hresult = error.code().0;
            return None;
        }
    };
    // SAFETY: The session enumerator interface is valid for this call.
    let count = match unsafe { enumerator.GetCount() } {
        Ok(value) => value,
        Err(error) => {
            result.session_hresult = error.code().0;
            return None;
        }
    };

    let normalized_source_app = normalize_match_text(source_app);
    let normalized_source_display_name = normalize_match_text(source_display_name);
    let mut fallback_sessions = Vec::new();
    for index in 0..count {
        // SAFETY: index is bounded by the enumerator's reported count.
        let Ok(control) = (unsafe { enumerator.GetSession(index) }) else {
            continue;
        };
        let Ok(control2) = control.cast::<IAudioSessionControl2>() else {
            continue;
        };
        let Ok(volume) = control.cast::<ISimpleAudioVolume>() else {
            continue;
        };

        // SAFETY: Returned strings are task-allocator values owned by the caller.
        let display_name = unsafe { take_task_mem_string(control.GetDisplayName()) };
        // SAFETY: Returned strings are task-allocator values owned by the caller.
        let session_identifier = unsafe { take_task_mem_string(control2.GetSessionIdentifier()) };
        // SAFETY: Returned strings are task-allocator values owned by the caller.
        let session_instance_identifier =
            unsafe { take_task_mem_string(control2.GetSessionInstanceIdentifier()) };
        // SAFETY: The session control interface is valid for this call.
        let process_id = unsafe { control2.GetProcessId() }.unwrap_or(0);
        // SAFETY: S_OK identifies the system-sounds session by contract.
        let is_system_sounds = unsafe { control2.IsSystemSoundsSession() }.0 == DESKBOX_NATIVE_S_OK;
        let candidate = SessionCandidate {
            volume,
            process_id,
            identity: SessionIdentity {
                is_system_sounds,
                process_name: process_name(process_id),
                display_name,
                session_identifier,
                session_instance_identifier,
            },
        };

        let match_kind = matching_kind(
            &candidate.identity,
            &normalized_source_app,
            &normalized_source_display_name,
        );
        if match_kind != DESKBOX_MUSIC_VOLUME_MATCH_NONE {
            result.session_hresult = DESKBOX_NATIVE_S_OK;
            return Some((candidate.volume, match_kind));
        }

        if candidate.process_id != 0 && !candidate.identity.is_system_sounds {
            fallback_sessions.push(candidate);
        }
    }

    if fallback_sessions.len() == 1 {
        result.session_hresult = DESKBOX_NATIVE_S_OK;
        fallback_sessions
            .pop()
            .map(|candidate| (candidate.volume, DESKBOX_MUSIC_VOLUME_MATCH_SINGLE_FALLBACK))
    } else {
        result.session_hresult = DESKBOX_NATIVE_S_FALSE;
        None
    }
}

fn matching_kind(
    identity: &SessionIdentity,
    normalized_source_app: &str,
    normalized_source_display_name: &str,
) -> u32 {
    if identity.is_system_sounds {
        return DESKBOX_MUSIC_VOLUME_MATCH_NONE;
    }

    let process_name = normalize_match_text(&identity.process_name);
    let display_name = normalize_match_text(&identity.display_name);
    let session_identifier = normalize_match_text(&identity.session_identifier);
    let session_instance_identifier = normalize_match_text(&identity.session_instance_identifier);

    if contains_meaningful_match(&session_identifier, normalized_source_app) {
        DESKBOX_MUSIC_VOLUME_MATCH_IDENTIFIER_APP_ID
    } else if contains_meaningful_match(&session_instance_identifier, normalized_source_app) {
        DESKBOX_MUSIC_VOLUME_MATCH_INSTANCE_APP_ID
    } else if contains_meaningful_match(&display_name, normalized_source_display_name) {
        DESKBOX_MUSIC_VOLUME_MATCH_DISPLAY_NAME
    } else if contains_meaningful_match(&process_name, normalized_source_display_name) {
        DESKBOX_MUSIC_VOLUME_MATCH_PROCESS_DISPLAY_NAME
    } else if contains_meaningful_match(normalized_source_app, &process_name) {
        DESKBOX_MUSIC_VOLUME_MATCH_APP_ID_PROCESS
    } else if contains_meaningful_match(&session_identifier, normalized_source_display_name) {
        DESKBOX_MUSIC_VOLUME_MATCH_IDENTIFIER_DISPLAY_NAME
    } else {
        DESKBOX_MUSIC_VOLUME_MATCH_NONE
    }
}

fn contains_meaningful_match(haystack: &str, needle: &str) -> bool {
    needle.encode_utf16().count() >= 3
        && haystack.encode_utf16().count() >= 3
        && haystack.contains(needle)
}

fn normalize_match_text(value: &str) -> String {
    value
        .chars()
        .flat_map(char::to_lowercase)
        // C# char.IsLetterOrDigit evaluates UTF-16 code units, so surrogate-pair
        // characters are removed by the legacy implementation.
        .filter(|value| value.len_utf16() == 1 && value.is_alphanumeric())
        .collect()
}

fn process_name(process_id: u32) -> String {
    if process_id == 0 {
        return String::new();
    }

    // SAFETY: Requested access is read-only and the process id comes from Core Audio.
    let handle = match unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, process_id) }
    {
        Ok(value) => HandleGuard(value),
        Err(_) => return String::new(),
    };
    let mut buffer = vec![0u16; 32_768];
    let mut length = buffer.len() as u32;
    // SAFETY: The buffer is writable for length UTF-16 units and the handle remains alive.
    if unsafe {
        QueryFullProcessImageNameW(
            handle.0,
            PROCESS_NAME_WIN32,
            PWSTR(buffer.as_mut_ptr()),
            &mut length,
        )
    }
    .is_err()
    {
        return String::new();
    }

    let path = String::from_utf16_lossy(&buffer[..length as usize]);
    let file_name = path.rsplit(['\\', '/']).next().unwrap_or_default();
    let has_exe_suffix = file_name
        .as_bytes()
        .get(file_name.len().saturating_sub(4)..)
        .is_some_and(|suffix| suffix.eq_ignore_ascii_case(b".exe"));
    let stem = if has_exe_suffix {
        &file_name[..file_name.len() - 4]
    } else {
        file_name
    };
    stem.to_string()
}

unsafe fn take_task_mem_string(value: windows::core::Result<PWSTR>) -> String {
    let Ok(value) = value else {
        return String::new();
    };
    if value.is_null() {
        return String::new();
    }

    // SAFETY: Core Audio returned a valid, null-terminated task-allocated string.
    let result = unsafe { value.to_string() }.unwrap_or_default();
    // SAFETY: Core Audio transfers ownership of these strings to the caller.
    unsafe { CoTaskMemFree(Some(value.as_ptr().cast())) };
    result
}

unsafe fn decode_input(value: &DeskBoxNativeUtf16StringV1) -> String {
    if value.length_chars == 0 {
        return String::new();
    }

    // SAFETY: The exported ABI validated pointer readability and the declared length.
    let value_slice = unsafe { slice::from_raw_parts(value.data, value.length_chars as usize) };
    String::from_utf16_lossy(value_slice)
}

fn normalize_volume(value: f64) -> f64 {
    if value.is_finite() {
        value.clamp(0.0, 1.0)
    } else {
        0.0
    }
}

fn finish(
    result: &mut DeskBoxMusicVolumeResultV1,
    status: u32,
    operation_hresult: i32,
    succeeded: bool,
) -> u32 {
    result.status = status;
    result.operation_hresult = operation_hresult;
    result.operation_succeeded = u32::from(succeeded);
    status
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn normalization_matches_legacy_ascii_and_cjk_rules() {
        assert_eq!(
            normalize_match_text("  Spotify.EXE_音乐  "),
            "spotifyexe音乐"
        );
        assert_eq!(normalize_match_text("A-B_C.1"), "abc1");
        assert_eq!(normalize_match_text("A\u{10400}B"), "ab");
    }

    #[test]
    fn meaningful_match_requires_three_characters() {
        assert!(!contains_meaningful_match("foobar", "fo"));
        assert!(contains_meaningful_match("foobar", "foo"));
        assert!(!contains_meaningful_match("fo", "foo"));
    }

    #[test]
    fn non_finite_and_out_of_range_volume_is_normalized_like_csharp() {
        assert_eq!(normalize_volume(f64::NAN), 0.0);
        assert_eq!(normalize_volume(f64::INFINITY), 0.0);
        assert_eq!(normalize_volume(-1.0), 0.0);
        assert_eq!(normalize_volume(2.0), 1.0);
        assert_eq!(normalize_volume(0.25), 0.25);
    }

    #[test]
    fn matching_priority_is_frozen() {
        let identity = SessionIdentity {
            process_name: "Spotify".to_string(),
            display_name: "Spotify Music".to_string(),
            session_identifier: "App.SpotifyABCD.Session".to_string(),
            session_instance_identifier: "Instance.SpotifyABCD.1".to_string(),
            is_system_sounds: false,
        };

        assert_eq!(
            matching_kind(&identity, "spotifyabcd", "spotifymusic"),
            DESKBOX_MUSIC_VOLUME_MATCH_IDENTIFIER_APP_ID
        );
        assert_eq!(
            matching_kind(&identity, "unknownapp", "spotifymusic"),
            DESKBOX_MUSIC_VOLUME_MATCH_DISPLAY_NAME
        );
    }

    #[test]
    fn every_legacy_matching_branch_is_preserved() {
        let cases = [
            (
                SessionIdentity {
                    process_name: String::new(),
                    display_name: String::new(),
                    session_identifier: "prefix-appid-suffix".to_string(),
                    session_instance_identifier: String::new(),
                    is_system_sounds: false,
                },
                "appid",
                "unused",
                DESKBOX_MUSIC_VOLUME_MATCH_IDENTIFIER_APP_ID,
            ),
            (
                SessionIdentity {
                    process_name: String::new(),
                    display_name: String::new(),
                    session_identifier: String::new(),
                    session_instance_identifier: "prefix-appid-suffix".to_string(),
                    is_system_sounds: false,
                },
                "appid",
                "unused",
                DESKBOX_MUSIC_VOLUME_MATCH_INSTANCE_APP_ID,
            ),
            (
                SessionIdentity {
                    process_name: String::new(),
                    display_name: "Player Display".to_string(),
                    session_identifier: String::new(),
                    session_instance_identifier: String::new(),
                    is_system_sounds: false,
                },
                "unknown",
                "playerdisplay",
                DESKBOX_MUSIC_VOLUME_MATCH_DISPLAY_NAME,
            ),
            (
                SessionIdentity {
                    process_name: "PlayerProcess".to_string(),
                    display_name: String::new(),
                    session_identifier: String::new(),
                    session_instance_identifier: String::new(),
                    is_system_sounds: false,
                },
                "unknown",
                "playerprocess",
                DESKBOX_MUSIC_VOLUME_MATCH_PROCESS_DISPLAY_NAME,
            ),
            (
                SessionIdentity {
                    process_name: "player".to_string(),
                    display_name: String::new(),
                    session_identifier: String::new(),
                    session_instance_identifier: String::new(),
                    is_system_sounds: false,
                },
                "companyplayerapp",
                "unknown",
                DESKBOX_MUSIC_VOLUME_MATCH_APP_ID_PROCESS,
            ),
            (
                SessionIdentity {
                    process_name: String::new(),
                    display_name: String::new(),
                    session_identifier: "prefix-playername-suffix".to_string(),
                    session_instance_identifier: String::new(),
                    is_system_sounds: false,
                },
                "unknown",
                "playername",
                DESKBOX_MUSIC_VOLUME_MATCH_IDENTIFIER_DISPLAY_NAME,
            ),
        ];

        for (identity, source_app, source_display_name, expected) in cases {
            assert_eq!(
                matching_kind(&identity, source_app, source_display_name),
                expected
            );
        }
    }

    #[test]
    fn system_sounds_never_match() {
        let identity = SessionIdentity {
            process_name: "spotify".to_string(),
            display_name: "spotify".to_string(),
            session_identifier: "spotify".to_string(),
            session_instance_identifier: "spotify".to_string(),
            is_system_sounds: true,
        };
        assert_eq!(
            matching_kind(&identity, "spotify", "spotify"),
            DESKBOX_MUSIC_VOLUME_MATCH_NONE
        );
    }

    #[test]
    fn process_name_matches_csharp_without_exe_suffix() {
        let name = process_name(std::process::id());

        assert!(!name.is_empty());
        assert!(!name.to_ascii_lowercase().ends_with(".exe"));
    }
}
