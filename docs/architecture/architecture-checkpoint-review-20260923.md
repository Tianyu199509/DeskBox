# Architecture checkpoint review

This is a local review checkpoint for the 21 DeskBox architecture batches recorded in
[architecture-optimization-progress-20260922.md](architecture-optimization-progress-20260922.md).
It is deliberately separate from the shared development checkout. It is not a
pull request or release artifact, and it has not been pushed.

## Source and isolation

- Base: `44e7a0d48769f8dbfb6d107715e0508918fc7d87` (`main` at capture time).
- Candidate branch: `codex/architecture-checkpoint-20260923`.
- Source checkout: `D:/project/wingezi`, branch `experiment/memory-probe-destroy-hidden`.
- Copied 99 changed or new architecture paths from the source checkout. At
  capture, SHA-256 matched for all 99 copies before excluding experiment code.
- Excluded `src/DeskBox/Services/MemoryDestroyProbe.cs`. Only this experiment's
  call sites were removed from the candidate copies of
  `src/DeskBox/Services/WidgetManager.cs` and
  `src/DeskBox/Views/SettingsWindow.xaml.cs`. The source checkout and its running
  DeskBox instance were not changed or stopped.
- All remaining candidate source and tests have no `MemoryDestroyProbe`,
  `DESKBOX_MEMPROBE`, or `[MemProbe]` reference.

## Review order

1. Batches 1–6 and 13–21: Todo, Search, Backup, and QuickCapture settings and
   runtime ownership, including shutdown draining and compatibility bindings.
2. Batches 7–12: content/file-window registration, Surface claims, group
   topology commits, and persisted-state compensation.
3. Boundary tests, JSON/AOT contracts, and the current architecture notes.

Do not treat all paths as one small change. The source checkout also contains
parallel work, and the Surface transaction code warrants its own focused review.
No parallel experiment code is part of this candidate.

## Candidate validation

- `git diff --check`: passed.
- Full x64 test suite after restoring x64/win-x64 assets:
  **4,203 passed, 0 failed**.
- Isolated Release AOT audit/smoke conditional build, x64/win-x64 with Rust
  native compilation: **0 errors, 890 warnings**. This was not Native AOT
  publish/link or a packaged-app run.
- The user reported manual testing without finding a problem in the shared
  development build. This candidate was not separately operated through the
  real UI. The settings-window startup logs from earlier batches opened only
  the General section; they do not prove deferred Todo/QuickCapture bindings.

The checkpoint is ready for a focused diff review. No remote pull request has
been created.
