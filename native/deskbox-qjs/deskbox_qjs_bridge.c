#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define DESKBOX_QJS_EXPORTS

#include <windows.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>

#include "vendor/quickjs.h"
#include "../include/deskbox_qjs.h"

typedef struct DeskBoxQjsGuard {
    int64_t deadline_qpc;
    volatile int32_t timed_out;
} DeskBoxQjsGuard;

static void init_guard(DeskBoxQjsGuard *guard, uint32_t timeout_ms)
{
    guard->timed_out = 0;
    if (timeout_ms == 0) {
        timeout_ms = 50;
    }
    LARGE_INTEGER freq;
    LARGE_INTEGER now;
    if (QueryPerformanceFrequency(&freq) && QueryPerformanceCounter(&now) && freq.QuadPart > 0) {
        int64_t delta = (freq.QuadPart * (int64_t)timeout_ms) / 1000;
        if (delta < 1) {
            delta = 1;
        }
        guard->deadline_qpc = now.QuadPart + delta;
    } else {
        guard->deadline_qpc = 0;
    }
}

static int deskbox_qjs_interrupt_handler(JSContext *ctx, void *opaque)
{
    (void)ctx;
    DeskBoxQjsGuard *guard = (DeskBoxQjsGuard *)opaque;
    if (!guard || guard->deadline_qpc <= 0) {
        return 0;
    }
    if (guard->timed_out) {
        return 1;
    }
    LARGE_INTEGER now;
    if (QueryPerformanceCounter(&now) && now.QuadPart >= guard->deadline_qpc) {
        guard->timed_out = 1;
        return 1;
    }
    return 0;
}

static JSModuleDef *deskbox_qjs_deny_module_loader(
    JSContext *ctx,
    const char *module_name,
    void *opaque)
{
    (void)opaque;
    JS_ThrowReferenceError(
        ctx,
        "External module import '%s' is disabled in the Custom Widget sandbox.",
        module_name ? module_name : "");
    return NULL;
}

static int alloc_copy_bytes(
    const void *src,
    size_t len,
    uint8_t **out_buf,
    size_t *out_len)
{
    if (!out_buf || !out_len) {
        return 0;
    }
    size_t alloc_size = len > 0 ? len : 1;
    uint8_t *buf = (uint8_t *)malloc(alloc_size);
    if (!buf) {
        *out_buf = NULL;
        *out_len = 0;
        return 0;
    }
    if (len > 0 && src) {
        memcpy(buf, src, len);
    } else {
        buf[0] = 0;
    }
    *out_buf = buf;
    *out_len = len;
    return 1;
}

static void set_error_text(
    const char *text,
    uint8_t **out_error_utf8,
    size_t *out_error_len)
{
    if (!out_error_utf8 || !out_error_len) {
        return;
    }
    if (!text) {
        text = "Unknown QuickJS error.";
    }
    alloc_copy_bytes(text, strlen(text), out_error_utf8, out_error_len);
}

static int32_t capture_exception_and_classify(
    JSContext *ctx,
    const DeskBoxQjsGuard *guard,
    uint8_t **out_error_utf8,
    size_t *out_error_len)
{
    JSValue exc = JS_GetException(ctx);
    if (guard && guard->timed_out) {
        JS_FreeValue(ctx, exc);
        set_error_text(
            "RangeError: Script execution timed out (exceeded sandbox CPU budget).",
            out_error_utf8,
            out_error_len);
        return DESKBOX_QJS_TIMEOUT;
    }

    size_t msg_len = 0;
    const char *msg = JS_ToCStringLen(ctx, &msg_len, exc);
    if (!msg) {
        JS_FreeValue(ctx, exc);
        set_error_text(
            "InternalError: Script exceeded 4 MB sandbox memory limit (Out of Memory).",
            out_error_utf8,
            out_error_len);
        return DESKBOX_QJS_OOM;
    }

    if (strstr(msg, "out of memory") != NULL) {
        JS_FreeCString(ctx, msg);
        JS_FreeValue(ctx, exc);
        set_error_text(
            "InternalError: Script exceeded 4 MB sandbox memory limit (Out of Memory).",
            out_error_utf8,
            out_error_len);
        return DESKBOX_QJS_OOM;
    }

    const char *stack_str = NULL;
    size_t stack_len = 0;
    JSValue stack_val = JS_UNDEFINED;
    if (JS_IsObject(exc)) {
        stack_val = JS_GetPropertyStr(ctx, exc, "stack");
        if (!JS_IsException(stack_val) && !JS_IsUndefined(stack_val) && !JS_IsNull(stack_val)) {
            stack_str = JS_ToCStringLen(ctx, &stack_len, stack_val);
        }
    }

    if (stack_str && stack_len > 0 && strstr(msg, stack_str) == NULL) {
        size_t total = msg_len + 1 + stack_len;
        char *combined = (char *)malloc(total + 1);
        if (combined) {
            memcpy(combined, msg, msg_len);
            combined[msg_len] = '\n';
            memcpy(combined + msg_len + 1, stack_str, stack_len);
            combined[total] = '\0';
            alloc_copy_bytes(combined, total, out_error_utf8, out_error_len);
            free(combined);
        } else {
            alloc_copy_bytes(msg, msg_len, out_error_utf8, out_error_len);
        }
    } else {
        alloc_copy_bytes(msg, msg_len, out_error_utf8, out_error_len);
    }

    if (stack_str) {
        JS_FreeCString(ctx, stack_str);
    }
    JS_FreeValue(ctx, stack_val);
    JS_FreeCString(ctx, msg);
    JS_FreeValue(ctx, exc);
    return DESKBOX_QJS_ERROR;
}

static JSRuntime *create_sandboxed_runtime(
    DeskBoxQjsGuard *guard,
    uint32_t timeout_ms,
    size_t memory_limit_bytes)
{
    JSRuntime *rt = JS_NewRuntime();
    if (!rt) {
        return NULL;
    }
    size_t mem_limit = memory_limit_bytes > 0
        ? memory_limit_bytes
        : DESKBOX_QJS_DEFAULT_MEMORY_LIMIT_BYTES;
    JS_SetMemoryLimit(rt, mem_limit);
    JS_SetMaxStackSize(rt, DESKBOX_QJS_DEFAULT_MAX_STACK_BYTES);
    JS_UpdateStackTop(rt);
    JS_SetCanBlock(rt, false);
    JS_SetModuleLoaderFunc(rt, NULL, deskbox_qjs_deny_module_loader, NULL);
    init_guard(guard, timeout_ms);
    JS_SetInterruptHandler(rt, deskbox_qjs_interrupt_handler, guard);
    return rt;
}

static int drain_microtasks(JSRuntime *rt)
{
    JSContext *job_ctx = NULL;
    for (int i = 0; i < 1024; i++) {
        if (!JS_IsJobPending(rt)) {
            return 0;
        }
        int err = JS_ExecutePendingJob(rt, &job_ctx);
        if (err < 0) {
            return -1;
        }
    }
    return 0;
}

static int check_promise_rejection(JSContext *ctx, JSValue promise_val)
{
    if (JS_IsPromise(promise_val) && JS_PromiseState(ctx, promise_val) == JS_PROMISE_REJECTED) {
        JSValue reason = JS_PromiseResult(ctx, promise_val);
        JS_PromiseMarkAsHandled(ctx, promise_val);
        JS_Throw(ctx, reason);
        return -1;
    }
    return 0;
}

static const char DESKBOX_BOOTSTRAP_SCRIPT[] =
    "(function(){\n"
    "  'use strict';\n"
    "  const _Object = Object;\n"
    "  const _create = Object.create;\n"
    "  const _keys = Object.keys;\n"
    "  const _freeze = Object.freeze;\n"
    "  const _assign = Object.assign;\n"
    "  const _hasOwn = Object.prototype.hasOwnProperty;\n"
    "  const _isArray = Array.isArray;\n"
    "  const _String = String;\n"
    "  const _Number = Number;\n"
    "  const _Math = Math;\n"
    "  const _Set = Set;\n"
    "  const _jsonStringify = JSON.stringify;\n"
    "  const META_KEYS = new _Set(['id','createdAtUtcTicks','createdAt','updatedAtUtcTicks','timeText','dateText','isToday','fields']);\n"
    "  function toStateStr(v) {\n"
    "    if (v === undefined || v === null) return '';\n"
    "    const t = typeof v;\n"
    "    if (t === 'string') return v;\n"
    "    if (t === 'boolean') return v ? 'true' : 'false';\n"
    "    if (t === 'number') return _Number.isFinite(v) ? _String(v) : '';\n"
    "    try { return _jsonStringify(v) ?? ''; } catch { return _String(v); }\n"
    "  }\n"
    "  function deepFreeze(obj, depth) {\n"
    "    if (!obj || typeof obj !== 'object' || depth > 5) return obj;\n"
    "    const ks = _keys(obj);\n"
    "    for (let i = 0; i < ks.length; i++) {\n"
    "      deepFreeze(obj[ks[i]], depth + 1);\n"
    "    }\n"
    "    return _freeze(obj);\n"
    "  }\n"
    "  let _seq = 1;\n"
    "  function makeId(nowMs) {\n"
    "    const base = _Math.floor(_Number(nowMs) || 1700000000000).toString(36);\n"
    "    return (base + (_seq++).toString(36)).slice(-12);\n"
    "  }\n"
    "  function cloneItemForJs(raw, nowIso) {\n"
    "    const out = {};\n"
    "    if (raw && typeof raw === 'object') {\n"
    "      if (raw.fields && typeof raw.fields === 'object') {\n"
    "        const fks = _keys(raw.fields);\n"
    "        for (let i = 0; i < fks.length; i++) {\n"
    "          const k = fks[i];\n"
    "          out[k] = toStateStr(raw.fields[k]);\n"
    "        }\n"
    "      }\n"
    "      const ks = _keys(raw);\n"
    "      for (let i = 0; i < ks.length; i++) {\n"
    "        const k = ks[i];\n"
    "        if (k === 'fields') continue;\n"
    "        out[k] = toStateStr(raw[k]);\n"
    "      }\n"
    "    }\n"
    "    if (!out.id) out.id = makeId(0);\n"
    "    if (!out.createdAt) out.createdAt = nowIso || '';\n"
    "    return out;\n"
    "  }\n"
    "  function normalizeItemForHost(item, nowMs, nowIso) {\n"
    "    const fields = {};\n"
    "    let id = '';\n"
    "    let createdAt = nowIso || '';\n"
    "    let createdAtUtcTicks = 0;\n"
    "    if (item && typeof item === 'object') {\n"
    "      if (typeof item.id === 'string' && item.id.length > 0) id = item.id;\n"
    "      if (typeof item.createdAt === 'string' && item.createdAt.length > 0) createdAt = item.createdAt;\n"
    "      if (item.createdAtUtcTicks) createdAtUtcTicks = _Number(item.createdAtUtcTicks) || 0;\n"
    "      if (item.fields && typeof item.fields === 'object') {\n"
    "        const fks = _keys(item.fields);\n"
    "        for (let i = 0; i < fks.length; i++) {\n"
    "          const k = fks[i];\n"
    "          fields[k] = toStateStr(item.fields[k]);\n"
    "        }\n"
    "      }\n"
    "      const ks = _keys(item);\n"
    "      for (let i = 0; i < ks.length; i++) {\n"
    "        const k = ks[i];\n"
    "        if (META_KEYS.has(k)) continue;\n"
    "        fields[k] = toStateStr(item[k]);\n"
    "      }\n"
    "    }\n"
    "    if (!id) id = makeId(nowMs);\n"
    "    const jsView = _assign({}, fields, { id, createdAt });\n"
    "    if (createdAtUtcTicks > 0) jsView.createdAtUtcTicks = _String(createdAtUtcTicks);\n"
    "    return { id, createdAt, createdAtUtcTicks, fields, jsView };\n"
    "  }\n"
    "  return function prepareSession(moduleFn, rawPayload, fnName) {\n"
    "    const payload = rawPayload || {};\n"
    "    const nowMs = _Number(payload.env && payload.env.nowMs) || Date.now();\n"
    "    const nowIso = (payload.env && payload.env.isoDate) ? _String(payload.env.isoDate) : '';\n"
    "    const maxColItems = _Math.min(500, _Math.max(1, _Number(payload.maxCollectionItems) || 500));\n"
    "    const allowCopy = !!payload.allowCopy;\n"
    "    const initState = payload.state || {};\n"
    "    const state = {};\n"
    "    const initStateKeys = _keys(initState);\n"
    "    for (let i = 0; i < initStateKeys.length; i++) {\n"
    "      const k = initStateKeys[i];\n"
    "      state[k] = toStateStr(initState[k]);\n"
    "    }\n"
    "    const computed = (payload.computed && typeof payload.computed === 'object' && !_isArray(payload.computed))\n"
    "      ? _assign({}, payload.computed)\n"
    "      : {};\n"
    "    const settings = _freeze(_assign({}, payload.settings || {}));\n"
    "    const i18n = _freeze(_assign({}, payload.i18n || {}));\n"
    "    const rawArgs = (payload.args && typeof payload.args === 'object' && !_isArray(payload.args)) ? payload.args : {};\n"
    "    const args = _freeze(_assign({}, rawArgs));\n"
    "    const rawEv = payload.event || {};\n"
    "    const event = _freeze({\n"
    "      arg: rawEv.arg == null ? '' : _String(rawEv.arg),\n"
    "      value: rawEv.value == null ? '' : _String(rawEv.value),\n"
    "      id: rawEv.id == null ? '' : _String(rawEv.id),\n"
    "      x: rawEv.x == null ? null : _Number(rawEv.x),\n"
    "      y: rawEv.y == null ? null : _Number(rawEv.y),\n"
    "      fromIndex: rawEv.fromIndex == null ? null : _Number(rawEv.fromIndex),\n"
    "      toIndex: rawEv.toIndex == null ? null : _Number(rawEv.toIndex),\n"
    "      args: args,\n"
    "      item: (rawEv.item && typeof rawEv.item === 'object') ? _freeze(_assign({}, rawEv.item)) : null,\n"
    "      drop: (rawEv.drop && typeof rawEv.drop === 'object') ? _freeze(_assign({}, rawEv.drop)) : null\n"
    "    });\n"
    "    const env = deepFreeze(_assign({}, payload.env || {}), 0);\n"
    "    const rawCols = payload.collections || {};\n"
    "    const workingCols = _create(null);\n"
    "    const dirtyCols = new _Set();\n"
    "    function ensureCol(name) {\n"
    "      const colKey = _String(name || '');\n"
    "      if (!colKey) return [];\n"
    "      if (!workingCols[colKey]) {\n"
    "        const src = _isArray(rawCols[colKey]) ? rawCols[colKey] : [];\n"
    "        const arr = [];\n"
    "        for (let i = 0; i < src.length && i < maxColItems; i++) {\n"
    "          arr.push(normalizeItemForHost(src[i], nowMs, nowIso));\n"
    "        }\n"
    "        workingCols[colKey] = arr;\n"
    "      }\n"
    "      return workingCols[colKey];\n"
    "    }\n"
    "    const collections = _freeze({\n"
    "      get(name) {\n"
    "        const arr = ensureCol(name);\n"
    "        const res = [];\n"
    "        for (let i = 0; i < arr.length; i++) res.push(_assign({}, arr[i].jsView));\n"
    "        return res;\n"
    "      },\n"
    "      add(name, fieldsObj, opts) {\n"
    "        const colKey = _String(name || '');\n"
    "        if (!colKey) return null;\n"
    "        const arr = ensureCol(colKey);\n"
    "        const norm = normalizeItemForHost(fieldsObj || {}, nowMs, nowIso);\n"
    "        if (opts && opts.prepend === false) {\n"
    "          arr.push(norm);\n"
    "          while (arr.length > maxColItems) arr.shift();\n"
    "        } else {\n"
    "          arr.unshift(norm);\n"
    "          if (arr.length > maxColItems) arr.length = maxColItems;\n"
    "        }\n"
    "        dirtyCols.add(colKey);\n"
    "        return _assign({}, norm.jsView);\n"
    "      },\n"
    "      prepend(name, fieldsObj) {\n"
    "        return collections.add(name, fieldsObj, { prepend: true });\n"
    "      },\n"
    "      append(name, fieldsObj) {\n"
    "        return collections.add(name, fieldsObj, { prepend: false });\n"
    "      },\n"
    "      update(name, id, patchObj) {\n"
    "        const colKey = _String(name || '');\n"
    "        if (!colKey || !id || !patchObj || typeof patchObj !== 'object') return false;\n"
    "        const targetId = _String(id);\n"
    "        const arr = ensureCol(colKey);\n"
    "        for (let i = 0; i < arr.length; i++) {\n"
    "          if (arr[i].id === targetId) {\n"
    "            const merged = _assign({}, arr[i].jsView, patchObj, { id: targetId });\n"
    "            arr[i] = normalizeItemForHost(merged, nowMs, arr[i].createdAt);\n"
    "            dirtyCols.add(colKey);\n"
    "            return true;\n"
    "          }\n"
    "        }\n"
    "        return false;\n"
    "      },\n"
    "      remove(name, idOrPredicate) {\n"
    "        const colKey = _String(name || '');\n"
    "        if (!colKey || idOrPredicate == null) return 0;\n"
    "        const arr = ensureCol(colKey);\n"
    "        const before = arr.length;\n"
    "        const isFn = typeof idOrPredicate === 'function';\n"
    "        const targetId = isFn ? '' : _String(idOrPredicate);\n"
    "        const kept = [];\n"
    "        for (let i = 0; i < arr.length; i++) {\n"
    "          const drop = isFn ? !!idOrPredicate(_assign({}, arr[i].jsView), i) : (arr[i].id === targetId);\n"
    "          if (!drop) kept.push(arr[i]);\n"
    "        }\n"
    "        if (kept.length !== before) {\n"
    "          workingCols[colKey] = kept;\n"
    "          dirtyCols.add(colKey);\n"
    "        }\n"
    "        return before - kept.length;\n"
    "      },\n"
    "      clear(name) {\n"
    "        const colKey = _String(name || '');\n"
    "        if (!colKey) return;\n"
    "        workingCols[colKey] = [];\n"
    "        dirtyCols.add(colKey);\n"
    "      },\n"
    "      set(name, items) {\n"
    "        const colKey = _String(name || '');\n"
    "        if (!colKey || !_isArray(items)) return;\n"
    "        const next = [];\n"
    "        for (let i = 0; i < items.length && i < maxColItems; i++) {\n"
    "          next.push(normalizeItemForHost(items[i], nowMs, nowIso));\n"
    "        }\n"
    "        workingCols[colKey] = next;\n"
    "        dirtyCols.add(colKey);\n"
    "      }\n"
    "    });\n"
    "    function t(key, fallbackOrVars, maybeVars) {\n"
    "      const k = key == null ? '' : _String(key);\n"
    "      const secondIsObj = fallbackOrVars !== null && typeof fallbackOrVars === 'object' && !_isArray(fallbackOrVars);\n"
    "      let tpl = (_hasOwn.call(i18n, k) && i18n[k] !== '')\n"
    "        ? _String(i18n[k])\n"
    "        : (!secondIsObj && fallbackOrVars !== undefined && fallbackOrVars !== null ? _String(fallbackOrVars) : k);\n"
    "      const varsObj = secondIsObj\n"
    "        ? fallbackOrVars\n"
    "        : (maybeVars !== null && typeof maybeVars === 'object' && !_isArray(maybeVars) ? maybeVars : null);\n"
    "      if (varsObj) {\n"
    "        const vks = _keys(varsObj);\n"
    "        for (let i = 0; i < vks.length; i++) {\n"
    "          const vk = vks[i];\n"
    "          const rep = varsObj[vk] == null ? '' : _String(varsObj[vk]);\n"
    "          tpl = tpl.split('{' + vk + '}').join(rep);\n"
    "        }\n"
    "      } else if (arguments.length > 2) {\n"
    "        for (let i = 2; i < arguments.length; i++) {\n"
    "          const token = '{' + (i - 2) + '}';\n"
    "          const rep = arguments[i] == null ? '' : _String(arguments[i]);\n"
    "          tpl = tpl.split(token).join(rep);\n"
    "        }\n"
    "      }\n"
    "      return tpl;\n"
    "    }\n"
    "    const uiOps = [];\n"
    "    function pushUi(op) {\n"
    "      if (uiOps.length < 8) uiOps.push(op);\n"
    "    }\n"
    "    const dispatchedActions = [];\n"
    "    function dispatch(actOrArr) {\n"
    "      if (!actOrArr) return;\n"
    "      if (_isArray(actOrArr)) {\n"
    "        for (let i = 0; i < actOrArr.length && dispatchedActions.length < 8; i++) {\n"
    "          const a = actOrArr[i];\n"
    "          if (a && typeof a === 'object' && typeof a.type === 'string' && a.type.length > 0) {\n"
    "            dispatchedActions.push(a);\n"
    "          }\n"
    "        }\n"
    "      } else if (typeof actOrArr === 'object' && typeof actOrArr.type === 'string' && actOrArr.type.length > 0) {\n"
    "        if (dispatchedActions.length < 8) dispatchedActions.push(actOrArr);\n"
    "      }\n"
    "    }\n"
    "    const ui = _freeze({\n"
    "      toast(message, tone) {\n"
    "        if (message == null || message === '') return;\n"
    "        pushUi({ type: 'toast', message: _String(message), tone: tone == null ? 'info' : _String(tone) });\n"
    "      },\n"
    "      copy(text) {\n"
    "        if (!allowCopy || text == null) return false;\n"
    "        pushUi({ type: 'copy', text: _String(text) });\n"
    "        return true;\n"
    "      },\n"
    "      notify(title, body) {\n"
    "        if (!title && !body) return;\n"
    "        pushUi({ type: 'notify', title: title == null ? '' : _String(title), body: body == null ? '' : _String(body) });\n"
    "      },\n"
    "      refreshSource(sourceId) {\n"
    "        pushUi({ type: 'refreshSource', sourceId: sourceId == null ? '' : _String(sourceId) });\n"
    "      },\n"
    "      dispatch(actOrArr) {\n"
    "        dispatch(actOrArr);\n"
    "      }\n"
    "    });\n"
    "    const response = payload.rawPayload !== undefined ? payload.rawPayload : null;\n"
    "    const ctx = _freeze({ state, computed, settings, i18n, args, event, env, collections, t, ui, dispatch, response });\n"
    "    let callResult;\n"
    "    if (fnName === 'onSourceData') {\n"
    "      callResult = moduleFn(ctx, payload.sourceId == null ? '' : _String(payload.sourceId), payload.rawPayload);\n"
    "    } else if (fnName === 'onHttpResponse' || fnName === 'onCommandResult') {\n"
    "      callResult = moduleFn(ctx, payload.rawPayload, event);\n"
    "    } else {\n"
    "      callResult = moduleFn(ctx, event);\n"
    "    }\n"
    "    function finalize() {\n"
    "      const stateSet = {};\n"
    "      const stateRemoved = [];\n"
    "      let stateChanged = false;\n"
    "      const curStateKeys = _keys(state);\n"
    "      for (let i = 0; i < curStateKeys.length; i++) {\n"
    "        const k = curStateKeys[i];\n"
    "        const rawV = state[k];\n"
    "        if (rawV === undefined || rawV === null) {\n"
    "          if (_hasOwn.call(initState, k)) { stateRemoved.push(k); stateChanged = true; }\n"
    "          continue;\n"
    "        }\n"
    "        const sv = toStateStr(rawV);\n"
    "        if (!_hasOwn.call(initState, k) || toStateStr(initState[k]) !== sv) {\n"
    "          stateSet[k] = sv;\n"
    "          stateChanged = true;\n"
    "        }\n"
    "      }\n"
    "      for (let i = 0; i < initStateKeys.length; i++) {\n"
    "        const k = initStateKeys[i];\n"
    "        if (!_hasOwn.call(state, k)) {\n"
    "          stateRemoved.push(k);\n"
    "          stateChanged = true;\n"
    "        }\n"
    "      }\n"
    "      const collectionsDirty = {};\n"
    "      dirtyCols.forEach(function(colKey) {\n"
    "        const arr = workingCols[colKey] || [];\n"
    "        const outArr = [];\n"
    "        for (let i = 0; i < arr.length; i++) {\n"
    "          outArr.push({\n"
    "            id: arr[i].id,\n"
    "            createdAt: arr[i].createdAt,\n"
    "            createdAtUtcTicks: arr[i].createdAtUtcTicks,\n"
    "            fields: arr[i].fields\n"
    "          });\n"
    "        }\n"
    "        collectionsDirty[colKey] = outArr;\n"
    "      });\n"
    "      return {\n"
    "        invoked: true,\n"
    "        stateChanged: stateChanged,\n"
    "        stateSet: stateSet,\n"
    "        stateRemoved: stateRemoved,\n"
    "        computed: computed,\n"
    "        collectionsDirty: collectionsDirty,\n"
    "        uiOps: uiOps,\n"
    "        dispatchedActions: dispatchedActions\n"
    "      };\n"
    "    }\n"
    "    return { callResult, finalize };\n"
    "  };\n"
    "})()\n";

DESKBOX_QJS_API uint32_t __cdecl deskbox_qjs_abi_version(void)
{
    return DESKBOX_QJS_ABI_VERSION;
}

DESKBOX_QJS_API void __cdecl deskbox_qjs_free_buffer(uint8_t *buffer)
{
    if (buffer) {
        free(buffer);
    }
}

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
    size_t *out_error_len)
{
    if (!out_bytecode || !out_bytecode_len || !out_exports_json_utf8 ||
        !out_exports_json_len || !out_error_utf8 || !out_error_len) {
        return DESKBOX_QJS_INVALID_ARGUMENT;
    }

    *out_bytecode = NULL;
    *out_bytecode_len = 0;
    *out_exports_json_utf8 = NULL;
    *out_exports_json_len = 0;
    *out_error_utf8 = NULL;
    *out_error_len = 0;

    if (!source_utf8 || source_len == 0) {
        set_error_text("Script source is empty.", out_error_utf8, out_error_len);
        return DESKBOX_QJS_INVALID_ARGUMENT;
    }

    if (source_len > DESKBOX_QJS_MAX_SOURCE_BYTES) {
        set_error_text("Script source exceeds 128 KB maximum size.", out_error_utf8, out_error_len);
        return DESKBOX_QJS_INVALID_ARGUMENT;
    }

    char *nul_source = (char *)malloc(source_len + 1);
    if (!nul_source) {
        set_error_text("Host out of memory copying script source.", out_error_utf8, out_error_len);
        return DESKBOX_QJS_OOM;
    }
    memcpy(nul_source, source_utf8, source_len);
    nul_source[source_len] = '\0';

    DeskBoxQjsGuard guard;
    JSRuntime *rt = create_sandboxed_runtime(&guard, timeout_ms > 0 ? timeout_ms : 100, memory_limit_bytes);
    if (!rt) {
        free(nul_source);
        set_error_text("Failed to create QuickJS runtime.", out_error_utf8, out_error_len);
        return DESKBOX_QJS_OOM;
    }

    JSContext *ctx = JS_NewContext(rt);
    if (!ctx) {
        JS_FreeRuntime(rt);
        free(nul_source);
        set_error_text("Failed to create QuickJS context.", out_error_utf8, out_error_len);
        return DESKBOX_QJS_OOM;
    }

    const char *filename = (module_name_utf8 && module_name_utf8[0] != '\0')
        ? module_name_utf8
        : "logic.js";

    JSValue mod_val = JS_Eval(
        ctx,
        nul_source,
        source_len,
        filename,
        JS_EVAL_TYPE_MODULE | JS_EVAL_FLAG_COMPILE_ONLY);
    free(nul_source);

    if (JS_IsException(mod_val)) {
        int32_t code = capture_exception_and_classify(ctx, &guard, out_error_utf8, out_error_len);
        JS_FreeContext(ctx);
        JS_FreeRuntime(rt);
        return code;
    }

    if (JS_ResolveModule(ctx, mod_val) < 0) {
        JS_FreeValue(ctx, mod_val);
        int32_t code = capture_exception_and_classify(ctx, &guard, out_error_utf8, out_error_len);
        JS_FreeContext(ctx);
        JS_FreeRuntime(rt);
        return code;
    }

    size_t bc_len = 0;
    uint8_t *bc_buf = JS_WriteObject(ctx, &bc_len, mod_val, JS_WRITE_OBJ_BYTECODE);
    if (!bc_buf || bc_len == 0) {
        if (bc_buf) {
            js_free(ctx, bc_buf);
        }
        JS_FreeValue(ctx, mod_val);
        int32_t code = JS_HasException(ctx)
            ? capture_exception_and_classify(ctx, &guard, out_error_utf8, out_error_len)
            : DESKBOX_QJS_ERROR;
        if (*out_error_utf8 == NULL) {
            set_error_text("Failed to serialize compiled module bytecode.", out_error_utf8, out_error_len);
        }
        JS_FreeContext(ctx);
        JS_FreeRuntime(rt);
        return code;
    }

    int copied_bc = alloc_copy_bytes(bc_buf, bc_len, out_bytecode, out_bytecode_len);
    js_free(ctx, bc_buf);
    if (!copied_bc) {
        JS_FreeValue(ctx, mod_val);
        JS_FreeContext(ctx);
        JS_FreeRuntime(rt);
        set_error_text("Out of memory allocating module bytecode buffer.", out_error_utf8, out_error_len);
        return DESKBOX_QJS_OOM;
    }

    /* Evaluate module top-level scope so exported functions are initialized and verified. */
    JSModuleDef *m = (JSModuleDef *)JS_VALUE_GET_PTR(mod_val);
    JSValue eval_res = JS_EvalFunction(ctx, mod_val); /* consumes mod_val */
    if (JS_IsException(eval_res)) {
        free(*out_bytecode);
        *out_bytecode = NULL;
        *out_bytecode_len = 0;
        int32_t code = capture_exception_and_classify(ctx, &guard, out_error_utf8, out_error_len);
        JS_FreeContext(ctx);
        JS_FreeRuntime(rt);
        return code;
    }

    if (drain_microtasks(rt) < 0 || check_promise_rejection(ctx, eval_res) < 0) {
        JS_FreeValue(ctx, eval_res);
        free(*out_bytecode);
        *out_bytecode = NULL;
        *out_bytecode_len = 0;
        int32_t code = capture_exception_and_classify(ctx, &guard, out_error_utf8, out_error_len);
        JS_FreeContext(ctx);
        JS_FreeRuntime(rt);
        return code;
    }
    JS_FreeValue(ctx, eval_res);

    JSValue ns = JS_GetModuleNamespace(ctx, m);
    if (JS_IsException(ns)) {
        free(*out_bytecode);
        *out_bytecode = NULL;
        *out_bytecode_len = 0;
        int32_t code = capture_exception_and_classify(ctx, &guard, out_error_utf8, out_error_len);
        JS_FreeContext(ctx);
        JS_FreeRuntime(rt);
        return code;
    }

    JSPropertyEnum *ptab = NULL;
    uint32_t plen = 0;
    if (JS_GetOwnPropertyNames(ctx, &ptab, &plen, ns, JS_GPN_STRING_MASK | JS_GPN_ENUM_ONLY) < 0) {
        JS_FreeValue(ctx, ns);
        free(*out_bytecode);
        *out_bytecode = NULL;
        *out_bytecode_len = 0;
        int32_t code = capture_exception_and_classify(ctx, &guard, out_error_utf8, out_error_len);
        JS_FreeContext(ctx);
        JS_FreeRuntime(rt);
        return code;
    }

    JSValue fn_arr = JS_NewArray(ctx);
    JSValue all_arr = JS_NewArray(ctx);
    uint32_t fn_idx = 0;
    uint32_t all_idx = 0;

    for (uint32_t i = 0; i < plen; i++) {
        JSValue key_str = JS_AtomToString(ctx, ptab[i].atom);
        JSValue prop_val = JS_GetProperty(ctx, ns, ptab[i].atom);
        if (!JS_IsException(key_str)) {
            JS_SetPropertyUint32(ctx, all_arr, all_idx++, JS_DupValue(ctx, key_str));
            if (!JS_IsException(prop_val) && JS_IsFunction(ctx, prop_val)) {
                JS_SetPropertyUint32(ctx, fn_arr, fn_idx++, JS_DupValue(ctx, key_str));
            }
        }
        JS_FreeValue(ctx, prop_val);
        JS_FreeValue(ctx, key_str);
    }
    JS_FreePropertyEnum(ctx, ptab, plen);
    JS_FreeValue(ctx, ns);

    JSValue meta_obj = JS_NewObject(ctx);
    JS_SetPropertyStr(ctx, meta_obj, "functions", fn_arr);
    JS_SetPropertyStr(ctx, meta_obj, "all", all_arr);

    JSValue exports_json = JS_JSONStringify(ctx, meta_obj, JS_UNDEFINED, JS_UNDEFINED);
    JS_FreeValue(ctx, meta_obj);
    if (!JS_IsException(exports_json)) {
        size_t exp_len = 0;
        const char *exp_cstr = JS_ToCStringLen(ctx, &exp_len, exports_json);
        if (exp_cstr) {
            alloc_copy_bytes(exp_cstr, exp_len, out_exports_json_utf8, out_exports_json_len);
            JS_FreeCString(ctx, exp_cstr);
        }
    }
    JS_FreeValue(ctx, exports_json);

    JS_FreeContext(ctx);
    JS_FreeRuntime(rt);
    return DESKBOX_QJS_OK;
}

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
    size_t *out_error_len)
{
    if (!out_result_json_utf8 || !out_result_json_len || !out_error_utf8 || !out_error_len) {
        return DESKBOX_QJS_INVALID_ARGUMENT;
    }

    *out_result_json_utf8 = NULL;
    *out_result_json_len = 0;
    *out_error_utf8 = NULL;
    *out_error_len = 0;

    if (!bytecode || bytecode_len == 0 || !fn_name_utf8 || fn_name_utf8[0] == '\0') {
        set_error_text("Invalid bytecode or function name.", out_error_utf8, out_error_len);
        return DESKBOX_QJS_INVALID_ARGUMENT;
    }

    DeskBoxQjsGuard guard;
    JSRuntime *rt = create_sandboxed_runtime(&guard, timeout_ms > 0 ? timeout_ms : 50, memory_limit_bytes);
    if (!rt) {
        set_error_text("Failed to create QuickJS runtime.", out_error_utf8, out_error_len);
        return DESKBOX_QJS_OOM;
    }

    JSContext *ctx = JS_NewContext(rt);
    if (!ctx) {
        JS_FreeRuntime(rt);
        set_error_text("Failed to create QuickJS context.", out_error_utf8, out_error_len);
        return DESKBOX_QJS_OOM;
    }

    /* 1. Compile pristine host bootstrap closure before user code can touch prototypes. */
    JSValue prepare_fn = JS_Eval(
        ctx,
        DESKBOX_BOOTSTRAP_SCRIPT,
        sizeof(DESKBOX_BOOTSTRAP_SCRIPT) - 1,
        "<deskbox-bootstrap>",
        JS_EVAL_TYPE_GLOBAL);
    if (JS_IsException(prepare_fn)) {
        int32_t code = capture_exception_and_classify(ctx, &guard, out_error_utf8, out_error_len);
        JS_FreeContext(ctx);
        JS_FreeRuntime(rt);
        return code;
    }

    /* 2. Parse input payload JSON before user module runs. */
    JSValue payload_val;
    if (payload_json_utf8 && payload_json_len > 0) {
        char *nul_json = (char *)malloc(payload_json_len + 1);
        if (!nul_json) {
            JS_FreeValue(ctx, prepare_fn);
            JS_FreeContext(ctx);
            JS_FreeRuntime(rt);
            set_error_text("Host out of memory copying input payload.", out_error_utf8, out_error_len);
            return DESKBOX_QJS_OOM;
        }
        memcpy(nul_json, payload_json_utf8, payload_json_len);
        nul_json[payload_json_len] = '\0';
        payload_val = JS_ParseJSON(ctx, nul_json, payload_json_len, "<payload>");
        free(nul_json);
        if (JS_IsException(payload_val)) {
            JS_FreeValue(ctx, prepare_fn);
            int32_t code = capture_exception_and_classify(ctx, &guard, out_error_utf8, out_error_len);
            JS_FreeContext(ctx);
            JS_FreeRuntime(rt);
            return code;
        }
    } else {
        payload_val = JS_NewObject(ctx);
    }

    /* 3. Deserialize and evaluate precompiled module bytecode. */
    JSValue mod_val = JS_ReadObject(ctx, bytecode, bytecode_len, JS_READ_OBJ_BYTECODE);
    if (JS_IsException(mod_val)) {
        JS_FreeValue(ctx, payload_val);
        JS_FreeValue(ctx, prepare_fn);
        int32_t code = capture_exception_and_classify(ctx, &guard, out_error_utf8, out_error_len);
        JS_FreeContext(ctx);
        JS_FreeRuntime(rt);
        return code;
    }

    if (!JS_IsModule(mod_val)) {
        JS_FreeValue(ctx, mod_val);
        JS_FreeValue(ctx, payload_val);
        JS_FreeValue(ctx, prepare_fn);
        JS_FreeContext(ctx);
        JS_FreeRuntime(rt);
        set_error_text("Bytecode buffer does not contain an ES module.", out_error_utf8, out_error_len);
        return DESKBOX_QJS_INVALID_ARGUMENT;
    }

    if (JS_ResolveModule(ctx, mod_val) < 0) {
        JS_FreeValue(ctx, mod_val);
        JS_FreeValue(ctx, payload_val);
        JS_FreeValue(ctx, prepare_fn);
        int32_t code = capture_exception_and_classify(ctx, &guard, out_error_utf8, out_error_len);
        JS_FreeContext(ctx);
        JS_FreeRuntime(rt);
        return code;
    }

    JSModuleDef *m = (JSModuleDef *)JS_VALUE_GET_PTR(mod_val);
    JSValue mod_eval = JS_EvalFunction(ctx, mod_val); /* consumes mod_val */
    if (JS_IsException(mod_eval)) {
        JS_FreeValue(ctx, payload_val);
        JS_FreeValue(ctx, prepare_fn);
        int32_t code = capture_exception_and_classify(ctx, &guard, out_error_utf8, out_error_len);
        JS_FreeContext(ctx);
        JS_FreeRuntime(rt);
        return code;
    }

    if (drain_microtasks(rt) < 0 || check_promise_rejection(ctx, mod_eval) < 0) {
        JS_FreeValue(ctx, mod_eval);
        JS_FreeValue(ctx, payload_val);
        JS_FreeValue(ctx, prepare_fn);
        int32_t code = capture_exception_and_classify(ctx, &guard, out_error_utf8, out_error_len);
        JS_FreeContext(ctx);
        JS_FreeRuntime(rt);
        return code;
    }
    JS_FreeValue(ctx, mod_eval);

    /* 4. Lookup target export in module namespace. */
    JSValue ns = JS_GetModuleNamespace(ctx, m);
    if (JS_IsException(ns)) {
        JS_FreeValue(ctx, payload_val);
        JS_FreeValue(ctx, prepare_fn);
        int32_t code = capture_exception_and_classify(ctx, &guard, out_error_utf8, out_error_len);
        JS_FreeContext(ctx);
        JS_FreeRuntime(rt);
        return code;
    }

    JSValue target_fn = JS_GetPropertyStr(ctx, ns, fn_name_utf8);
    JS_FreeValue(ctx, ns);
    if (JS_IsException(target_fn)) {
        JS_FreeValue(ctx, payload_val);
        JS_FreeValue(ctx, prepare_fn);
        int32_t code = capture_exception_and_classify(ctx, &guard, out_error_utf8, out_error_len);
        JS_FreeContext(ctx);
        JS_FreeRuntime(rt);
        return code;
    }

    if (JS_IsUndefined(target_fn)) {
        JS_FreeValue(ctx, target_fn);
        JS_FreeValue(ctx, payload_val);
        JS_FreeValue(ctx, prepare_fn);
        JS_FreeContext(ctx);
        JS_FreeRuntime(rt);
        if (require_export) {
            char err_buf[256];
            snprintf(err_buf, sizeof(err_buf), "Exported function '%s' was not found in logic.js.", fn_name_utf8);
            set_error_text(err_buf, out_error_utf8, out_error_len);
            return DESKBOX_QJS_EXPORT_NOT_FOUND;
        }
        static const char NOT_INVOKED_JSON[] = "{\"invoked\":false}";
        alloc_copy_bytes(NOT_INVOKED_JSON, sizeof(NOT_INVOKED_JSON) - 1, out_result_json_utf8, out_result_json_len);
        return DESKBOX_QJS_OK;
    }

    if (!JS_IsFunction(ctx, target_fn)) {
        JS_FreeValue(ctx, target_fn);
        JS_FreeValue(ctx, payload_val);
        JS_FreeValue(ctx, prepare_fn);
        JS_FreeContext(ctx);
        JS_FreeRuntime(rt);
        char err_buf[256];
        snprintf(err_buf, sizeof(err_buf), "Export '%s' in logic.js is not a function.", fn_name_utf8);
        set_error_text(err_buf, out_error_utf8, out_error_len);
        return DESKBOX_QJS_ERROR;
    }

    /* 5. Invoke target export via pristine bootstrap session. */
    JSValue fn_name_val = JS_NewString(ctx, fn_name_utf8);
    JSValue args[3] = { target_fn, payload_val, fn_name_val };
    JSValue session_obj = JS_Call(ctx, prepare_fn, JS_UNDEFINED, 3, args);
    JS_FreeValue(ctx, fn_name_val);
    JS_FreeValue(ctx, payload_val);
    JS_FreeValue(ctx, target_fn);
    JS_FreeValue(ctx, prepare_fn);

    if (JS_IsException(session_obj)) {
        int32_t code = capture_exception_and_classify(ctx, &guard, out_error_utf8, out_error_len);
        JS_FreeContext(ctx);
        JS_FreeRuntime(rt);
        return code;
    }

    JSValue call_res = JS_GetPropertyStr(ctx, session_obj, "callResult");
    if (drain_microtasks(rt) < 0 || check_promise_rejection(ctx, call_res) < 0) {
        JS_FreeValue(ctx, call_res);
        JS_FreeValue(ctx, session_obj);
        int32_t code = capture_exception_and_classify(ctx, &guard, out_error_utf8, out_error_len);
        JS_FreeContext(ctx);
        JS_FreeRuntime(rt);
        return code;
    }
    JS_FreeValue(ctx, call_res);

    JSValue finalize_fn = JS_GetPropertyStr(ctx, session_obj, "finalize");
    JSValue delta_obj = JS_Call(ctx, finalize_fn, JS_UNDEFINED, 0, NULL);
    JS_FreeValue(ctx, finalize_fn);
    JS_FreeValue(ctx, session_obj);

    if (JS_IsException(delta_obj)) {
        int32_t code = capture_exception_and_classify(ctx, &guard, out_error_utf8, out_error_len);
        JS_FreeContext(ctx);
        JS_FreeRuntime(rt);
        return code;
    }

    /* 6. Serialize output envelope with C-level JS_JSONStringify. */
    JSValue json_str = JS_JSONStringify(ctx, delta_obj, JS_UNDEFINED, JS_UNDEFINED);
    JS_FreeValue(ctx, delta_obj);
    if (JS_IsException(json_str)) {
        int32_t code = capture_exception_and_classify(ctx, &guard, out_error_utf8, out_error_len);
        JS_FreeContext(ctx);
        JS_FreeRuntime(rt);
        return code;
    }

    size_t res_len = 0;
    const char *res_cstr = JS_ToCStringLen(ctx, &res_len, json_str);
    if (!res_cstr) {
        JS_FreeValue(ctx, json_str);
        int32_t code = capture_exception_and_classify(ctx, &guard, out_error_utf8, out_error_len);
        JS_FreeContext(ctx);
        JS_FreeRuntime(rt);
        return code;
    }

    if (res_len > DESKBOX_QJS_MAX_RESULT_JSON_BYTES) {
        JS_FreeCString(ctx, res_cstr);
        JS_FreeValue(ctx, json_str);
        JS_FreeContext(ctx);
        JS_FreeRuntime(rt);
        set_error_text(
            "RangeError: Script output delta exceeded 512 KB maximum size.",
            out_error_utf8,
            out_error_len);
        return DESKBOX_QJS_OOM;
    }

    int ok = alloc_copy_bytes(res_cstr, res_len, out_result_json_utf8, out_result_json_len);
    JS_FreeCString(ctx, res_cstr);
    JS_FreeValue(ctx, json_str);
    JS_FreeContext(ctx);
    JS_FreeRuntime(rt);

    if (!ok) {
        set_error_text("Host out of memory copying script output.", out_error_utf8, out_error_len);
        return DESKBOX_QJS_OOM;
    }

    return DESKBOX_QJS_OK;
}
