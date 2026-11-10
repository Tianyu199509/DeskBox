# Architecture documentation map

Agent entry point for the architecture docs. Read order: this map, then the
current-state snapshot, then only the contract you are about to touch.

## Current state

- [current_architecture.md](current_architecture.md) — start with the
  **2026-09-29 state snapshot** section near the top; the sections below it
  are the 1.2.0-era historical foundation.
- [architecture-optimization-progress-20260922.md](architecture-optimization-progress-20260922.md)
  — per-batch log, batches 1-51 in full (settings coordinators, facade
  retirement, P/Invoke, IFeatureRuntime, AppSettings verdict).

## Living contracts (must follow as written)

- [startup-policy.md](startup-policy.md) — startup pipeline policy.
- [sync-protocol-contract-20260918.md](sync-protocol-contract-20260918.md)
  — sync envelope / transport contract.
- [%5B重要勿删%5Dfile_drag_stack_contract.md]([重要勿删]file_drag_stack_contract.md)
  and [%5B重要勿删%5Dwidget_zorder_lifecycle.md]([重要勿删]widget_zorder_lifecycle.md)
  — the two do-not-delete widget contracts.
- Native ABI docs (Rust/AOT boundary; several are pinned by contract tests):
  [explorer-shell-launch-native-abi-v1.md](explorer-shell-launch-native-abi-v1.md),
  [music-volume-native-abi-v1.md](music-volume-native-abi-v1.md),
  [quick-access-native-abi-v1.md](quick-access-native-abi-v1.md),
  [recycle-bin-native-abi-v1.md](recycle-bin-native-abi-v1.md),
  [search-core-native-abi-v3.md](search-core-native-abi-v3.md),
  [shortcut-native-abi-v2.md](shortcut-native-abi-v2.md),
  [rust-native-aot-roadmap.md](rust-native-aot-roadmap.md).
- [widget_contribution_seam.md](widget_contribution_seam.md) — how feature
  widgets contribute settings/content without shell references.
- [native_context_menu_hosting.md](native_context_menu_hosting.md)
  — native context menu hosting rules.
- [json-source-generation-baseline.md](json-source-generation-baseline.md)
  — System.Text.Json source-generation baseline (no reflection serializers).
- [custom-widget-remote-catalog-workflow.md](custom-widget-remote-catalog-workflow.md)
  — remote OTA workflow for publishing, hot-updating, or deprecating official Custom Widget templates and AI prompt packs across `deskbox.fun` and GitHub (`scripts/publish_widget_catalog.py`).

## Settled verdicts (do not reopen)

- [residency-p0-attribution-20260926.md](residency-p0-attribution-20260926.md)
  — View Eviction / Cold tier skipped: P0 attribution 2-3% hit rate is below
  the stop line.
- AppSettings is a frozen on-disk wire contract: 220 passthroughs stay,
  runtime code reads slices — see the 2026-09-29 addendum in
  [module-boundary-roadmap-20260918.md](module-boundary-roadmap-20260918.md).
- 11 -> 13 settings coordinators were enough: the batch-50 close added only
  `PerformanceSettingsCoordinator`; no further coordinator splits without the
  five-question freeze-point test (same addendum).

## Process records (history, not current state)

- [architecture-optimization-progress-20260922.md](architecture-optimization-progress-20260922.md)
  — batch 1-51 execution log (also listed under Current state).
- [development-plan-20260919.md](development-plan-20260919.md)
- [cloud-backup-audit-and-live-sync-20260921.md](cloud-backup-audit-and-live-sync-20260921.md)
- [stage-reports/](stage-reports/) — one-shot stage reports and audits.

## Release gates

- [../../scripts/publish-aot-audit.ps1](../../scripts/publish-aot-audit.ps1)
  — sealed AOT publish audit (WMC1510 = 864, nameof-count ratchets).
- [../../scripts/run-aot-managed-ui-smoke.ps1](../../scripts/run-aot-managed-ui-smoke.ps1)
  — 21-scenario managed-UI smoke matrix.
- [../../scripts/publish-arm64-aot-static-audit.ps1](../../scripts/publish-arm64-aot-static-audit.ps1)
  — ARM64 static audit.

Environment invariants for each gate are stated in the script headers; read
them before running or editing the scripts.
