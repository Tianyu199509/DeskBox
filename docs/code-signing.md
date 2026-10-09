# Code signing policy

DeskBox direct installers (`DeskBox_Setup_<version>_x64.exe` and `DeskBox_Setup_<version>_arm64.exe`) are built by GitHub Actions and signed in the same workflow run via SignPath.

Free code signing provided by [SignPath.io](https://signpath.io), certificate by the [SignPath Foundation](https://signpath.org).

## What is signed

- Direct installers published to GitHub Releases: Authenticode signatures with an RFC 3161 timestamp.
- The Microsoft Store package is re-signed by Partner Center and is not covered by this policy.

## Status

- CI signing with the SignPath test certificate has been verified end to end (2026-10-08).
- The production certificate is issued by the SignPath Foundation after their review. Installers published before the production certificate takes effect are unsigned; verify the `.sha256` sidecar in that case.
- From the first release published after the production certificate is imported, all direct installers are signed.

## How to verify

```powershell
# Signature (signed builds)
Get-AuthenticodeSignature .\DeskBox_Setup_x.y.z_x64.exe | Format-List Status, SignerCertificate, TimeStamperCertificate

# Hash (any build)
Get-FileHash .\DeskBox_Setup_x.y.z_x64.exe -Algorithm SHA256
```

The CI distribution gate also runs an automated signature check (`verify-signature` job) on every signing run before the artifacts are accepted.

## Team roles

DeskBox is a single-maintainer project. All three roles are held by the same person:

- Committer: Tianyu Zhu (Simon)
- Reviewer: Tianyu Zhu (Simon)
- Approver: Tianyu Zhu (Simon)

Builds must originate from GitHub Actions on GitHub-hosted runners (a SignPath Foundation requirement for origin verification).

## Privacy statement

This program will not transfer any information to other networked systems unless specifically requested by the user or installer/operator.

Feedback reports and diagnostic packages are only uploaded when the user explicitly submits them from the app.
