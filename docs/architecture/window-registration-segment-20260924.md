# Window registration delivery segment

This local C-segment worktree starts at A+B commit `61f05c5d`. The later,
uncommitted A+B review fixes were reapplied without a commit; eleven files
outside the two shared WidgetManager seams match the A+B worktree after line
ending normalization. The clean `f3f357f1` architecture checkpoint supplied
the C registration helpers and tests. Its D Surface/group transactions and
the separate batch-22 QuickCapture text-size change were not copied.

## Included

- `ContentWindowRegistration` owns the existing content-window ID dictionary
  and HWND set together. It rejects duplicate IDs, duplicate or zero HWNDs,
  rebinds a persistent group host by identity, and ignores a retired window's
  late `Closed` event when another window already owns the ID.
- `FileSessionRegistration` owns the existing standalone File-session dictionary.
  Repeated registration is idempotent; a new content instance replaces its old
  session; old hosts can only unregister their own sessions.
- WidgetManager creation, deletion, feature close, group member switch and
  retirement, Surface candidate rollback, deferred initialization and process
  teardown now use those registration boundaries. Creation tracking and
  registration share one failure-cleanup scope. The two boundary tests prohibit
  direct dictionary/HWND mutations from returning to WidgetManager partials.

No additional collection of live windows or sessions was introduced. The
registration helpers mutate the existing dictionaries and set in place.

## Validation

- Focused registration, group, Surface and deletion tests: **60/60 passed**.
- Final full x64/win-x64 suite, including the two boundary laws: **4,173/4,173
  passed**. Ignored local evidence:
  `tests/DeskBox.Tests/TestResults/architecture-c-registration-final-20260924.trx`.
- Canonical Debug build: **0 errors**. Release AOT audit/smoke conditional build:
  **0 errors, 890 warnings**. Native AOT publish/link and packaged execution
  were not run.
- An isolated development-data launch ran the canonical Debug executable from
  this C worktree (PID 13288): **36 startup steps, 0 degraded, 0 failed**.
  Actual window close/rebuild and group-gesture acceptance remain device checks.

The next segment is D. Before integrating it, inject a merge failure after
topology persistence and a second failure while saving rollback. Verify the
persisted topology, live hosts and Registry claims remain coherent without
weakening fail-closed claim checks. Then review the reused-detach visibility
snapshot and missing-active-config path as narrower follow-ups. Run full x64,
AOT conditional compilation and real group gestures on the combined candidate.
The current C worktree has not been committed, pushed or merged.

## Final C alignment after A+B review fixes

The C checkout now includes the final A+B backup, QuickCapture shutdown and
open-widget recording, and Todo non-finite input fixes without bringing in
D's Surface-promotion candidate parameter or group recovery implementation.
The C registration helper and zero-HWND guard match the combined candidate.

The final full x64 suite passed **4,177/4,177**; ignored local TRX:
`tests/DeskBox.Tests/TestResults/architecture-c-final-20260924.trx`.
Release AOT audit/smoke conditional compilation passed with **0 errors, 888
warnings**. The canonical Debug build passed with **0 errors, 22 warnings**.
An isolated Debug launch from this worktree's canonical executable, PID 34056,
reported 36 startup steps, 0 degraded and 0 failed, Medium integrity. The
test process was stopped afterward. C remains a local worktree at HEAD
`61f05c5d` with uncommitted A+B/C deltas; no push, PR, or merge occurred.

## Stacked review branch

For delivery, `codex/architecture-window-registration-review` starts at the
validated A+B commit `c966a9a0`. Only the ten C-specific registration paths
were copied from the earlier C checkout; its older audit script was excluded
because the A+B base already contains the corrected version. The resulting
source and tests match that C checkout apart from the inherited A+B audit
script and its updated A+B note.

This stacked branch passed **4,177/4,177** full x64 tests, Release AOT
audit/smoke conditional compilation with **0 errors**, and a canonical Debug
build with **0 errors**. Isolated Debug PID 38208 started from this worktree's
canonical executable at Medium integrity: 36 startup steps, 0 degraded,
0 failed. It was stopped after verification. The local TRX is
`tests/DeskBox.Tests/TestResults/architecture-c-stacked-20260925.trx`.
