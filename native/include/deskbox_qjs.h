#ifndef DESKBOX_QJS_H
#define DESKBOX_QJS_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#  if defined(DESKBOX_QJS_EXPORTS)
#    define DESKBOX_QJS_API __declspec(dllexport)
#  else
#    define DESKBOX_QJS_API __declspec(dllimport)
#  endif
#else
#  define DESKBOX_QJS_API
#endif

#define DESKBOX_QJS_ABI_VERSION 1u

#define DESKBOX_QJS_OK                 0
#define DESKBOX_QJS_ERROR              1
#define DESKBOX_QJS_TIMEOUT            2
#define DESKBOX_QJS_OOM                3
#define DESKBOX_QJS_INVALID_ARGUMENT   4
#define DESKBOX_QJS_EXPORT_NOT_FOUND   5

#define DESKBOX_QJS_DEFAULT_MEMORY_LIMIT_BYTES (4u * 1024u * 1024u)
#define DESKBOX_QJS_DEFAULT_MAX_STACK_BYTES    (256u * 1024u)
#define DESKBOX_QJS_MAX_SOURCE_BYTES           (128u * 1024u)
#define DESKBOX_QJS_MAX_RESULT_JSON_BYTES      (512u * 1024u)

DESKBOX_QJS_API uint32_t __cdecl deskbox_qjs_abi_version(void);

DESKBOX_QJS_API int32_t __cdecl deskbox_qjs_compile_module(
    const uint8_t *source_utf8,
    size_t source_len,
    const char *module_name_utf8,
    uint32_t timeout_ms,
    size_t memory_limit_bytes,
    uint8_t **out_bytecode,
    size_t *out_bytecode_len,
    uint8_t **out_exports_json_utf8,
    size_t *out_exports_json_len,
    uint8_t **out_error_utf8,
    size_t *out_error_len);

DESKBOX_QJS_API int32_t __cdecl deskbox_qjs_invoke_bytecode(
    const uint8_t *bytecode,
    size_t bytecode_len,
    const char *fn_name_utf8,
    int32_t require_export,
    const uint8_t *payload_json_utf8,
    size_t payload_json_len,
    uint32_t timeout_ms,
    size_t memory_limit_bytes,
    uint8_t **out_result_json_utf8,
    size_t *out_result_json_len,
    uint8_t **out_error_utf8,
    size_t *out_error_len);

DESKBOX_QJS_API void __cdecl deskbox_qjs_free_buffer(uint8_t *buffer);

#ifdef __cplusplus
}
#endif

#endif /* DESKBOX_QJS_H */
