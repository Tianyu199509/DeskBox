#ifndef DESKBOX_NATIVE_H
#define DESKBOX_NATIVE_H

#include <stdint.h>

#define DESKBOX_NATIVE_ABI_VERSION 2u
#define DESKBOX_EXPLORER_SHELL_LAUNCH_STRUCT_VERSION_1 1u
#define DESKBOX_QUICK_ACCESS_STRUCT_VERSION_1 1u
#define DESKBOX_RECYCLE_BIN_STRUCT_VERSION_1 1u
#define DESKBOX_MUSIC_VOLUME_STRUCT_VERSION_1 1u
#define DESKBOX_NATIVE_STRUCT_VERSION_2 2u
#define DESKBOX_NATIVE_DLL_NAME L"deskbox_native.dll"

#define DESKBOX_NATIVE_CAPABILITY_SHORTCUT_READ_STORED_RAW_V2 (1ull << 0)
#define DESKBOX_NATIVE_CAPABILITY_SHORTCUT_READ_EFFECTIVE_DIAGNOSTIC_V2 (1ull << 1)
#define DESKBOX_NATIVE_CAPABILITY_SHORTCUT_RESOLVE_NO_UI_V2 (1ull << 2)
#define DESKBOX_NATIVE_CAPABILITY_SHORTCUT_WRITE_V2 (1ull << 3)
#define DESKBOX_NATIVE_CAPABILITY_SHORTCUT_RESOLVE_WITH_UI_V2 (1ull << 4)
#define DESKBOX_NATIVE_CAPABILITY_MUSIC_VOLUME_V1 (1ull << 5)
#define DESKBOX_NATIVE_CAPABILITY_EXPLORER_SHELL_LAUNCH_V1 (1ull << 6)
#define DESKBOX_NATIVE_CAPABILITY_QUICK_ACCESS_V1 (1ull << 7)
#define DESKBOX_NATIVE_CAPABILITY_RECYCLE_BIN_V1 (1ull << 8)
#define DESKBOX_NATIVE_CAPABILITY_PLUGIN_SIGNATURE_V1 (1ull << 9)
#define DESKBOX_NATIVE_CAPABILITIES_STAGE_3C2 \
    (DESKBOX_NATIVE_CAPABILITY_SHORTCUT_READ_STORED_RAW_V2 | \
     DESKBOX_NATIVE_CAPABILITY_SHORTCUT_READ_EFFECTIVE_DIAGNOSTIC_V2 | \
     DESKBOX_NATIVE_CAPABILITY_SHORTCUT_RESOLVE_NO_UI_V2 | \
     DESKBOX_NATIVE_CAPABILITY_SHORTCUT_WRITE_V2 | \
     DESKBOX_NATIVE_CAPABILITY_SHORTCUT_RESOLVE_WITH_UI_V2)
#define DESKBOX_NATIVE_CAPABILITIES_STAGE_4C \
    (DESKBOX_NATIVE_CAPABILITIES_STAGE_3C2 | \
     DESKBOX_NATIVE_CAPABILITY_MUSIC_VOLUME_V1)
#define DESKBOX_NATIVE_CAPABILITIES_STAGE_4D4A \
    (DESKBOX_NATIVE_CAPABILITIES_STAGE_4C | \
     DESKBOX_NATIVE_CAPABILITY_EXPLORER_SHELL_LAUNCH_V1)
#define DESKBOX_NATIVE_CAPABILITIES_STAGE_4D4B \
    (DESKBOX_NATIVE_CAPABILITIES_STAGE_4D4A | \
     DESKBOX_NATIVE_CAPABILITY_QUICK_ACCESS_V1)
#define DESKBOX_NATIVE_CAPABILITIES_STAGE_5B4C1B1 \
    (DESKBOX_NATIVE_CAPABILITIES_STAGE_4D4B | \
     DESKBOX_NATIVE_CAPABILITY_RECYCLE_BIN_V1)
#define DESKBOX_NATIVE_CAPABILITIES_PLUGIN_V1 \
    (DESKBOX_NATIVE_CAPABILITIES_STAGE_5B4C1B1 | DESKBOX_NATIVE_CAPABILITY_PLUGIN_SIGNATURE_V1)
#define DESKBOX_NATIVE_CAPABILITIES DESKBOX_NATIVE_CAPABILITIES_PLUGIN_V1

#define DESKBOX_NATIVE_STATUS_OK 0u
#define DESKBOX_NATIVE_STATUS_INVALID_ARGUMENT 1u
#define DESKBOX_NATIVE_STATUS_INCOMPATIBLE_STRUCT 2u
#define DESKBOX_NATIVE_STATUS_BUFFER_TOO_SMALL 3u
#define DESKBOX_NATIVE_STATUS_COM_INITIALIZATION_FAILED 4u
#define DESKBOX_NATIVE_STATUS_OBJECT_CREATION_FAILED 5u
#define DESKBOX_NATIVE_STATUS_LOAD_FAILED 6u
#define DESKBOX_NATIVE_STATUS_OPERATION_FAILED 7u
#define DESKBOX_NATIVE_STATUS_INTERNAL_ERROR 8u
#define DESKBOX_NATIVE_STATUS_NOT_IMPLEMENTED 9u

#define DESKBOX_NATIVE_S_OK ((int32_t)0x00000000u)
#define DESKBOX_NATIVE_S_FALSE ((int32_t)0x00000001u)
#define DESKBOX_NATIVE_E_NOTIMPL ((int32_t)0x80004001u)
#define DESKBOX_NATIVE_E_INVALIDARG ((int32_t)0x80070057u)
#define DESKBOX_NATIVE_HRESULT_INSUFFICIENT_BUFFER ((int32_t)0x8007007Au)
#define DESKBOX_NATIVE_HRESULT_NOT_ATTEMPTED ((int32_t)0x8000000Au)

#define DESKBOX_SHORTCUT_READ_MODE_STORED_RAW 1u
#define DESKBOX_SHORTCUT_READ_MODE_EFFECTIVE_DIAGNOSTIC 2u
#define DESKBOX_SHORTCUT_WRITE_FLAG_SHELL_NAMESPACE_TARGET (1u << 0)

#define DESKBOX_SHORTCUT_FIELD_TARGET_PATH (1u << 0)
#define DESKBOX_SHORTCUT_FIELD_DESCRIPTION (1u << 1)
#define DESKBOX_SHORTCUT_FIELD_ARGUMENTS (1u << 2)
#define DESKBOX_SHORTCUT_FIELD_WORKING_DIRECTORY (1u << 3)
#define DESKBOX_SHORTCUT_FIELD_ICON_PATH (1u << 4)

#define DESKBOX_SHORTCUT_PHASE_COM_INITIALIZE (1u << 0)
#define DESKBOX_SHORTCUT_PHASE_CREATE_OBJECT (1u << 1)
#define DESKBOX_SHORTCUT_PHASE_LOAD (1u << 2)
#define DESKBOX_SHORTCUT_PHASE_RESOLVE (1u << 3)
#define DESKBOX_SHORTCUT_PHASE_SAVE (1u << 4)

#define DESKBOX_MUSIC_VOLUME_OPERATION_GET_SNAPSHOT 1u
#define DESKBOX_MUSIC_VOLUME_OPERATION_GET_SYSTEM 2u
#define DESKBOX_MUSIC_VOLUME_OPERATION_SET_SYSTEM 3u
#define DESKBOX_MUSIC_VOLUME_OPERATION_SET_SESSION 4u

#define DESKBOX_MUSIC_VOLUME_PHASE_COM_INITIALIZE (1u << 0)
#define DESKBOX_MUSIC_VOLUME_PHASE_CREATE_ENUMERATOR (1u << 1)
#define DESKBOX_MUSIC_VOLUME_PHASE_GET_DEVICE (1u << 2)
#define DESKBOX_MUSIC_VOLUME_PHASE_SYSTEM_VOLUME (1u << 3)
#define DESKBOX_MUSIC_VOLUME_PHASE_ENUMERATE_SESSIONS (1u << 4)
#define DESKBOX_MUSIC_VOLUME_PHASE_SESSION_VOLUME (1u << 5)

#define DESKBOX_MUSIC_VOLUME_MATCH_NONE 0u
#define DESKBOX_MUSIC_VOLUME_MATCH_IDENTIFIER_APP_ID 1u
#define DESKBOX_MUSIC_VOLUME_MATCH_INSTANCE_APP_ID 2u
#define DESKBOX_MUSIC_VOLUME_MATCH_DISPLAY_NAME 3u
#define DESKBOX_MUSIC_VOLUME_MATCH_PROCESS_DISPLAY_NAME 4u
#define DESKBOX_MUSIC_VOLUME_MATCH_APP_ID_PROCESS 5u
#define DESKBOX_MUSIC_VOLUME_MATCH_IDENTIFIER_DISPLAY_NAME 6u
#define DESKBOX_MUSIC_VOLUME_MATCH_SINGLE_FALLBACK 7u

#define DESKBOX_EXPLORER_SHELL_LAUNCH_PHASE_COM_INITIALIZE (1u << 0)
#define DESKBOX_EXPLORER_SHELL_LAUNCH_PHASE_CREATE_OBJECT (1u << 1)
#define DESKBOX_EXPLORER_SHELL_LAUNCH_PHASE_WINDOWS (1u << 2)
#define DESKBOX_EXPLORER_SHELL_LAUNCH_PHASE_DESKTOP (1u << 3)
#define DESKBOX_EXPLORER_SHELL_LAUNCH_PHASE_DOCUMENT (1u << 4)
#define DESKBOX_EXPLORER_SHELL_LAUNCH_PHASE_APPLICATION (1u << 5)
#define DESKBOX_EXPLORER_SHELL_LAUNCH_PHASE_EXECUTE (1u << 6)

#define DESKBOX_QUICK_ACCESS_OPERATION_QUERY_PIN_STATE 1u
#define DESKBOX_QUICK_ACCESS_OPERATION_PIN 2u
#define DESKBOX_QUICK_ACCESS_OPERATION_UNPIN 3u

#define DESKBOX_QUICK_ACCESS_PIN_STATE_UNKNOWN 0u
#define DESKBOX_QUICK_ACCESS_PIN_STATE_NOT_PINNED 1u
#define DESKBOX_QUICK_ACCESS_PIN_STATE_PINNED 2u

#define DESKBOX_QUICK_ACCESS_PHASE_COM_INITIALIZE (1u << 0)
#define DESKBOX_QUICK_ACCESS_PHASE_CREATE_OBJECT (1u << 1)
#define DESKBOX_QUICK_ACCESS_PHASE_QUICK_NAMESPACE (1u << 2)
#define DESKBOX_QUICK_ACCESS_PHASE_ITEMS (1u << 3)
#define DESKBOX_QUICK_ACCESS_PHASE_ENUMERATE (1u << 4)
#define DESKBOX_QUICK_ACCESS_PHASE_ITEM_PATH (1u << 5)
#define DESKBOX_QUICK_ACCESS_PHASE_PROPERTY (1u << 6)
#define DESKBOX_QUICK_ACCESS_PHASE_PARENT_NAMESPACE (1u << 7)
#define DESKBOX_QUICK_ACCESS_PHASE_PARSE_NAME (1u << 8)
#define DESKBOX_QUICK_ACCESS_PHASE_INVOKE (1u << 9)

#define DESKBOX_RECYCLE_BIN_OPERATION_QUERY 1u
#define DESKBOX_RECYCLE_BIN_OPERATION_RESTORE 2u

#define DESKBOX_RECYCLE_BIN_PHASE_COM_INITIALIZE (1u << 0)
#define DESKBOX_RECYCLE_BIN_PHASE_CREATE_OBJECT (1u << 1)
#define DESKBOX_RECYCLE_BIN_PHASE_NAMESPACE (1u << 2)
#define DESKBOX_RECYCLE_BIN_PHASE_ITEMS (1u << 3)
#define DESKBOX_RECYCLE_BIN_PHASE_ENUMERATE (1u << 4)
#define DESKBOX_RECYCLE_BIN_PHASE_ITEM_NAME (1u << 5)
#define DESKBOX_RECYCLE_BIN_PHASE_PROPERTY (1u << 6)
#define DESKBOX_RECYCLE_BIN_PHASE_INVOKE (1u << 7)

#define DESKBOX_SHORTCUT_DEFAULT_RESOLVE_TIMEOUT_MS 3000u
#define DESKBOX_SHORTCUT_MAX_RESOLVE_TIMEOUT_MS 65535u
#define DESKBOX_SHORTCUT_MAX_INPUT_PATH_CHARS 32767u
#define DESKBOX_SHORTCUT_MAX_INPUT_VALUE_CHARS 32767u
#define DESKBOX_EXPLORER_SHELL_LAUNCH_MAX_INPUT_CHARS 32767u
#define DESKBOX_QUICK_ACCESS_MAX_INPUT_CHARS 32767u
#define DESKBOX_RECYCLE_BIN_MAX_INPUT_CHARS 32767u

#define DESKBOX_NATIVE_UTF16_BUFFER_V1_SIZE_64 16u
#define DESKBOX_NATIVE_UTF16_STRING_V1_SIZE_64 16u
#define DESKBOX_SHORTCUT_READ_REQUEST_V2_SIZE_64 144u
#define DESKBOX_SHORTCUT_READ_RESULT_V2_SIZE_64 136u
#define DESKBOX_SHORTCUT_RESOLVE_REQUEST_V2_SIZE_64 192u
#define DESKBOX_SHORTCUT_WRITE_REQUEST_V2_SIZE_64 144u
#define DESKBOX_SHORTCUT_WRITE_RESULT_V2_SIZE_64 96u
#define DESKBOX_SHORTCUT_UI_RESOLVE_REQUEST_V2_SIZE_64 64u
#define DESKBOX_SHORTCUT_UI_RESOLVE_RESULT_V2_SIZE_64 64u
#define DESKBOX_MUSIC_VOLUME_REQUEST_V1_SIZE_64 88u
#define DESKBOX_MUSIC_VOLUME_RESULT_V1_SIZE_64 104u
#define DESKBOX_EXPLORER_SHELL_LAUNCH_REQUEST_V1_SIZE_64 96u
#define DESKBOX_EXPLORER_SHELL_LAUNCH_RESULT_V1_SIZE_64 88u
#define DESKBOX_QUICK_ACCESS_REQUEST_V1_SIZE_64 96u
#define DESKBOX_QUICK_ACCESS_RESULT_V1_SIZE_64 112u
#define DESKBOX_RECYCLE_BIN_REQUEST_V1_SIZE_64 80u
#define DESKBOX_RECYCLE_BIN_RESULT_V1_SIZE_64 104u

#if defined(_WIN32)
#define DESKBOX_NATIVE_API __declspec(dllimport)
#define DESKBOX_NATIVE_CALL __cdecl
#else
#define DESKBOX_NATIVE_API
#define DESKBOX_NATIVE_CALL
#endif

typedef struct DeskBoxNativeUtf16BufferV1 {
    uint16_t* data;
    uint32_t capacity_chars;
    uint32_t reserved0;
} DeskBoxNativeUtf16BufferV1;

typedef struct DeskBoxNativeUtf16StringV1 {
    const uint16_t* data;
    uint32_t length_chars;
    uint32_t reserved0;
} DeskBoxNativeUtf16StringV1;

typedef struct DeskBoxShortcutReadRequestV2 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t mode;
    uint32_t flags;
    const uint16_t* shortcut_path;
    uint32_t shortcut_path_length_chars;
    uint32_t reserved0;
    DeskBoxNativeUtf16BufferV1 target_path;
    DeskBoxNativeUtf16BufferV1 description;
    DeskBoxNativeUtf16BufferV1 arguments;
    DeskBoxNativeUtf16BufferV1 working_directory;
    DeskBoxNativeUtf16BufferV1 icon_path;
    uint64_t reserved[4];
} DeskBoxShortcutReadRequestV2;

typedef struct DeskBoxShortcutReadResultV2 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t status;
    int32_t operation_hresult;
    uint32_t attempted_phases;
    int32_t com_hresult;
    int32_t create_hresult;
    int32_t load_hresult;
    int32_t resolve_hresult;
    uint32_t attempted_fields;
    uint32_t succeeded_fields;
    uint32_t present_fields;
    uint32_t caller_buffer_too_small_fields;
    uint32_t source_truncated_fields;
    int32_t target_hresult;
    int32_t description_hresult;
    int32_t arguments_hresult;
    int32_t working_directory_hresult;
    int32_t icon_hresult;
    int32_t icon_index;
    uint32_t target_required_chars;
    uint32_t description_required_chars;
    uint32_t arguments_required_chars;
    uint32_t working_directory_required_chars;
    uint32_t icon_required_chars;
    uint32_t reserved0;
    uint64_t reserved[4];
} DeskBoxShortcutReadResultV2;

typedef struct DeskBoxShortcutResolveRequestV2 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t timeout_ms;
    uint32_t flags;
    DeskBoxShortcutReadRequestV2 read_request;
    uint64_t reserved[4];
} DeskBoxShortcutResolveRequestV2;

typedef struct DeskBoxShortcutWriteRequestV2 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t flags;
    int32_t icon_index;
    DeskBoxNativeUtf16StringV1 shortcut_path;
    DeskBoxNativeUtf16StringV1 target_path;
    DeskBoxNativeUtf16StringV1 description;
    DeskBoxNativeUtf16StringV1 arguments;
    DeskBoxNativeUtf16StringV1 working_directory;
    DeskBoxNativeUtf16StringV1 icon_path;
    uint64_t reserved[4];
} DeskBoxShortcutWriteRequestV2;

typedef struct DeskBoxShortcutWriteResultV2 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t status;
    int32_t operation_hresult;
    uint32_t attempted_phases;
    int32_t com_hresult;
    int32_t create_hresult;
    int32_t save_hresult;
    uint32_t attempted_fields;
    uint32_t succeeded_fields;
    int32_t target_hresult;
    int32_t description_hresult;
    int32_t arguments_hresult;
    int32_t working_directory_hresult;
    int32_t icon_hresult;
    uint32_t reserved0;
    uint64_t reserved[4];
} DeskBoxShortcutWriteResultV2;

typedef struct DeskBoxShortcutUiResolveRequestV2 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t flags;
    uint32_t reserved0;
    DeskBoxNativeUtf16StringV1 shortcut_path;
    uint64_t owner_hwnd;
    uint64_t reserved[3];
} DeskBoxShortcutUiResolveRequestV2;

typedef struct DeskBoxShortcutUiResolveResultV2 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t status;
    int32_t operation_hresult;
    uint32_t attempted_phases;
    int32_t com_hresult;
    int32_t create_hresult;
    int32_t load_hresult;
    int32_t resolve_hresult;
    uint32_t resolve_flags;
    uint64_t reserved[3];
} DeskBoxShortcutUiResolveResultV2;

typedef struct DeskBoxMusicVolumeRequestV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t operation;
    uint32_t flags;
    DeskBoxNativeUtf16StringV1 source_app_user_model_id;
    DeskBoxNativeUtf16StringV1 source_display_name;
    double volume;
    uint64_t reserved[4];
} DeskBoxMusicVolumeRequestV1;

typedef struct DeskBoxMusicVolumeResultV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t status;
    int32_t operation_hresult;
    uint32_t attempted_phases;
    uint32_t match_kind;
    int32_t com_hresult;
    int32_t create_hresult;
    int32_t device_hresult;
    int32_t system_hresult;
    int32_t session_hresult;
    uint32_t has_session_volume;
    uint32_t operation_succeeded;
    uint32_t reserved0;
    double system_volume;
    double session_volume;
    uint64_t reserved[4];
} DeskBoxMusicVolumeResultV1;

typedef struct DeskBoxExplorerShellLaunchRequestV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t flags;
    uint32_t reserved0;
    DeskBoxNativeUtf16StringV1 path;
    DeskBoxNativeUtf16StringV1 working_directory;
    DeskBoxNativeUtf16StringV1 verb;
    uint64_t reserved[4];
} DeskBoxExplorerShellLaunchRequestV1;

typedef struct DeskBoxExplorerShellLaunchResultV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t status;
    int32_t operation_hresult;
    uint32_t attempted_phases;
    int32_t com_hresult;
    int32_t create_hresult;
    int32_t windows_hresult;
    int32_t desktop_hresult;
    int32_t document_hresult;
    int32_t application_hresult;
    int32_t execute_hresult;
    uint32_t operation_succeeded;
    uint32_t reserved0;
    uint64_t reserved[4];
} DeskBoxExplorerShellLaunchResultV1;

typedef struct DeskBoxQuickAccessRequestV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t operation;
    uint32_t flags;
    DeskBoxNativeUtf16StringV1 folder_path;
    DeskBoxNativeUtf16StringV1 parent_path;
    DeskBoxNativeUtf16StringV1 folder_name;
    uint64_t reserved[4];
} DeskBoxQuickAccessRequestV1;

typedef struct DeskBoxQuickAccessResultV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t status;
    int32_t operation_hresult;
    uint32_t attempted_phases;
    int32_t com_hresult;
    int32_t create_hresult;
    int32_t quick_namespace_hresult;
    int32_t items_hresult;
    int32_t enumerate_hresult;
    int32_t item_path_hresult;
    int32_t property_hresult;
    int32_t parent_namespace_hresult;
    int32_t parse_name_hresult;
    int32_t invoke_hresult;
    uint32_t pin_state;
    uint32_t operation_succeeded;
    uint32_t matched_item;
    uint32_t fallback_used;
    uint32_t reserved0;
    uint64_t reserved[4];
} DeskBoxQuickAccessResultV1;

typedef struct DeskBoxRecycleBinRequestV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t operation;
    uint32_t flags;
    DeskBoxNativeUtf16StringV1 original_parent;
    DeskBoxNativeUtf16StringV1 original_name;
    uint64_t reserved[4];
} DeskBoxRecycleBinRequestV1;

typedef struct DeskBoxRecycleBinResultV1 {
    uint32_t struct_size;
    uint32_t struct_version;
    uint32_t status;
    int32_t operation_hresult;
    uint32_t attempted_phases;
    int32_t com_hresult;
    int32_t create_hresult;
    int32_t namespace_hresult;
    int32_t items_hresult;
    int32_t enumerate_hresult;
    int32_t item_name_hresult;
    int32_t property_hresult;
    int32_t invoke_hresult;
    uint32_t matched_count;
    uint32_t restored_count;
    uint32_t operation_succeeded;
    uint32_t reserved0;
    uint32_t reserved1;
    uint64_t reserved[4];
} DeskBoxRecycleBinResultV1;

#if UINTPTR_MAX == UINT64_MAX
#if defined(__cplusplus)
static_assert(sizeof(DeskBoxNativeUtf16BufferV1) == DESKBOX_NATIVE_UTF16_BUFFER_V1_SIZE_64,
              "DeskBoxNativeUtf16BufferV1 ABI size changed");
static_assert(sizeof(DeskBoxNativeUtf16StringV1) == DESKBOX_NATIVE_UTF16_STRING_V1_SIZE_64,
              "DeskBoxNativeUtf16StringV1 ABI size changed");
static_assert(sizeof(DeskBoxShortcutReadRequestV2) == DESKBOX_SHORTCUT_READ_REQUEST_V2_SIZE_64,
              "DeskBoxShortcutReadRequestV2 ABI size changed");
static_assert(sizeof(DeskBoxShortcutReadResultV2) == DESKBOX_SHORTCUT_READ_RESULT_V2_SIZE_64,
              "DeskBoxShortcutReadResultV2 ABI size changed");
static_assert(sizeof(DeskBoxShortcutResolveRequestV2) == DESKBOX_SHORTCUT_RESOLVE_REQUEST_V2_SIZE_64,
              "DeskBoxShortcutResolveRequestV2 ABI size changed");
static_assert(sizeof(DeskBoxShortcutWriteRequestV2) == DESKBOX_SHORTCUT_WRITE_REQUEST_V2_SIZE_64,
              "DeskBoxShortcutWriteRequestV2 ABI size changed");
static_assert(sizeof(DeskBoxShortcutWriteResultV2) == DESKBOX_SHORTCUT_WRITE_RESULT_V2_SIZE_64,
              "DeskBoxShortcutWriteResultV2 ABI size changed");
static_assert(sizeof(DeskBoxShortcutUiResolveRequestV2) == DESKBOX_SHORTCUT_UI_RESOLVE_REQUEST_V2_SIZE_64,
              "DeskBoxShortcutUiResolveRequestV2 ABI size changed");
static_assert(sizeof(DeskBoxShortcutUiResolveResultV2) == DESKBOX_SHORTCUT_UI_RESOLVE_RESULT_V2_SIZE_64,
              "DeskBoxShortcutUiResolveResultV2 ABI size changed");
static_assert(sizeof(DeskBoxMusicVolumeRequestV1) == DESKBOX_MUSIC_VOLUME_REQUEST_V1_SIZE_64,
              "DeskBoxMusicVolumeRequestV1 ABI size changed");
static_assert(sizeof(DeskBoxMusicVolumeResultV1) == DESKBOX_MUSIC_VOLUME_RESULT_V1_SIZE_64,
              "DeskBoxMusicVolumeResultV1 ABI size changed");
static_assert(sizeof(DeskBoxExplorerShellLaunchRequestV1) == DESKBOX_EXPLORER_SHELL_LAUNCH_REQUEST_V1_SIZE_64,
              "DeskBoxExplorerShellLaunchRequestV1 ABI size changed");
static_assert(sizeof(DeskBoxExplorerShellLaunchResultV1) == DESKBOX_EXPLORER_SHELL_LAUNCH_RESULT_V1_SIZE_64,
              "DeskBoxExplorerShellLaunchResultV1 ABI size changed");
static_assert(sizeof(DeskBoxQuickAccessRequestV1) == DESKBOX_QUICK_ACCESS_REQUEST_V1_SIZE_64,
              "DeskBoxQuickAccessRequestV1 ABI size changed");
static_assert(sizeof(DeskBoxQuickAccessResultV1) == DESKBOX_QUICK_ACCESS_RESULT_V1_SIZE_64,
              "DeskBoxQuickAccessResultV1 ABI size changed");
static_assert(sizeof(DeskBoxRecycleBinRequestV1) == DESKBOX_RECYCLE_BIN_REQUEST_V1_SIZE_64,
              "DeskBoxRecycleBinRequestV1 ABI size changed");
static_assert(sizeof(DeskBoxRecycleBinResultV1) == DESKBOX_RECYCLE_BIN_RESULT_V1_SIZE_64,
              "DeskBoxRecycleBinResultV1 ABI size changed");
#elif defined(__STDC_VERSION__) && __STDC_VERSION__ >= 201112L
_Static_assert(sizeof(DeskBoxNativeUtf16BufferV1) == DESKBOX_NATIVE_UTF16_BUFFER_V1_SIZE_64,
               "DeskBoxNativeUtf16BufferV1 ABI size changed");
_Static_assert(sizeof(DeskBoxNativeUtf16StringV1) == DESKBOX_NATIVE_UTF16_STRING_V1_SIZE_64,
               "DeskBoxNativeUtf16StringV1 ABI size changed");
_Static_assert(sizeof(DeskBoxShortcutReadRequestV2) == DESKBOX_SHORTCUT_READ_REQUEST_V2_SIZE_64,
               "DeskBoxShortcutReadRequestV2 ABI size changed");
_Static_assert(sizeof(DeskBoxShortcutReadResultV2) == DESKBOX_SHORTCUT_READ_RESULT_V2_SIZE_64,
               "DeskBoxShortcutReadResultV2 ABI size changed");
_Static_assert(sizeof(DeskBoxShortcutResolveRequestV2) == DESKBOX_SHORTCUT_RESOLVE_REQUEST_V2_SIZE_64,
               "DeskBoxShortcutResolveRequestV2 ABI size changed");
_Static_assert(sizeof(DeskBoxShortcutWriteRequestV2) == DESKBOX_SHORTCUT_WRITE_REQUEST_V2_SIZE_64,
               "DeskBoxShortcutWriteRequestV2 ABI size changed");
_Static_assert(sizeof(DeskBoxShortcutWriteResultV2) == DESKBOX_SHORTCUT_WRITE_RESULT_V2_SIZE_64,
               "DeskBoxShortcutWriteResultV2 ABI size changed");
_Static_assert(sizeof(DeskBoxShortcutUiResolveRequestV2) == DESKBOX_SHORTCUT_UI_RESOLVE_REQUEST_V2_SIZE_64,
               "DeskBoxShortcutUiResolveRequestV2 ABI size changed");
_Static_assert(sizeof(DeskBoxShortcutUiResolveResultV2) == DESKBOX_SHORTCUT_UI_RESOLVE_RESULT_V2_SIZE_64,
               "DeskBoxShortcutUiResolveResultV2 ABI size changed");
_Static_assert(sizeof(DeskBoxMusicVolumeRequestV1) == DESKBOX_MUSIC_VOLUME_REQUEST_V1_SIZE_64,
               "DeskBoxMusicVolumeRequestV1 ABI size changed");
_Static_assert(sizeof(DeskBoxMusicVolumeResultV1) == DESKBOX_MUSIC_VOLUME_RESULT_V1_SIZE_64,
               "DeskBoxMusicVolumeResultV1 ABI size changed");
_Static_assert(sizeof(DeskBoxExplorerShellLaunchRequestV1) == DESKBOX_EXPLORER_SHELL_LAUNCH_REQUEST_V1_SIZE_64,
               "DeskBoxExplorerShellLaunchRequestV1 ABI size changed");
_Static_assert(sizeof(DeskBoxExplorerShellLaunchResultV1) == DESKBOX_EXPLORER_SHELL_LAUNCH_RESULT_V1_SIZE_64,
               "DeskBoxExplorerShellLaunchResultV1 ABI size changed");
_Static_assert(sizeof(DeskBoxQuickAccessRequestV1) == DESKBOX_QUICK_ACCESS_REQUEST_V1_SIZE_64,
               "DeskBoxQuickAccessRequestV1 ABI size changed");
_Static_assert(sizeof(DeskBoxQuickAccessResultV1) == DESKBOX_QUICK_ACCESS_RESULT_V1_SIZE_64,
               "DeskBoxQuickAccessResultV1 ABI size changed");
_Static_assert(sizeof(DeskBoxRecycleBinRequestV1) == DESKBOX_RECYCLE_BIN_REQUEST_V1_SIZE_64,
               "DeskBoxRecycleBinRequestV1 ABI size changed");
_Static_assert(sizeof(DeskBoxRecycleBinResultV1) == DESKBOX_RECYCLE_BIN_RESULT_V1_SIZE_64,
               "DeskBoxRecycleBinResultV1 ABI size changed");
#endif
#endif

#ifdef __cplusplus
extern "C" {
#endif

DESKBOX_NATIVE_API uint32_t DESKBOX_NATIVE_CALL deskbox_native_abi_version(void);
DESKBOX_NATIVE_API uint64_t DESKBOX_NATIVE_CALL deskbox_native_capabilities(void);
DESKBOX_NATIVE_API uint32_t DESKBOX_NATIVE_CALL deskbox_shortcut_read_v2(
    const DeskBoxShortcutReadRequestV2* request,
    DeskBoxShortcutReadResultV2* result);
DESKBOX_NATIVE_API uint32_t DESKBOX_NATIVE_CALL deskbox_shortcut_resolve_no_ui_v2(
    const DeskBoxShortcutResolveRequestV2* request,
    DeskBoxShortcutReadResultV2* result);
DESKBOX_NATIVE_API uint32_t DESKBOX_NATIVE_CALL deskbox_shortcut_write_v2(
    const DeskBoxShortcutWriteRequestV2* request,
    DeskBoxShortcutWriteResultV2* result);
DESKBOX_NATIVE_API uint32_t DESKBOX_NATIVE_CALL deskbox_shortcut_resolve_with_ui_v2(
    const DeskBoxShortcutUiResolveRequestV2* request,
    DeskBoxShortcutUiResolveResultV2* result);
DESKBOX_NATIVE_API uint32_t DESKBOX_NATIVE_CALL deskbox_music_volume_v1(
    const DeskBoxMusicVolumeRequestV1* request,
    DeskBoxMusicVolumeResultV1* result);
DESKBOX_NATIVE_API uint32_t DESKBOX_NATIVE_CALL deskbox_explorer_shell_launch_v1(
    const DeskBoxExplorerShellLaunchRequestV1* request,
    DeskBoxExplorerShellLaunchResultV1* result);
DESKBOX_NATIVE_API uint32_t DESKBOX_NATIVE_CALL deskbox_quick_access_v1(
    const DeskBoxQuickAccessRequestV1* request,
    DeskBoxQuickAccessResultV1* result);
DESKBOX_NATIVE_API uint32_t DESKBOX_NATIVE_CALL deskbox_recycle_bin_v1(
    const DeskBoxRecycleBinRequestV1* request,
    DeskBoxRecycleBinResultV1* result);

/* Returns status; valid is 0/1. Inputs are borrowed and never retained. */
DESKBOX_NATIVE_API uint32_t DESKBOX_NATIVE_CALL deskbox_plugin_verify_ed25519_v1(
    const uint8_t* signature, uint32_t signature_len,
    const uint8_t* message, uint32_t message_len,
    const uint8_t* public_key, uint32_t public_key_len,
    uint32_t* valid);

#ifdef __cplusplus
}
#endif

#endif
