# Per-topology file-icon dimensions

DeskBox already remembers surface geometry by display topology. This opt-in
extension remembers file-icon dimensions without replacing the user's settings.
The existing v3 topology key distinguishes monitor identity, arrangement,
resolution and DPI; transient DISPLAY aliases are not new profiles.

## Opt in through configuration

Stop DeskBox and back up its data before editing. If `data/widget-layout.json`
exists, merge the following member into its `layout` object. Otherwise merge it
into the legacy `data/settings.json`; the normal migration adopts it into the
device-local layout store on launch. Never replace the full document with this
example. There is no new settings-page toggle in this change.

```json
"widgetTopologyAppearanceDefaults": {
  "iconSize": 36,
  "textSize": 11.5,
  "horizontalSpacingScale": 0.1,
  "verticalSpacingScale": 0.3,
  "fileNameWidthScale": 0.25
}
```

These are example values, not a resolution-to-size heuristic. After opting in,
use the existing appearance controls to choose dimensions for the current
topology. Its `widgetTopologyLayouts[<key>].appearance` is saved with the layout.
Returning to that topology restores the five dimensions; restarting cannot
overwrite them with the stale global projection. Unknown topologies and older
profiles missing `appearance` use the baseline instead of inheriting the last
monitor's oversized icons. Equivalent legacy v1/v2 keys retain their saved
appearance when lazily migrated to v3.

Set `widgetTopologyAppearanceDefaults` to null to restore legacy global
behavior. Saved profile appearances may remain for re-enabling later.

## Boundaries

- This is per **whole display topology**, not different global fonts for two
  monitors in the same topology.
- Icon size and text size remain separate parameters; both participate in a
  profile, along with horizontal/vertical spacing and file-name width.
- Per-widget `IconSizeOverride`, themes, feature-widget text preferences, files,
  grouping and unrelated settings are not overwritten.
- Device-local appearance profiles are not new cloud-synced preferences.
- The feature does not create another monitor process, timer or service, and
  does not promise that every icon fits on a small logical work area.

## Verification

Targeted tests cover disabled compatibility, independent dimensions across
2560x1440/150% and 3840x2160/200% snapshots, restart, legacy key migration,
layout-store persistence, baseline fallback, late capture rejection, idempotent
updates, normalization and per-widget overrides. Existing topology, settings
ownership and layout-store contracts run alongside them.

Snapshot tests are not physical monitor hot-plug tests. Runtime and deployment
evidence should state which display configurations were actually exercised.
