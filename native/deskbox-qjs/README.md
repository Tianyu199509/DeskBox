# DeskBox Sandboxed QuickJS-NG Module (`deskbox_qjs.dll`)

Provides the hermetic, zero-idle-memory ES2023 script engine for Custom Widget private `logic.js` modules.

## Upstream Engine
- **Project**: [QuickJS-NG](https://github.com/quickjs-ng/quickjs)
- **Version**: `v0.18.0` (`quickjs-amalgam.zip`, SHA-256 `6C05066A6B62B483E2BA0F83D7A16688C6958182B214BFD693529434CEF84A9E`)
- **License**: MIT (`vendor/LICENSE`)

## Security & Resource Model
- **Airgapped intrinsics**: Only pure ECMAScript standard objects (`JS_NewContext`) are instantiated. No `std`, `os`, filesystem, process, socket, or network globals exist, and external ES module `import` statements are rejected at compile and link time.
- **CPU deadline interrupt**: Hardware `QueryPerformanceCounter` interrupt handler terminates runaway loops/recursion (`50ms` default action/tick budget; `100ms` cold-start/source-parse budget).
- **Memory & stack caps**: `JS_SetMemoryLimit` (`4 MB` default) and `JS_SetMaxStackSize` (`256 KB`).
- **Ephemeral execution**: `logic.js` is precompiled to QuickJS bytecode (`byte[]`) and executed inside a disposable runtime/context per event, releasing 100% of JS heap immediately upon return.
