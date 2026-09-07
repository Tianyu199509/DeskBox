# Builds the stage-3.5 WASM leg (roadmap): guest component + Wasmtime host,
# then refreshes the spike package (artifact copy + integrity + signature).
# The crate is standalone BY DESIGN (native/deskbox-wasm-spike) - the app
# build, audit, and retail scripts never reference it (roadmap red line).
#
# Toolchain prerequisites (one-time):
#   rustup target add wasm32-unknown-unknown
#   cargo install wasm-tools --locked
param(
    [switch]$SkipHost
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent | Split-Path -Parent
$crate = Join-Path $repo 'native\deskbox-wasm-spike'

# The local cargo mirror needs the revocation check disabled behind the
# proxy (schannel CRYPT_E_REVOCATION_OFFLINE).
$env:CARGO_HTTP_CHECK_REVOKE = 'false'

Push-Location $crate
try {
    Write-Host '[1/4] building guest core module (wasm32-unknown-unknown, no_std)...'
    cargo build --release --target wasm32-unknown-unknown -p github-stats-wasm-guest
    if ($LASTEXITCODE -ne 0) { throw 'guest build failed' }

    if (-not $SkipHost) {
        Write-Host '[2/4] building host (wasmtime)...'
        cargo build --release -p deskbox-wasm-spike-host
        if ($LASTEXITCODE -ne 0) { throw 'host build failed' }
    }

    Write-Host '[3/4] wrapping the core module into a component (wasm-tools)...'
    $core = Join-Path $crate 'target\wasm32-unknown-unknown\release\github_stats_wasm_guest.wasm'
    $embedded = Join-Path $env:TEMP 'deskbox-wasm-spike-embedded.wasm'
    $component = Join-Path $repo 'spikes\github-stats-wasm\plugin\plugin.wasm'
    wasm-tools component embed (Join-Path $crate 'wit') --world plugin-world $core -o $embedded
    if ($LASTEXITCODE -ne 0) { throw 'wasm-tools component embed failed' }
    wasm-tools component new $embedded -o $component
    if ($LASTEXITCODE -ne 0) { throw 'wasm-tools component new failed' }
}
finally {
    Pop-Location
}

Write-Host '[4/4] refreshing the spike package (integrity + signature)...'
Push-Location $repo
try {
    node scripts\spike\build-package.mjs spikes\github-stats-wasm spikes\keys
    if ($LASTEXITCODE -ne 0) { throw 'package build failed' }
    node scripts\spike\validate-package.mjs spikes\github-stats-wasm
    if ($LASTEXITCODE -ne 0) { throw 'package validation failed' }
}
finally {
    Pop-Location
}

Write-Host 'wasm spike built and package refreshed. NOTE: wasm builds are not'
Write-Host 'byte-reproducible - always re-run this script (not just cargo) so the'
Write-Host 'package.integrity chain matches the committed artifact.'
