# DeskBox native workspace

This workspace contains narrowly scoped native modules used by DeskBox.

## Current stage

The production `deskbox-native` module uses ABI 2, capability mask
`1023`, and eleven exports. Supported x64 Native AOT builds use it for shortcut,
Explorer-hosted launch, Quick Access, music-volume, and exact Recycle Bin
recovery boundaries. Ordinary JIT runs keep the established C# implementations
as their default oracle. Both JIT and AOT builds include the module for plugin
signature verification using `ed25519-dalek` 2.2.0 `verify_strict`.

The workspace also contains `deskbox-audio-session-fixture`, a test-only binary
used by the Stage 5B-3C smoke script. It loops a generated all-zero PCM WAV to
create a controlled Core Audio session, is bound to the parent script lifetime,
and is never copied into the application or AOT publish output. It is not part
of the production ABI, capability mask, or export list.

## Contract

- Production module ABI version: `2`
- Capability mask: `1023` (`STORED_RAW`,
  `EFFECTIVE_DIAGNOSTIC`, `RESOLVE_NO_UI`, `WRITE`, `RESOLVE_WITH_UI`, and
  `MUSIC_VOLUME_V1`, `EXPLORER_SHELL_LAUNCH_V1`, `QUICK_ACCESS_V1`, and
  `RECYCLE_BIN_V1`, `PLUGIN_SIGNATURE_V1`)
- Targets: `x86_64-pc-windows-msvc` and `aarch64-pc-windows-msvc`
- Library type: `cdylib`
- Public header: `include/deskbox_native.h`
- ARM64 Stage 7A report: `../docs/architecture/rust-stage-7a-arm64-static-report.md`
- Detailed shortcut contract: `../docs/architecture/shortcut-native-abi-v2.md`
- Detailed music-volume contract: `../docs/architecture/music-volume-native-abi-v1.md`
- Detailed Explorer-launch contract: `../docs/architecture/explorer-shell-launch-native-abi-v1.md`
- Detailed Quick Access contract: `../docs/architecture/quick-access-native-abi-v1.md`
- Detailed Recycle Bin contract: `../docs/architecture/recycle-bin-native-abi-v1.md`
- Panic policy: abort; panic must never cross the C ABI
- COM and Shell bindings: official `windows` crate `0.62.2`, with only the
  required Win32 feature groups enabled
- Cargo dependencies and the compiler toolchain are locked by `Cargo.lock`
  and `rust-toolchain.toml`

The C ABI uses fixed-width values, explicit UTF-16 lengths, caller-owned output
buffers, versioned structures, and reserved fields. Rust-owned strings, Rust
object pointers, COM pointers, and exceptions must not cross the boundary.

The required exports are:

- `deskbox_native_abi_version`
- `deskbox_native_capabilities`
- `deskbox_shortcut_read_v2`
- `deskbox_shortcut_resolve_no_ui_v2`
- `deskbox_shortcut_write_v2`
- `deskbox_shortcut_resolve_with_ui_v2`
- `deskbox_music_volume_v1`
- `deskbox_explorer_shell_launch_v1`
- `deskbox_quick_access_v1`
- `deskbox_recycle_bin_v1`
- `deskbox_plugin_verify_ed25519_v1`

The presence of an export does not by itself mean the operation is implemented.
Callers check ABI version, all required exports, and the operation capability
before every operation class. The tenth capability adds strict Ed25519 verification
without changing the existing nine operation contracts. Its inputs are a 64-byte
signature, a message of at most 1 MiB, and a 32-byte public key. A caller-owned
`uint32_t` receives 0 or 1; no keys or buffers are retained. Invalid arguments
return `INVALID_ARGUMENT`; a rejected signature returns `OK` with result 0.

The read implementation is synchronous and stateless. Each call initializes or
reuses COM on the calling thread, creates and releases its own Shell Link
object, and never stores caller buffers or COM interfaces. `STORED_RAW` keeps
the existing 260-character raw metadata behavior. `EFFECTIVE_DIAGNOSTIC` reads
only target and arguments, trims them with the .NET whitespace set, and keeps
the existing 260/512-character source capacities.

`RESOLVE_NO_UI` loads the shortcut, calls `Resolve` with no owner window and
`SLR_NO_UI | SLR_NOSEARCH`, records the raw HRESULT, and then reads stored
metadata even when Resolve returns `S_FALSE` or a failure HRESULT. A zero
timeout preserves the Windows default; values from 1 through 65535 are encoded
in the high word of the resolve flags.

`WRITE` creates a fresh Shell Link object, applies target, description,
arguments, working directory, and icon path/index in order, then saves with
`IPersistFile::Save(..., TRUE)`. Empty optional fields are applied explicitly so
overwriting an existing link cannot retain stale metadata. Parent-directory
creation, path normalization, and application cache invalidation remain in C#.

`RESOLVE_WITH_UI` synchronously loads the shortcut and calls `Resolve` on the
caller's thread with the supplied owner HWND and exactly
`SLR_UPDATE | SLR_NOSEARCH | SLR_OFFER_DELETE_WITHOUT_FILE`. It intentionally
does not set `SLR_NO_UI`. Rust records the raw COM, create, load, and resolve
HRESULT values but does not retain the HWND or COM object. C# owns file-cache
invalidation and maps the post-call existence of the `.lnk` to the existing
kept/deleted product result.

`MUSIC_VOLUME_V1` resolves the default render/multimedia endpoint on every
call. It reads or sets system master volume and enumerates application sessions
using the legacy DeskBox matching order without retaining device, session, or
callback state. Task-allocated Core Audio strings and COM interfaces are
released before the call returns.

`EXPLORER_SHELL_LAUNCH_V1` locates the desktop object owned by the running
Explorer process and invokes its `IShellDispatch2::ShellExecute`. It uses the
typed `IShellDispatch` → `IShellWindows` → `IWebBrowser` →
`IShellFolderViewDual` chain, records seven phase HRESULTs, and releases all
COM interfaces and automation values before returning. The operation does not
replace DeskBox's existing local `Process.Start` and `SHOpenWithDialog`
fallbacks.

`QUICK_ACCESS_V1` queries `System.IsPinnedToNameSpaceTree`, invokes
`pintohome`, or invokes `unpinfromhome` through typed Shell Automation
interfaces. Query and unpin enumerate the Quick Access namespace and compare
normalized paths case-insensitively. Unpin preserves the existing idempotent
managed pre-check and falls back to parent-folder `ParseName` when the pinned
item is not present in the namespace. The operation records ten phase HRESULTs
and never retains a Shell object, collection, item, `BSTR`, or `VARIANT`.

For JIT diagnostics, build with `DeskBoxRustNative=true` and launch with
`DESKBOX_SHORTCUT_BACKEND=rust`. Missing DLL, export, ABI, or capability errors
are logged and do not fall back to C#. Native AOT defines
`DESKBOX_NATIVE_AOT`, excludes the legacy shortcut COM code, and always selects
Rust. Both architecture audit scripts pass `DeskBoxRustNative=true`.
The current MSBuild guard accepts only complete x64/win-x64 or ARM64/win-arm64
pairs, and every Native AOT build requires `DeskBoxRustNative=true`.
The Rust property defaults to true; explicitly disabling it for an AOT publish
fails before compilation. Diagnostic bundle capture does not initialize the lazy
native loader and records no absolute module path. Stage 7A cross-publishes ARM64
without executing target code on the x64 host; real ARM64 runtime evidence is a
separate Stage 7B gate.

Music volume has a separate `DESKBOX_MUSIC_VOLUME_BACKEND=rust` JIT opt-in.
Native AOT selects Rust at compile time and excludes the legacy Core Audio
`ComImport` declarations. Its failure path likewise never falls back to C#.

Explorer-hosted launch has a separate
`DESKBOX_EXPLORER_SHELL_BACKEND=rust` JIT opt-in. Native AOT excludes the C#
dynamic oracle and always selects Rust. A Rust boundary failure is returned to
the unchanged product layer, which may then use its local ShellExecute/Open
With fallbacks; it never silently executes the C# oracle.

Quick Access has a separate `DESKBOX_QUICK_ACCESS_BACKEND=rust` JIT opt-in.
The public synchronous APIs and dedicated-background-STA asynchronous wrapper
are unchanged. Native AOT excludes the C# ProgID/dynamic oracle and always
selects Rust. Automated tests probe the capability and export and perform only
read-only state queries. Stage 5B-2B's isolated AOT smoke additionally covers
pin, unpin, in-process compensation, and independent compensation without using
the production data root.

## Validation

From the repository root:

```powershell
cargo fmt --manifest-path .\native\Cargo.toml --all -- --check
cargo clippy --manifest-path .\native\Cargo.toml --workspace --all-targets --target x86_64-pc-windows-msvc --locked -- -D warnings
cargo test --manifest-path .\native\Cargo.toml --workspace --target x86_64-pc-windows-msvc --locked
.\scripts\build-rust-native.ps1 -Platform x64 -Configuration Release -OutputDirectory .\.artifacts\rust-native-check
.\scripts\build-rust-search-core.ps1 -Platform x64 -Configuration Release -OutputDirectory .\.artifacts\rust-search-core-check
.\scripts\build-rust-native.ps1 -Platform ARM64 -Configuration Release -OutputDirectory .\.artifacts\rust-native-arm64-check
.\scripts\build-rust-search-core.ps1 -Platform ARM64 -Configuration Release -OutputDirectory .\.artifacts\rust-search-core-arm64-check
.\scripts\publish-aot-audit.ps1 -Platform x64
.\scripts\publish-arm64-aot-static-audit.ps1
```

The ARM64 script proves cross-compiled PE, exports, imports, hashes, symbols, and
distribution structure only. Run the Stage 7B matrix on a real ARM64 Windows
device before treating it as runtime-compatible or changing the product default.

The Stage 3C-2 missing-target Shell dialog gate is complete. A Rust-enabled JIT
build was used to verify the actual loaded module path plus owner-window,
cancel, repair, and delete behavior; the record is described in
`../docs/architecture/shortcut-native-abi-v2.md`. Stage 3C-3 also verified the
compile-time AOT exclusion, side-effect-free diagnostics, unique x64 publish
DLL, ARM64 exclusion, and detached updater boundary. A fresh ordinary JIT
instance was then verified to load no `deskbox_native.dll` at startup. The
DeskBox AOT executable was not launched because other application-level AOT
blockers remain.

Stage 3C-3-R closes the release-contract gap found after 3C-3. Real MSBuild
allow/reject combinations cover ordinary JIT, complete x64 AOT, missing Rust,
ARM64, and mismatched Platform/RID inputs. The audit script now has only a
supported x64 execution path and always enables the Rust module there.

Stage 4C extends the module with the music-volume export after FolderPicker and
JSON source-generation work completed. Audit profile 14 / schema 11 confirms
that both shortcut and music-volume always-throw sets are empty, with ABI 2,
capability mask 63, seven required exports, and matching staging/publish DLL
hashes. The generated AOT application is still not launched in this stage.

Stage 4D-4A adds the Explorer-hosted launch export without changing the prior
shortcut or music-volume contracts. Audit profile 20 / schema 17 confirms ABI
2, capability mask 127, eight required exports, matching staging/publish DLL
hashes, zero target-file warnings, and zero Explorer/complete always-throw
messages. The generated AOT application is still not launched; the explicit
Rust JIT file/folder/URL and failure-fallback matrix remains a manual gate.

Stage 4D-4B adds the independent Quick Access export without changing the
shortcut, music-volume, or Explorer-launch structures. Audit profile 21 /
schema 18 confirms ABI 2, capability mask 255, nine required exports,
matching staging/publish hashes, zero Quick Access target warnings, and zero
Quick Access/complete always-throw messages. The read-only real-system probe
completed through all seven query phases; automated and audit workflows did
not invoke pin or unpin. The generated AOT application is still not launched.

Stage 4D-5 is managed-only. It replaces tray identity and private-flyout
reflection with public H.NotifyIcon contracts and the WinUI visual tree; no
Rust source, ABI structure, capability, export, or backend policy changes.
Audit profile 22 / schema 19 confirms ABI 2, capability mask 255, nine required
exports, matching staging/publish hashes, zero Stage 4D-5 source warnings, and
zero IL2026/IL2050/IL2072/IL2075/IL3050 or complete always-throw messages. The
generated AOT application is still not launched.

Stage 4E-0 is also managed-only. It changes six immutable search-history
bindings from OneWay to OneTime and closes WMC1506 without changing any native
source, ABI structure, capability, export, or backend policy. Audit profile 23 /
schema 20 confirms ABI 2, capability mask 255, nine required exports, matching
staging/publish hashes, zero WMC1506 and Stage 4E-0 source warnings, and zero
complete always-throw messages. WMC1510 remains at 1265. The generated AOT
application is still not launched.

Stage 4E-1 is managed-only. It converts seven bindings across four leaf XAML
controls to typed OneWay x:Bind without changing any native source, ABI
structure, capability, export, or backend policy. Audit profile 24 / schema 21
confirms ABI 2, capability mask 255, nine required exports, matching
staging/publish hashes, zero Stage 4E-1 target-XAML warnings, and zero complete
always-throw messages. WMC1506 remains zero and WMC1510 decreases from 1265 to
1258. The generated AOT application is still not launched.

Stage 4E-2 is managed-only. It converts fifteen bindings across
MusicTransportIcon and WidgetInlineEditor to typed x:Bind, including an
immediate TwoWay text binding, without changing any native source, ABI
structure, capability, export, or backend policy. Audit profile 25 / schema 22
confirms ABI 2, capability mask 255, nine required exports, matching
staging/publish hashes, zero Stage 4E-2 target-XAML warnings, and zero complete
always-throw messages. WMC1506 remains zero and WMC1510 decreases from 1258 to
1243. The generated AOT application is still not launched.

Stage 4E-3 is managed-only. It converts fourteen bindings across the typed
AttachmentTileStrip and SearchPopupWindow data templates. Attachment updates
remain OneWay through INotifyPropertyChanged; the search tab Count remains
OneWay while six immutable or explicitly refreshed search values use OneTime.
No native source, ABI structure, capability, export, or backend policy changes.
Audit profile 26 / schema 23 confirms ABI 2, capability mask 255, nine required
exports, matching staging/publish hashes, zero Stage 4E-3 target-XAML warnings,
and zero complete always-throw messages. WMC1506 remains zero and WMC1510
decreases from 1243 to 1229. The generated AOT application is still not
launched.

Stage 4E-4 is managed-only. It adds an explicit SettingsViewModel dependency-
property bridge to FileWidgetSettingsSection and converts three OneWay plus two
TwoWay bindings to compiled x:Bind. Generated code tracks the root dependency
property, nested PropertyChanged events, and both attached ValueProperty target-
to-source callbacks without a manual Bindings.Update hook. No native source,
ABI structure, capability, export, or backend policy changes. Audit profile 27 /
schema 24 confirms ABI 2, capability mask 255, nine required exports, matching
staging/publish hashes, zero Stage 4E-4 target-source warnings, and zero complete
always-throw messages. WMC1506 remains zero and WMC1510 decreases from 1229 to
1224. The generated AOT application is still not launched.

Stage 4E-5 is managed-only. It converts all eight SearchResultRowControl runtime
bindings to OneTime compiled x:Bind. A public typed Item dependency property was
rejected by the real XAML compiler because it generated an invalid activator for
SearchResultItem's required members. The final control therefore keeps an
internal typed Item projection and refreshes generated bindings on every
ItemsRepeater ElementPrepared event, while the existing explicit Icon/Size/Date
refresh and recycled-row reference check remain intact. No native source, ABI
structure, capability, export, or backend policy changes. Audit profile 28 /
schema 25 confirms ABI 2, capability mask 255, nine required exports, matching
staging/publish hashes, zero Stage 4E-5 target-source warnings, and zero complete
always-throw messages. WMC1506 remains zero and WMC1510 decreases from 1224 to
1216. The generated AOT application is still not launched; Stage 5A is the next
open batch.

Stage 5B-3C uses audit profile 35 / schema 32 and a controlled test-only audio
session fixture. The final x64 AOT matrix proves process/display-name match kind
4, product session read/write `1.0 -> 0.92 -> 1.0`, application-finally recovery,
forced-termination recovery by an independent AOT process, preservation of the
recovery intent when the session disappears, and unchanged system master
volume. The Rust workspace passes 54 tests: 52 production-module tests and 2
fixture tests. The production module remains ABI 2, capability mask 255, and
nine required exports; only the test workspace membership changed. Full details
are in `../docs/architecture/aot-stage-5b-3c-report.md`.

Stage 5B-4C1B1 adds the exact Recycle Bin query/recovery export without changing
the File Widget's existing C# `SHFileOperationW` delete path. The Rust operation
fully enumerates the Shell Recycle Bin namespace, compares original parent and
name, and invokes `undelete` only after proving exactly one match. Audit profile
47 / schema 44 confirms ABI 2, capability mask 511, ten required exports,
matching staging/publish hashes, zero target warnings, and zero complete
always-throw messages. The three-process x64 AOT matrix restores all three owned
items and leaves zero exact matches. Full details are in
`../docs/architecture/aot-stage-5b-4c1b1-report.md`.

Stage 7A adds the pinned ARM64 Rust standard library, an audited x64-hosted MSVC
ARM64 environment, case-sensitive static PE/export parsing, exact Platform/RID
guards, and a separate cross-compiled static AOT audit. `DeskBox.exe`,
`DeskBox.Updater.exe`, and `deskbox_native.dll` all report machine `0xAA64`;
staging/publish DLL hashes match and symbols are separated. The evidence does
not execute target code. Full results and the Stage 7B boundary are in
`../docs/architecture/rust-stage-7a-arm64-static-report.md`.
