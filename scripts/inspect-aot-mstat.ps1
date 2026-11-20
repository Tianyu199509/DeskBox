[CmdletBinding()]
param(
    [string]$MstatPath = "",
    [int]$Top = 20
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($PSVersionTable.PSVersion.Major -lt 7) {
    $pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
    if ($null -eq $pwsh) {
        throw "inspect-aot-mstat.ps1 requires PowerShell 7+ (pwsh) for System.Reflection.Metadata.PEReader."
    }
    $forwardArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $PSCommandPath, "-Top", $Top)
    if (-not [string]::IsNullOrWhiteSpace($MstatPath)) {
        $forwardArgs += @("-MstatPath", $MstatPath)
    }
    & $pwsh.Source @forwardArgs | Write-Output
    return
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($MstatPath)) {
    $searchRoots = @(
        (Join-Path $repoRoot "src\DeskBox\obj"),
        (Join-Path $repoRoot ".artifacts")
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Container }
    $candidates = @(
        @(
            foreach ($root in $searchRoots) {
                Get-ChildItem -LiteralPath $root -Recurse -Filter "DeskBox.mstat" -File -ErrorAction SilentlyContinue
            }
        ) | Sort-Object LastWriteTimeUtc -Descending
    )
    if ($candidates.Count -eq 0) {
        throw "No DeskBox.mstat file found under 'src\DeskBox\obj' or '.artifacts'. Run scripts/publish-aot-audit.ps1 or scripts/publish-aot-retail.ps1 first."
    }
    $MstatPath = $candidates[0].FullName
}

$resolvedPath = [System.IO.Path]::GetFullPath($MstatPath)
if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
    throw "MSTAT file not found: '$resolvedPath'."
}

$stream = [System.IO.File]::OpenRead($resolvedPath)
$peReader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
try {
    $mr = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($peReader)
    $assemblyNames = @{}
    foreach ($arh in $mr.AssemblyReferences) {
        $ar = $mr.GetAssemblyReference($arh)
        $assemblyNames[$arh] = $mr.GetString($ar.Name)
    }

    $assemblyTypeCounts = @{}
    $namespaceGroupCounts = @{}
    foreach ($trh in $mr.TypeReferences) {
        $tr = $mr.GetTypeReference($trh)
        $scope = $tr.ResolutionScope
        if ($scope.Kind -eq [System.Reflection.Metadata.HandleKind]::AssemblyReference) {
            $asmHandle = [System.Reflection.Metadata.AssemblyReferenceHandle]$scope
            $asmName = $assemblyNames[$asmHandle]
            if (-not [string]::IsNullOrWhiteSpace($asmName)) {
                $assemblyTypeCounts[$asmName] = 1 + ($assemblyTypeCounts[$asmName] ?? 0)
            }
        }

        $ns = $mr.GetString($tr.Namespace)
        if (-not [string]::IsNullOrWhiteSpace($ns)) {
            $parts = $ns.Split('.')
            $group = if ($parts.Length -ge 2) { "$($parts[0]).$($parts[1])" } else { $ns }
            $namespaceGroupCounts[$group] = 1 + ($namespaceGroupCounts[$group] ?? 0)
        }
    }

    [pscustomobject]@{
        MstatPath = $resolvedPath
        FileBytes = (Get-Item -LiteralPath $resolvedPath).Length
        CompiledAssemblies = $mr.AssemblyReferences.Count
        CompiledTypes = $mr.TypeReferences.Count
        CompiledMembers = $mr.MemberReferences.Count
        TopAssembliesByTypeCount = @(
            $assemblyTypeCounts.GetEnumerator() |
                Sort-Object Value -Descending |
                Select-Object -First $Top |
                ForEach-Object { [pscustomobject]@{ Assembly = $_.Key; CompiledTypes = $_.Value } }
        )
        TopNamespaceGroupsByTypeCount = @(
            $namespaceGroupCounts.GetEnumerator() |
                Sort-Object Value -Descending |
                Select-Object -First $Top |
                ForEach-Object { [pscustomobject]@{ NamespaceGroup = $_.Key; CompiledTypes = $_.Value } }
        )
    }
}
finally {
    $peReader.Dispose()
    $stream.Dispose()
}
