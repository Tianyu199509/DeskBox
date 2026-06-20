# Deferred Online Update Draft

This folder keeps the first implementation draft for the DeskBox online update feature.

The feature is intentionally not compiled into the app right now. The product decision is to pause online updates until server, domestic download reliability, package signing, and rollout risks are evaluated further.

Files:

- `UpdateManifest.cs.txt` - JSON manifest models.
- `UpdateCheckResult.cs.txt` - update check result types.
- `UpdateService.cs.txt` - draft check/download/SHA-256/install service.
- `UpdateServiceTests.cs.txt` - draft version comparison tests.

To restore later, copy these files back to their original locations and remove the `.txt` suffix:

- `src/DeskBox/Models/UpdateManifest.cs`
- `src/DeskBox/Models/UpdateCheckResult.cs`
- `src/DeskBox/Services/UpdateService.cs`
- `tests/DeskBox.Tests/UpdateServiceTests.cs`

Then reintroduce the settings UI from `docs/requirements/online-update.md` rather than leaving any partial UI in the main app.
