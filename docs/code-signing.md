# Code Signing and Integrity Verification Policy

DeskBox distributes builds through two channels with different signing and verification flows:

- **Microsoft Store (`MSIX`)**: Signed by Microsoft Partner Center during Store ingestion and publishing.
- **Direct Installers (`DeskBox_Setup_<version>_x64.exe` and `DeskBox_Setup_<version>_arm64.exe`)**: Built in GitHub Actions (Native AOT Distribution Gate) and published to GitHub Releases alongside matching `.sha256` sidecars.

## Current Direct-Installer Policy

Because DeskBox adopts a dual-licensing model (`GPL-3.0-only` + Commercial License, see [LICENSING.md](../LICENSING.md)), direct installers do not use third-party OSS-only foundation signing services.

- **Current releases**: Direct installers are unsigned binaries accompanied by per-installer `.sha256` sidecars (`Get-FileHash`) and published over HTTPS on GitHub Releases and `deskbox.fun`. Verify the SHA-256 hash before running an unsigned installer.
- **Future commercial Authenticode signing**: When a self-managed commercial Authenticode code-signing certificate is provisioned for direct builds, releases will carry Authenticode signatures with an RFC 3161 timestamp, and the CI distribution gate (`verify-signature` job with `verify-authenticode=true`) enforces signature validity before artifacts are accepted.

## How to Verify

```powershell
# SHA-256 checksum (any release)
Get-FileHash .\DeskBox_Setup_x.y.z_x64.exe -Algorithm SHA256

# Authenticode signature (when commercial code signing is active)
Get-AuthenticodeSignature .\DeskBox_Setup_x.y.z_x64.exe | Format-List Status, SignerCertificate, TimeStamperCertificate
```

## Windows 11 Smart App Control (SAC) Considerations

On Windows 11 systems where **Smart App Control (SAC)** is in enforcement mode:

- **Direct installers**: Newly published unsigned Direct installers (`DeskBox_Setup_<version>_<arch>.exe`) whose hashes have not yet accumulated cloud reputation in Microsoft's Intelligent Security Graph (ISG) are blocked at launch by Windows Code Integrity (`ERROR_SYSTEM_INTEGRITY_POLICY_VIOLATION` / Win32 `4551` and related `ERROR_SYSTEM_INTEGRITY_REPUTATION_*` codes) before elevation or setup initialization.
- **User guidance**: Users should **not** disable Smart App Control to run an unsigned update, because Windows cannot re-enable SAC without a system reset. `DeskBox.Updater` detects policy/code-integrity launch blocks (`blocked-by-policy`), reopens the existing installation safely, and advises keeping the current version or migrating via backup to the Microsoft Store (`MSIX`) channel, which is signed by Microsoft Partner Center and trusted by SAC.
- **Future signing scope**: When commercial Authenticode signing is enabled for Direct builds, signing must cover all first-party PE payloads (`DeskBox.exe`, `DeskBox.Updater.exe`, `DeskBox.ThumbnailProxy.exe`, `deskbox_native.dll`, `deskbox_qjs.dll`) prior to packaging as well as Inno Setup's bootstrapper, uninstaller (`SignedUninstaller=yes`), and outer setup executable, followed by `.sha256` sidecar generation.

## Privacy Statement

DeskBox is a local-first desktop application and will not transfer user information to external networked systems unless explicitly requested by the user or operator (such as weather queries, user-configured WebDAV backups, update checks, or explicitly submitted feedback/diagnostic packages).
