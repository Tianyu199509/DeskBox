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

## Review hardening (2026-09-24, uncommitted)

An independent review identified three A+B paths worth closing before the next
segment. Search widget deletion now awaits the Search settings owner's runtime
commit; the synchronous helper rejects Search before changing its cached state.
Todo menu toggles now enter the same coordinator gate as settings toggles, so
their order is preserved. Cloud snapshot deletion, download, restore staging,
and marker-mode changes now have an operation lifetime owned by the settings
window. Shutdown cancels and drains those operations before disposing backup
services, removes their temporary downloads, and cancels an unconfirmed scoped
restore marker. A successfully scheduled restore relaunch keeps its marker.

The focused tests passed **129/129**. The complete x64 suite passed
**4,161/4,161**; the ignored local result is
`tests/DeskBox.Tests/TestResults/architecture-ab-review-fixes-20260924.trx`.
Release AOT audit/smoke conditional compilation passed with **0 errors, 888
warnings**; Native AOT publish/link was not run. The canonical Debug build
passed with **0 errors, 22 warnings**. An isolated development-data launch
reported **36 startup steps, 0 degraded, 0 failed**, running the Debug
executable from this worktree. This is a startup smoke, not manual verification
of Search deletion, Todo menu interaction, or a real cloud restore.

## Bounded backup shutdown (2026-09-24, uncommitted)

App teardown now gives each of the backup-restore-actions and backup-runtime
steps a **15-second grace period**. Both owners request cancellation and normally
drain their work. If a backend ignores cancellation, ShutdownSequence logs the
timeout and continues closing windows and releasing the single-instance lock;
the underlying stop task remains owned by its runtime until the process exits
or it completes. An accepted backup commit still wins over late cancellation.

The local archive is written to a temporary file and renamed only when complete.
The pending-restore marker is also written atomically. New scoped cloud markers
are explicitly unconfirmed until the user confirms the item mode; startup
discards an unconfirmed marker and its staging. Older markers without this field
retain their previous behavior. This closes the path where a crash or bounded
exit during the confirmation dialog could apply an unapproved cloud restore.
Remote WebDAV PUT atomicity depends on the server: an exceptional forced exit
may leave a partial remote object or temporary download; restore validation
rejects an invalid archive, but this is not a promise of zero residue.

Non-cooperative-backend and marker-compatibility focused tests passed **150/150**.
The complete x64 suite passed **4,163/4,163**; the ignored local result is
`tests/DeskBox.Tests/TestResults/architecture-ab-bounded-shutdown-20260924.trx`.
Release AOT audit/smoke conditional compilation passed with **0 errors, 888
warnings**. The canonical Debug build passed with **0 errors, 22 warnings**.
Native AOT publish/link and physical Search, Todo, and backup interactions have
not been run for this branch.

The next independent segment is C (content/file window registration) on a
reviewed local A+B checkpoint, with identity and late-close tests. D
(Surface/group transactions) follows C; before integrating D, inject merge
post-save commit failure plus rollback-save failure and prove that persisted
topology, live windows, and Registry claims recover coherently. Batch-22
QuickCapture text size remains separate. This branch is still local, uncommitted,
unpushed, and unmerged.

## Final A+B alignment after the combined-candidate review

The A+B branch now also carries the bounded QuickCapture shutdown step and
cooperative recent-trim cancellation. The open QuickCapture widget's
"Enable Recording" action persists the setting before starting the listener
and does not reveal another widget; the focused test reads the persisted
clipboard-enabled value at the moment the listener refreshes. Todo's
coordinator and editor reject non-finite text-size input without replacing a
stored zero/inherited size. Batch-22 QuickCapture text-size ownership remains
outside this segment.

The final A+B full x64 suite passed **4,167/4,167**; ignored local TRX:
`tests/DeskBox.Tests/TestResults/architecture-ab-final-20260924.trx`.
Release AOT audit/smoke conditional compilation passed with **0 errors, 888
warnings**. The canonical Debug build passed with **0 errors, 22 warnings**.
The complete x64 Native AOT publish/link and `publish-aot-audit.ps1` audit also
passed: 44 publish files, `AlwaysThrowCount=0`, and a stable source snapshot;
summary at `.artifacts/aot-audit/win-x64/summary.json`.
An isolated Debug launch from this worktree's canonical executable, PID 31512,
reported 36 startup steps, 0 degraded and 0 failed, Medium integrity. The
test process was stopped afterward. This branch remains at local HEAD
`61f05c5d` plus reviewable uncommitted fixes; no push, PR, or merge occurred.
