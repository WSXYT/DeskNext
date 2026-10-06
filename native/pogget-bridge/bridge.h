#pragma once
#include <stdint.h>
#ifdef _WIN32
#ifdef DESKNEST_POGGET_EXPORT
#define DN_API __declspec(dllexport)
#else
#define DN_API __declspec(dllimport)
#endif
#else
#define DN_API
#endif
#ifdef __cplusplus
extern "C" {
#endif
// All strings are borrowed UTF-8 for the duration of a call; no native-owned buffers escape.
// IDs belong to the Core snapshot, never to a vector index or pointer.
typedef struct dn_icon {
    uint64_t id;
    const char* name;
    float x, y;
    int page, visible;
} dn_icon;
typedef struct dn_layout_config {
    int width, height, icon_size, gap, columns, rows, flow_mode, current_page, list_view;
} dn_layout_config;
typedef struct dn_layout_result { int page_count, current_page, columns, rows; } dn_layout_result;
// Returns 1 on success; otherwise output is unchanged. Input icon array is a snapshot,
// and positions are written back in the same order (including after native sorting).
DN_API int dn_layout(const dn_layout_config* config, dn_icon* icons, uint32_t count, dn_layout_result* result);
// Callback returns 1 accepted, 0 rejected, -1 cancelled. It must cooperate with
// cancellation; callback may call dn_flow_cancel but must not call dn_flow_destroy.
typedef int (*dn_tip_callback)(void* context, const char* title, const char* message);
// Core owns the complete file transaction. On success write the actual UTF-8 result
// path into actual_path (including NUL). 1=success, 0=refused, -1=cancelled.
typedef int (*dn_file_executor)(void* context, const char* source, const char* requested_destination,
    char* actual_path, uint32_t capacity);
typedef struct dn_flow dn_flow;
DN_API int dn_flow_validate(const char* json);
DN_API dn_flow* dn_flow_create(dn_tip_callback callback, void* context);
DN_API dn_flow* dn_flow_create_with_executor(dn_tip_callback tip, dn_file_executor executor, void* context);
DN_API int dn_flow_submit(dn_flow* flow, const char* json);
// 1=queued/running, 0=idle, -1=invalid/unavailable. Not an execution-success receipt.
DN_API int dn_flow_busy(dn_flow* flow);
DN_API void dn_flow_cancel(dn_flow* flow);
// Destroy waits until callbacks drain: call on a non-callback/non-UI thread.
DN_API void dn_flow_destroy(dn_flow* flow);
#ifdef __cplusplus
}
#endif
