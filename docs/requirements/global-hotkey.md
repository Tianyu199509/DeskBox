# Global Hotkey Requirement

Status: Deferred
Created: 2026-06-17

## Goal

Add a configurable global hotkey that triggers the same behavior as left-clicking the DeskBox tray icon:

- If widgets are hidden behind other windows, temporarily raise them.
- Pointer movement should not restore widget layering.
- Clicking another non-DeskBox window should restore widgets to desktop level.
- If widgets are already raised from the tray, use the existing tray-left-click toggle behavior.

## Recommended Default

Default hotkey: `Ctrl + Alt + G`.

Rationale:

- `G` maps reasonably to "grid" / "gezi".
- Avoids Windows logo key combinations, which are commonly reserved by Windows.
- Lower conflict risk than common system and app shortcuts such as `F1`, `F2`, `F5`, `F11`, `F12`, `Ctrl + C`, `Ctrl + F`, `Alt + Space`, and `Win + D`.

## User Customization

Add settings UI under the Interaction section:

- Enable/disable global hotkey.
- Hotkey recorder input.
- Reset to default.
- Registration status text.
- Warning text for risky but allowed combinations.

Suggested user-facing behavior:

- Allow convenient shortcuts such as `F8`, `F9`, `Alt + G`, `Ctrl + G`, and `Ctrl + Alt + G`.
- Do not block weak combinations by default, but show a risk warning because global shortcuts can fire while the user is working in other apps.
- Reject plain letter/number keys without modifiers.
- Avoid or strongly warn for well-known system/app keys such as `F1`, `F2`, `F5`, `F11`, and `F12`.
- Avoid Windows logo key combinations.

## Conflict Handling

Use the native Windows `RegisterHotKey` API.

Conflict detection should be based on attempted registration:

- If `RegisterHotKey` succeeds, save and use the hotkey.
- If it fails because the hotkey is already registered, keep the previous working hotkey and show a clear message.

Suggested message:

> This shortcut is already used by Windows or another app. Choose a different shortcut.

Limitations:

- Windows does not expose which app owns an already registered hotkey.
- DeskBox should not claim to identify the occupying app.
- DeskBox should not force override another app's global hotkey.

## Non-Goals

- Do not implement a low-level keyboard hook for this feature.
- Do not intercept or suppress shortcuts owned by other apps.
- Do not try to display the exact app name that occupies a shortcut.

Reasons:

- Keyboard hooks are heavier than needed for this feature.
- Hooks can interfere with other apps, games, full-screen windows, elevated windows, and security software.
- The native global hotkey API is safer and better aligned with DeskBox as a lightweight desktop tool.

## Implementation Notes

Potential implementation shape:

- Add hotkey settings to `AppSettings`, for example:
  - `GlobalHotkeyEnabled`
  - `GlobalHotkeyModifiers`
  - `GlobalHotkeyKey`
- Add normalization/defaulting in `SettingsService`.
- Add a `HotkeyService` that:
  - registers/unregisters the configured hotkey,
  - listens for `WM_HOTKEY`,
  - re-registers when settings change,
  - uses `MOD_NOREPEAT` to avoid repeated toggles while a key is held,
  - invokes the same command path as tray left-click.
- Use the hidden tray host window or another app-owned HWND as the `RegisterHotKey` target.
- Surface registration failures in Settings without crashing startup.

## References

- Microsoft `RegisterHotKey`: https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-registerhotkey
- Microsoft `WM_HOTKEY`: https://learn.microsoft.com/windows/win32/inputdev/wm-hotkey
