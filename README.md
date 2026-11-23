<div align="center">

<img src="docs/images/brand/logo-200.png" alt="DeskBox" width="80" />

# DeskBox

**An open-source, local-first Windows 10/11 desktop organizer with native-feeling WinUI 3 widgets and a declarative custom widget ecosystem.**

English | [简体中文](README.zh-CN.md)

[Download](#download) · [Features](#features) · [Visual Experience](#visual-experience) · [Technical Overview](#technical-overview) · [Build from Source](#build-from-source) · [Licensing](LICENSING.md)

[![CI](https://github.com/Tianyu199509/DeskBox/actions/workflows/ci.yml/badge.svg)](https://github.com/Tianyu199509/DeskBox/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/Tianyu199509/DeskBox?label=release&color=2563EB)](https://github.com/Tianyu199509/DeskBox/releases/latest)
[![Windows 10/11](https://img.shields.io/badge/Windows-10%2F11-0078D4.svg)](#technical-overview)
[![x64 and ARM64](https://img.shields.io/badge/architecture-x64%20%7C%20ARM64-5C2D91.svg)](#download)
[![License: GPL v3 | Commercial](https://img.shields.io/badge/license-GPLv3%20%7C%20Commercial-blue.svg)](LICENSING.md)
[![GitHub stars](https://img.shields.io/github/stars/Tianyu199509/DeskBox?style=flat&color=yellow)](https://github.com/Tianyu199509/DeskBox/stargazers)
[![Downloads](https://img.shields.io/github/downloads/Tianyu199509/DeskBox/total?style=flat&color=brightgreen)](https://github.com/Tianyu199509/DeskBox/releases)
<a href="https://hellogithub.com/repository/Tianyu199509/DeskBox" target="_blank"><img src="https://api.hellogithub.com/v1/widgets/recommend.svg?rid=f0cae3cb81f3496b9b6ead91194dc6f8&claim_uid=x4er8iQsXYT3aMN&theme=small" alt="Featured｜HelloGitHub" /></a>

</div>

> **Contribution status:** External pull requests are temporarily paused while core architecture and extensibility boundaries settle. Bug reports, feature ideas, and technical discussions are warmly welcomed via [GitHub Issues](https://github.com/Tianyu199509/DeskBox/issues) and Discussions — see [CONTRIBUTING.md](CONTRIBUTING.md).

![DeskBox Windows desktop organizer with file, todo, clipboard, weather, music, and custom widgets](docs/images/brand/readme-hero-1-3-7-dark-en.png)

Built on C#, WinUI 3, .NET 10 Native AOT, and a Rust native Shell layer, DeskBox adds a lightweight widget layer over the Windows desktop without replacing Explorer or moving your files into a proprietary database. Real-folder-backed file widgets make it a modern open-source alternative to tools such as Stardock Fences, complemented by clipboard history, a global search popup, todos, quick notes, Glance, weather, media controls, and programmable custom desktop widgets.

---

## Download

| Channel | Package & Highlights | Link |
| --- | --- | --- |
| **GitHub Releases (Direct)** | Offline `x64` & `ARM64` Full Native AOT installers bundling a private Windows App Runtime 2.7, silent in-app updates, and companion-process UI isolation | [Latest GitHub Releases](https://github.com/Tianyu199509/DeskBox/releases/latest) |
| **Official Website** | Product overview, direct installer downloads, and documentation | [deskbox.fun](https://deskbox.fun) |
| **Microsoft Store** | Store-signed MSIX package with Store updates — ideal for supporting ongoing maintenance | [Get it from Microsoft Store](https://apps.microsoft.com/store/detail/9PBZSNB4D69H) |

- **Integrity verification**: Every GitHub Release installer ships alongside a matching `.sha256` sidecar file so you can verify package integrity before running Setup; see the [code signing and verification policy](docs/code-signing.md).
- **System requirements**: **Windows 10 version 2004 (build 19041) or later** and **Windows 11 (22H2+ recommended)** on `x64` or native `ARM64` PCs. Supports standard `Program Files` or unelevated per-user installation and works fully offline.
- **Release history**: See [CHANGELOG.md](CHANGELOG.md) for the complete version-by-version history.

---

## Features

### 1. File Widgets & Desktop Organization

- **Real-folder storage & mapping**: Create managed storage widgets backed by ordinary folders, or map existing desktop and disk folders in place without moving them. Migrating files into managed storage uses a copy-first, verify-before-commit workflow that never auto-deletes source files.
- **Windows 11-native file interactions**: Single-column right-click menus with inline icons, Explorer-ordered items, real "Open with" app associations (including UWP), blank-area "New / View / Sort by" submenus, native ZIP compress/extract, and Spacebar [QuickLook](https://github.com/QL-Win/QuickLook) preview.
- **Multi-source drag-and-drop & stacks**: Drag files and links seamlessly to and from Explorer, WeChat, and browsers (including downloading remote URLs), reorder tiles manually, expand nested folder stacks inside widgets or Adaptive / 3×3 / 5×5 popovers, and drag files out safely even from elevated sessions.
- **Widget groups & Desktop Organization workbench**: Combine any desktop widgets into tabbed groups (connected tabs or equal-width tabs with `Ctrl+Tab`, wheel switching, and acrylic merge/detach previews); preview rule-based routing before moving desktop files, enable automatic background filing for newly settled items, and review or undo organization history.

### 2. Everyday Productivity & Desktop Widgets

- **Unified Global Search popup (`Alt+D`)**: Connects over local IPC to an existing Everything installation (not bundled) and merges file results with DeskBox notes, todos, clipboard history, apps, and settings in one window — featuring an instant calculator, command palette, one-keystroke web fallback, and Recommendation Edit Mode (app folders & multi-select drag-to-reorder).
- **Clipboard History & Quick Paste (`Alt+V`)**: Captures text, HTML/RTF, images, files, and color swatches with instant search, a keyboard-first `Alt+V` Quick Paste popup, offline image OCR text indexing, built-in text transforms, optional webpage URL title fetching, per-app exclusions, externalized large entries, and automatic local DPAPI encryption and UI masking for sensitive secrets.
- **Responsive Todo & Quick Capture**: Adaptive single- and dual-pane layouts, natural-language date/time/recurrence input, full-text search, overdue reschedule bar, bidirectional Todo ↔ QuickCapture conversion, task reminders, color markers, a popout pinned note window, multi-note merge, and live Markdown folder mirroring with linked or managed attachments.
- **Glance, Weather & Music**: Glance displays date, weekday, lunar calendar, solar terms, and custom rotating backgrounds; Weather pairs MSN Weather with Open-Meteo fallback across four bundled icon packs; Music controls Windows media sessions with album-color ambience, audio spectrum visualization, and LRCLIB / local `.lrc` synced lyrics.

### 3. Custom Widgets & Widget Studio

- **Zero-WebView declarative engine**: Renders native WinUI 3 desktop widgets from a flat element graph (`manifest.json`) without permanent WebView memory overhead.
- **Air-gapped QuickJS script sandbox & permission gates**: Interactive custom widgets run `logic.js` inside an isolated QuickJS sandbox enforced by strict instruction-count, wall-clock, and memory quotas, backed by per-widget capability permissions (Internet, LAN/localhost, clipboard/system, local files, command execution).
- **Three authoring channels & Agent integration**: Create or edit widgets via **Direct API (BYOK)**, **External Coding Agents** (`--mcp` stdio bridge & CLI skills for Claude Code, Cursor, Codex, Windsurf, etc.), or **zero-setup Web AI prompt handoff** — alongside 14 localized official templates, ECDSA P-256 signed remote catalog/prompt updates, `.dbxwidget` import/export, `deskbox://` deep links, and mandatory **host approval / stage-and-preview confirmation** before applying external changes.

### 4. Capsule Mode, Native Materials & Multi-Monitor Memory

- **Smart capsules & Quick Reveal**: Collapse any widget into a smart capsule or a unified movable capsule bar with hover-to-expand and collapsed text masking. Summon widgets via the tray, `F7`, double `Ctrl`, `Alt+Space`, desktop double-click, or custom global hotkeys.
- **Native Windows styling**: Choose Mica or Acrylic materials, DWM rounded corners, custom per-widget icons and backgrounds, and material-matched, unified, or desktop-panorama global backgrounds.
- **Hardware-aware multi-monitor memory**: Remembers per-display-topology arrangements bound to monitor hardware identities — unplugging a screen gracefully collapses or relocates its widgets and restores every position upon reconnect, immune to DPI scaling changes, sleep, or full-screen games.

### 5. Lightweight Native AOT Runtime, Backup & Reliability

- **Isolated companion processes & low footprint**: Powered by `.NET 10 Full Native AOT` and a Rust native Shell layer. Settings and Widget Studio run in isolated companion host processes over authenticated named-pipe IPC and Shell thumbnails run out-of-process, reclaiming UI memory as soon as windows close alongside tiered idle working-set trimming.
- **Resilient storage, portable snapshots & cloud backups**: Atomic snapshot writes and automatic wipe-recovery offers protect settings and layouts from corruption, backed by scheduled local snapshots, portable cross-device backup exports (with optional decrypted clipboard snapshots and automatic credential stripping on untrusted restores), domain-scoped personal WebDAV cloud backups, and one-click privacy-filtered diagnostic bundles.

---

## Visual Experience

DeskBox adopts Windows 11 native materials and rounded geometry so widgets blend naturally into your desktop while keeping Explorer workflows intact.

| Mica | Acrylic |
| --- | --- |
| ![DeskBox desktop widgets with Mica material in English](docs/images/screenshots/en-us/云母材质.png) | ![DeskBox desktop widgets with Acrylic material in English](docs/images/screenshots/en-us/亚克力材质.png) |

| Widget Studio — Official Templates & Channels | Widget Studio — Live Stage & AI Co-Creation |
| --- | --- |
| ![DeskBox Widget Studio template gallery in English](docs/images/screenshots/en-us/studio-gallery.png) | ![DeskBox Widget Studio live stage preview and AI co-creation in English](docs/images/screenshots/en-us/studio-stage.png) |

<details>
<summary>Expand Settings screenshots (General / Appearance / Windows & Interaction / Files & Organization / Feature Widgets / Custom Widgets / Global Search / Data & Backup)</summary>

| General | Appearance |
| --- | --- |
| ![DeskBox General settings in English](docs/images/screenshots/en-us/settings-general.png) | ![DeskBox Appearance settings in English](docs/images/screenshots/en-us/settings-appearance.png) |

| Windows & interaction | Files & organization |
| --- | --- |
| ![DeskBox Windows and interaction settings in English](docs/images/screenshots/en-us/settings-interaction.png) | ![DeskBox Files and organization settings in English](docs/images/screenshots/en-us/settings-files-organization.png) |

| Feature widgets | Custom widgets & Widget Studio |
| --- | --- |
| ![DeskBox Built-in feature widgets settings in English](docs/images/screenshots/en-us/settings-feature-widgets.png) | ![DeskBox Custom widgets and Widget Studio settings in English](docs/images/screenshots/en-us/settings-custom-widgets.png) |

| Global search | Data & backup |
| --- | --- |
| ![DeskBox Global search settings in English](docs/images/screenshots/en-us/settings-search.png) | ![DeskBox Data and backup settings in English](docs/images/screenshots/en-us/settings-backup.png) |

</details>

---

## Technical Overview

| Item | Details |
| --- | --- |
| **Supported OS** | Windows 10 version 2004 (build 19041) or later; Windows 11 (22H2+ recommended) |
| **Architectures** | `x64` (Intel / AMD) and native `ARM64` (Windows on ARM) |
| **Stack** | C#, WinUI 3, .NET 10 Full Native AOT, Windows App SDK 2.7, Rust native Shell layer, QuickJS sandbox |
| **Data model** | Local-first; no account or mandatory cloud sync |
| **Languages (13)** | English, Simplified Chinese, Traditional Chinese, Japanese, German, Brazilian Portuguese, Hindi, Spanish, French, Arabic, Bengali, Russian, Korean |
| **Licensing** | [GPL-3.0-only](LICENSE) + [Commercial License](LICENSING.md) (dual-licensed) |

---

## Local-First, Privacy & Installation Notes

- **Your data stays local**: No account required. Settings and widget data live under `%LocalAppData%\DeskBox\data` on Direct builds (and `%UserProfile%\DeskBox\.appdata` on Microsoft Store builds so MSIX package resets never wipe your data), while new managed storage folders default to a fixed non-system drive with ample free space (such as `D:\DeskBox\<user>`) or `%UserProfile%\DeskBox`.
- **Network calls are strictly opt-in / intentional**: DeskBox only accesses the network for weather forecasts, update checks, ECDSA-verified remote widget/prompt catalog (`catalog`) updates, user-configured AI / WebDAV endpoints, enabled custom-widget network sources, confirmed remote URL imports, or explicitly submitted feedback diagnostics.
- **Upgrades, privileges & safe uninstall**: Direct builds install to `Program Files\DeskBox` or unelevated per-user `%LocalAppData%\Programs\DeskBox` via Inno Setup and preserve existing scope, settings, and storage across upgrades. Launching elevated shows a clear Explorer drag-and-drop notice with a one-click normal-privilege restart. Uninstalling lets you keep or remove app data while **always preserving user files inside managed storage folders**.

---

## Build from Source

Development requires **Windows 11** and **.NET SDK 10.0.404** (or a later `10.0.4xx` patch pinned in [global.json](global.json); Visual Studio with the Windows App SDK workload is recommended). Retail Native AOT builds and native Rust / QuickJS modules (`-p:DeskBoxRustNative=true`) also require the Rust toolchain pinned in [rust-toolchain.toml](rust-toolchain.toml) and Visual Studio C++ build tools (`VC.Tools.x86.x64` for `x64`, `VC.Tools.ARM64` for `ARM64`).

Restore, test, and build `x64` Debug:

```powershell
dotnet restore .\DeskBox.sln -p:Platform=x64
dotnet test .\tests\DeskBox.Tests\DeskBox.Tests.csproj --configuration Debug --no-restore -p:Platform=x64 -v:minimal
dotnet build .\src\DeskBox\DeskBox.csproj --configuration Debug --no-restore -p:Platform=x64 -v:minimal
```

Publish retail Full Native AOT payloads via the authoritative script (compiles Rust / QuickJS native libraries, bundles private Windows App Runtime 2.7, and generates `DeskBox.InstallManifest.txt` for safe upgrades):

```powershell
.\scripts\publish-aot-retail.ps1 -Platform x64
.\scripts\publish-aot-retail.ps1 -Platform ARM64
```

Compile offline installers with **Inno Setup 6.5 or newer** (do not substitute bare `dotnet publish`, as the installer requires `DeskBox.InstallManifest.txt` to clean up retired files safely):

```powershell
ISCC.exe /DDeskBoxNativeAot=1 /DDeskBoxBundledRuntime=1 /DMyAppReleaseDir=..\.artifacts\aot-retail\win-x64\publish .\installer\DeskBox.iss
ISCC.exe /DDeskBoxNativeAot=1 /DDeskBoxBundledRuntime=1 /DMyAppReleaseDir=..\.artifacts\aot-retail\win-arm64\publish .\installer\DeskBox.arm64.iss
```

Expected outputs:

```text
Output\DeskBox_Setup_1.6.0_x64.exe
Output\DeskBox_Setup_1.6.0_arm64.exe
```

### Project Layout

```text
src\DeskBox                 WinUI 3 app, widget engine, Studio, companion hosts, and core services
src\DeskBox.Updater         Direct-channel updater helper
native                      Rust native Shell / thumbnail proxy (deskbox-native / deskbox-thumbnail-proxy) and QuickJS sandbox (deskbox-qjs)
catalog                     Remote custom widget catalog and AI prompt OTA rules (widgets.v1.json / prompts.v1.json)
tests\DeskBox.Tests         Service, contract, sandbox, and Native AOT guardrail test suite
scripts                     AOT retail publish, native build, local dev, and catalog validation scripts
installer                   x64 / ARM64 Inno Setup scripts and multi-language installer messages
docs                        Architecture specs, requirements, code-signing policy, and visual assets
.github\workflows           CI build/test, ARM64 native gate, and distribution audit workflows
```

---

## Feedback, Credits & Licensing

- **Feedback & support**: Share bug reports or suggestions via the in-app **Feedback** entry, [GitHub Issues](https://github.com/Tianyu199509/DeskBox/issues), or [deskbox.fun](https://deskbox.fun); see [SUPPORT.md](SUPPORT.md) for details.
- **Special thanks**: [@magisph](https://github.com/magisph) for Brazilian Portuguese localization.
- **Author**: Tianyu Zhu (朱天雨) · Repository: <https://github.com/Tianyu199509/DeskBox>
- **Dual Licensing**: Starting with **1.6.0**, DeskBox is dual-licensed under [GPL-3.0-only](LICENSE) and a [Commercial License](LICENSING.md) (independent `.dbxwidget` custom widgets are explicitly exempt from GPL copyleft, while DeskBox branding and official `deskbox.fun` endpoints are reserved; commercial inquiries: `1047078635@qq.com`). Earlier MIT and single-track GPL-3.0-only releases remain under their original licenses and are not changed retroactively.

## Star History

If DeskBox makes your desktop cleaner and more enjoyable, a star ⭐ is a huge encouragement for this solo project.

[![Star History Chart](https://api.star-history.com/svg?repos=Tianyu199509/DeskBox&type=Date)](https://star-history.com/#Tianyu199509/DeskBox&Date)
