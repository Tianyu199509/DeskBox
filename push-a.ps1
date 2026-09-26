Set-Location D:\project\wingezi-final-a
git add -A
git commit --no-edit
git push origin codex/final-settings-appearance
$body = @'
## Summary

Track A batch 1 of the settings-facade dissolution (batch 29 of the architecture program): the Appearance section (26 fields) migrates to the established coordinator/editor pattern, plus the full inventory of the remaining SettingsViewModel flat-write surface (115 write points across 15 files, split into batches 33-39).

- New `Contracts/IAppearanceSettings` + `Services/AppearanceSettingsCoordinator` (sole settings-page writer, writes the slice directly) + `Features/Appearance/AppearanceSettingsViewModel` (thin editor seam that preserves the live-preview timing in the settings shell).
- Five SettingsViewModel partials become compat facades; the live-preview mechanics are preserved point-for-point (slider update writes raw fields only, material switch keeps write-preview-debounced-save order, global text size still refreshes Todo/QuickCapture editors immediately).
- SettingsSliceOwnership inventory shrinks (AppearanceCallbacks 16→0, AppearanceOptions 18→4, PreferenceCallbacks 21→17, WidgetForeground 6→4, main file 141→94); a new 26-field appearance write gate added.
- Includes merge resolution against main (feature-runtime registry wiring combined with the appearance coordinator assembly; progress-doc batch order 29→32 fixed, future settings batches renumbered 33-39).

## Validation

- Full suite 4,287/4,287 on the merged branch (= 4,252 base + 8 new appearance tests + 2 device-layer + 25 feature-runtime).
- Build 0 errors; isolated Debug start: 35 steps, 0 degraded / 0 failed, 19 preseeded non-default appearance values preserved on disk.
'@
[System.IO.File]::WriteAllText('D:\project\wingezi-final-a\pr-a.md', $body, (New-Object System.Text.UTF8Encoding($false)))
gh pr create --base main --head codex/final-settings-appearance --title "Migrate the Appearance settings section and inventory the remaining facade (batch 29)" --body-file D:\project\wingezi-final-a\pr-a.md
Remove-Item pr-a.md, push-a.ps1 -ErrorAction SilentlyContinue
