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

## Privacy Statement

DeskBox is a local-first desktop application and will not transfer user information to external networked systems unless explicitly requested by the user or operator (such as weather queries, user-configured WebDAV backups, update checks, or explicitly submitted feedback/diagnostic packages).
