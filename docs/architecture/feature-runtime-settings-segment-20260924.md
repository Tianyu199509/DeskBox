# Feature runtime and settings delivery segment

This local branch extracts the independently buildable feature portion of the
architecture checkpoint. It starts at `44e7a0d4` and uses `f3f357f1` as a
read-only source. The scope combines the planned A and B segments because the
new Todo/QuickCapture coordinators grew their setting snapshots in the same
files after their runtime ownership was introduced. Reconstructing an earlier
version of those classes would replace already tested behavior.

## Included

- Todo reminder settings and runtime; Search settings, master enablement and
  application-owned search runtime; BackupRuntime, settings editor/coordinator
  and shutdown draining; QuickCapture enablement, clipboard runtime, navigation,
  display and recent-item settings.
- App and settings-window composition for those owners, compatible JSON/XAML
  fields, and tests covering their behavior and dependency direction.
- The required feature hooks in `WidgetManager.cs` and
  `WidgetManager.FeatureWidgets.cs`.

## Deliberately excluded

- `MemoryDestroyProbe` and both parallel experiment call sites.
- Content-window and file-session registration helpers; Surface registry,
  switch gates, group-topology and persisted-recovery changes; their tests and
  Debug group-failure probe. The feature-window close path retains the baseline
  dictionary cleanup until the registration segment is delivered.
- The later QuickCapture text-size batch (batch 22), which remains in the shared
  source checkout and is not part of the 21-batch checkpoint.

`App.xaml.cs`, `SettingsWindow.xaml.cs` and the settings ViewModel partials are
copied from the checkpoint; only the feature-specific constructor/state hooks
were extracted into the baseline `WidgetManager.cs`. The content-registration
cleanup hunk in `WidgetManager.FeatureWidgets.cs` was restored to its baseline
form. No C/D registration or Surface helper is referenced by this branch.

## Validation

- x64/win-x64 restore and full test suite: **4,156 passed, 0 failed**.
- The feature dependency/ownership laws were carried into
  `FeatureSettingsBoundaryContractTests.cs` and passed with the full suite.
- Isolated Release AOT audit/smoke conditional build with Rust native:
  **0 errors, 890 warnings**. This is not Native AOT publish/link or packaged
  runtime validation.
- `git diff --check`: passed. The shared checkout and its DeskBox process were
  not changed or stopped for this extraction.

This branch is a local delivery candidate. It is not pushed or merged. The
next independent segments are content/file registration and then Surface/group
transactions. Each must carry its own tests and be validated on top of this
segment before a remote PR is considered.
