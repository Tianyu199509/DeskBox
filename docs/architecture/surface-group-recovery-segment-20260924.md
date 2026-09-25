# Surface and group recovery candidate

This local D candidate starts from the clean 21-batch checkpoint `f3f357f1`.
It adds the tested A+B review fixes, the C registration guard for a zero HWND,
and the D merge-failure repair. The original shared checkout, the A+B/C
worktrees, the memory experiment, and batch-22 QuickCapture text size remain
separate. No changes here have been committed, pushed, or merged.

## Persisted merge recovery

The failure window is: new group topology saved, Surface-host commit fails,
and saving the old topology as rollback also fails. At that point disk and
memory must retain the committed group. The old source/target Registry claims
cannot be left in place because the next group notification or member switch
may fail closed on their conflicting members.

The recovery now first reconciles exact current member claims to a surviving
target host in one Registry operation. The source host is retired only after
that transfer succeeds. If the target host is gone, recovery tries one new
unified host. If neither can be established, it closes the affected old hosts,
removes their claims, and attempts a clean rebuild. A still-unavailable visible
group remains without an active host rather than leaving contradictory live
claims; the saved group can be restored on the next launch. Unexpected claims
from another group remain a hard error, never an implicit transfer.
Quarantine also releases the retired source group's switch gate.
If retirement itself throws, a second exact-host hide/close attempt runs
before its Registry claim is discarded.

Isolated Debug fault stages `merge-post-save-commit`, `merge-rollback-save`,
and `merge-recovery-promotion` are one-shot and require
`DESKBOX_DEV_DATA_ROOT`. A headless test consumed the first two through the
actual `MergeWidgetsAsync` hidden-group path and verified that the committed
group reloads from disk. Registry tests model a live surviving target plus a
source group and verify that all member claims move atomically; an unrelated
claim is still rejected. Source-order tests keep the recovery call between
failed rollback and group notification.

The same review found two narrower D defects. Reused-detach rollback now
passes the visibility restored by its snapshot into replacement retirement,
so cleanup cannot overwrite it with `true`. A missing active-member config
now fails promotion explicitly and enters transaction compensation instead of
returning as if promotion succeeded. The stale business-global-access budget
for `SettingsViewModel.FeatureCallbacks.cs` was removed because its current
violation count is zero.

## Validation and remaining acceptance

- Focused Surface, group, boundary and deletion tests: **104/104 passed**;
  the later direct-merge subset passed **55/55**.
- Full x64/win-x64 suite: **4,215/4,215 passed**. Ignored local TRX:
  `tests/DeskBox.Tests/TestResults/architecture-d-merge-recovery-final2-20260924.trx`.
- Canonical Debug build: **0 errors**. Release AOT audit/smoke conditional
  compilation: **0 errors, 888 warnings**. This is not Native AOT publish/link.
- Isolated Debug data-root launch: PID 5904 from this worktree's canonical
  executable, **36 startup steps, 0 degraded, 0 failed**.

The double-failure tests do not replace a physical visible-HWND merge, first-
frame, drag, layer-order, detach and dissolve acceptance pass. The
`merge-recovery-promotion` fallback also needs a visible-host failure exercise
before treating its degraded presentation as device-verified. A final combined
tree must be reviewed before PR or release. Batch-22 text size remains an
independent increment; Native AOT publish/link and a packaged run are release
gates still outstanding.

The next verification batch is a visible-window fault run under an isolated
developer data root. Create two visible File widgets, inject the post-save
commit and rollback-save stages, and inspect persisted group membership,
actual HWND ownership, Registry claim logs, source retirement and restart
reconstruction. Exercise a persistent host-creation failure separately to
check the no-host quarantine and later rebuild. Then perform normal physical
merge, member switch, detach and dissolve gestures before declaring D accepted.

An unlaunched profile for that run is prepared at
`C:/Users/simon/AppData/Local/DeskBox-Dev/architecture-d-visible-fault-20260924`.
It contains two visible File widgets, `故障注入-源` and `故障注入-目标`, with separate
sample folders under the same profile; its managed storage root is also inside
that profile. Close the current DeskBox instance before launching the canonical
Debug executable with `DESKBOX_DEV_DATA_ROOT` set to that directory and
`DESKBOX_DEV_GROUP_FAIL_STAGE` set to
`merge-post-save-commit,merge-rollback-save`. Drag source onto target once,
then inspect the profile's `DeskBox.log`, `widget-layout.json`, and the widget
HWNDs. Restart without the failure variable to verify durable reconstruction.
The local `launch-merge-check.ps1` beside that profile starts either phase:
run it with `-Fault` for the first launch and without that switch after exit.
It refuses to start while any DeskBox process is running and never closes one.
The source widget's title-bar `…` menu → `组合格子…` → `故障注入-目标` is a
deterministic alternative to the drag gesture for triggering the injected
merge; test the physical drag separately after a clean restart.

## Visible double-failure run (2026-09-24)

The prepared profile was launched with the two merge failure stages in the
canonical Debug build, PID 34092, Medium integrity. Startup reported 35 steps,
0 degraded and 0 failed, and two visible native widgets were loaded. The user
combined source into target and reported normal interaction. At 15:57:18 the
log recorded both `merge-post-save-commit` and `merge-rollback-save`, followed
by adoption of the surviving target HWND `0xA40F4C` and a completed merge.
The durable `widget-layout.json` contains one visible group with both original
member IDs. Three subsequent member switches settled on that same HWND with
`registryEntries=1` and a presentable frame; neither source/target sample file
was changed, and the log contains no member-claim conflict or recovery error.

This confirms the visible surviving-target branch of the double-failure path.
The user then exited normally from the tray. At 16:17:24 the same profile
restarted without the fault variable as PID 31492: startup reported 35 steps,
0 degraded and 0 failed. It restored the same group ID
`12d902ad-2941-49eb-b2d2-6c619394ed85` as one visible group Surface on
new HWND `0x17D1196`; the layout still has both members and both sample files
remain present. No new group, Registry or unhandled error appeared after the
restart. This completes the surviving-target double-failure persistence check.
The no-target-host quarantine branch and normal physical detach/dissolve
gestures remain separate acceptance cases.

## Normal group gestures after recovery

On the no-fault restart, the user removed one member and reported two working
windows. The log recorded reused active-Surface detach at 16:19:53, then a
normal re-merge at 16:19:57. The user subsequently selected dissolve; the log
recorded `Dissolved group` at 16:21:35. The durable layout then contained zero
groups and two visible File widgets, each still pointing to its intact sample
file. There were no member-claim conflict or unhandled-error records.

With the user's authorization to end DeskBox processes during testing, that
test instance was force-stopped after the layout commit and relaunched without
fault injection as PID 37372. Startup reported 35 steps, 0 degraded and
0 failed, with two restored widget HWNDs. The durable layout still contained
zero groups and two visible widgets, and both sample files remained present.
This verifies persistence even across that abrupt test-process exit. At this
stage, no-target-host quarantine and Native AOT publish/link still awaited
the follow-up runs recorded below.

## No-target-host quarantine and reused-detach fault runs

The combined candidate added Debug-only, isolated-data-root fault points
`merge-recovery-target-unavailable` and `merge-recovery-rebuild` so the branch
with no adoptable target and no replacement host could be exercised without
changing Release behavior. The test profile at
`C:/Users/simon/AppData/Local/DeskBox-Dev/architecture-d-no-host-20260924`
started with two visible File widgets and intact sample files. At 18:45:00,
the user combined the widgets while five fault stages were active:
`merge-post-save-commit`, `merge-rollback-save`,
`merge-recovery-target-unavailable`, `merge-recovery-promotion`, and
`merge-recovery-rebuild`. The user saw both windows temporarily disappear.
The log recorded all five injections, quarantine, and an unavailable live
host; it recorded no member-claim conflict or unhandled exception. The
durable layout retained one visible group with both member IDs, and both
sample files remained present.

After the test process was stopped, the same profile started without faults
as PID 22040. At 18:46:34 it reconstructed the same group on HWND `0x601648`;
startup reported 35 steps, 0 degraded and 0 failed. Surface evidence showed
`registryEntries=1` and `presentable=True`. The user confirmed that the
group appeared and member switching worked. This verifies durable recovery
from the no-host quarantine case on the current Windows device.

A separate profile at
`C:/Users/simon/AppData/Local/DeskBox-Dev/architecture-d-reused-detach-20260924`
tested the actual drag-detach path. A title-bar menu removal first produced
`reusedSurface=False`, so that gesture did not exercise the rollback branch.
After re-merging, the user's first drag of the active member tab outside the
window triggered `reused-detach-first-frame` at 18:50:11. The production path
logged `Detach Surface rollback completed ... saved=True
boundsRestored=True`. Later drags in the same process completed successfully
with `reusedSurface=True`; the final layout had zero groups and two working
File widgets. The final two-window observation therefore reflects subsequent
successful detaches, not failure of the injected rollback. This run does not
isolate a pre-rollback `IsVisible=false` member state; the reachability check
below determines whether that state is a meaningful device-test case.

## Hidden-member reachability check

The later code review found that a valid persisted visible group cannot keep
an individually hidden member. `WidgetGroupSettings.Normalize` aligns every
member's `IsVisible` with the group's value on load, and both
`ApplyGroupLayoutToMember` and `SetWidgetGroupVisibility` maintain that
group-level value during ordinary interactions. The reused active-Surface
detach branch also requires a visible host. The existing
`Normalize_SynchronizesMemberVisibilityWithTheVisibleGroupSurface` test passed
again (1/1).

To test the persisted edge rather than assume it, an isolated copy at
`C:/Users/simon/AppData/Local/DeskBox-Dev/architecture-d-hidden-normalize-20260924`
was seeded with one visible group, two members, and one member deliberately
marked `IsVisible=false`. On Debug startup, the layout was rewritten with
both members visible while the group remained intact. Startup reported 35
steps, 0 degraded and 0 failed; the group had a visible HWND, and both sample
files remained present. The process was then stopped. Therefore this
particular hidden-member snapshot cannot be carried from disk into the
reused-detach rollback path. The visibility-preserving retirement remains a
defensive fix for an inconsistent transient in-memory state; a physical
"hidden member inside a valid visible group" gesture is not a meaningful
remaining acceptance gate without first identifying a legitimate runtime
path that can create that state.

## Stacked D review branch

`codex/architecture-surface-group-review` starts at the C registration commit
`6728c2e4`. This segment adds only the persistent Surface/group transaction
code, exact Registry claim transfer and gates, isolated Debug fault probes,
and their tests. The final module-boundary ratchet travels here after A+B/C;
the QuickCapture text-size ownership law is reserved for the following batch.
Compared with the previously validated combined candidate, the remaining
source/test differences are exactly the nine batch-22 text-size paths.

This stacked D branch passed **4,224/4,224** full x64 tests (ignored local
TRX `tests/DeskBox.Tests/TestResults/architecture-d-stacked-20260925.trx`)
and the complete x64 Native AOT publish/link plus `publish-aot-audit.ps1`
audit: 44 publish files, `AlwaysThrowCount=0`, stable source snapshot.
The canonical Debug build passed with 0 errors. An isolated launch, PID 3060,
restored the persisted two-member group on HWND `0x1120C66`; startup reported
35 steps, 0 degraded and 0 failed at Medium integrity. Both sample files
remained present and no member-claim conflict or unhandled exception was
logged. The test process was stopped afterward.
