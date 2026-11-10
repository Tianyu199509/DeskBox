function Get-DeskBoxPeUInt16 {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes,

        [Parameter(Mandatory)]
        [int]$Offset
    )

    if ($Offset -lt 0 -or $Offset + 2 -gt $Bytes.Length) {
        throw "PE read exceeds the file boundary at offset $Offset."
    }

    return [System.BitConverter]::ToUInt16($Bytes, $Offset)
}

function Get-DeskBoxPeUInt32 {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes,

        [Parameter(Mandatory)]
        [int]$Offset
    )

    if ($Offset -lt 0 -or $Offset + 4 -gt $Bytes.Length) {
        throw "PE read exceeds the file boundary at offset $Offset."
    }

    return [System.BitConverter]::ToUInt32($Bytes, $Offset)
}

function Convert-DeskBoxPeRvaToOffset {
    param(
        [Parameter(Mandatory)]
        [uint32]$Rva,

        [Parameter(Mandatory)]
        [object[]]$Sections,

        [Parameter(Mandatory)]
        [uint32]$SizeOfHeaders,

        [Parameter(Mandatory)]
        [int]$FileLength
    )

    if ($Rva -lt $SizeOfHeaders -and $Rva -lt $FileLength) {
        return [int]$Rva
    }

    foreach ($section in $Sections) {
        [uint64]$start = $section.VirtualAddress
        [uint64]$span = [Math]::Max(
            [uint64]$section.VirtualSize,
            [uint64]$section.SizeOfRawData)
        [uint64]$candidate = $Rva
        if ($candidate -ge $start -and $candidate -lt $start + $span) {
            [uint64]$sectionOffset = $candidate - $start
            if ($sectionOffset -ge [uint64]$section.SizeOfRawData) {
                throw "PE RVA 0x$($Rva.ToString('X8')) maps to an uninitialized section range."
            }

            [uint64]$offset = [uint64]$section.PointerToRawData + $sectionOffset
            if ($offset -ge [uint64]$FileLength) {
                throw "PE RVA 0x$($Rva.ToString('X8')) maps beyond the file boundary."
            }

            return [int]$offset
        }
    }

    throw "PE RVA 0x$($Rva.ToString('X8')) does not map to a file section."
}

function Get-DeskBoxPeAsciiString {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes,

        [Parameter(Mandatory)]
        [int]$Offset
    )

    if ($Offset -lt 0 -or $Offset -ge $Bytes.Length) {
        throw "PE string offset $Offset is outside the file."
    }

    $end = $Offset
    $maximumEnd = [Math]::Min($Bytes.Length, $Offset + 4096)
    while ($end -lt $maximumEnd -and $Bytes[$end] -ne 0) {
        $end++
    }

    if ($end -eq $maximumEnd) {
        throw "PE export name at offset $Offset is not null terminated."
    }

    return [System.Text.Encoding]::ASCII.GetString($Bytes, $Offset, $end - $Offset)
}

function Get-DeskBoxNativePeContract {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [ValidateSet("x64", "ARM64")]
        [string]$ExpectedPlatform,

        [Parameter(Mandatory)]
        [string[]]$RequiredExports
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "PE module was not found: '$fullPath'."
    }

    [byte[]]$bytes = [System.IO.File]::ReadAllBytes($fullPath)
    if ($bytes.Length -lt 0x40 -or
        (Get-DeskBoxPeUInt16 -Bytes $bytes -Offset 0) -ne 0x5A4D) {
        throw "'$fullPath' is not a DOS/PE image."
    }

    [int]$peOffset = [int](Get-DeskBoxPeUInt32 -Bytes $bytes -Offset 0x3C)
    if ((Get-DeskBoxPeUInt32 -Bytes $bytes -Offset $peOffset) -ne 0x00004550) {
        throw "'$fullPath' does not contain a valid PE signature."
    }

    $coffOffset = $peOffset + 4
    [uint16]$machine = Get-DeskBoxPeUInt16 -Bytes $bytes -Offset $coffOffset
    [uint16]$sectionCount = Get-DeskBoxPeUInt16 -Bytes $bytes -Offset ($coffOffset + 2)
    [uint16]$optionalHeaderSize =
        Get-DeskBoxPeUInt16 -Bytes $bytes -Offset ($coffOffset + 16)
    $optionalOffset = $coffOffset + 20
    [uint16]$optionalMagic = Get-DeskBoxPeUInt16 -Bytes $bytes -Offset $optionalOffset
    if ($optionalMagic -ne 0x20B) {
        throw "'$fullPath' is not a PE32+ module."
    }

    $expectedMachine = if ($ExpectedPlatform -eq "ARM64") { 0xAA64 } else { 0x8664 }
    if ($machine -ne $expectedMachine) {
        throw "Unexpected PE machine 0x$($machine.ToString('X4')) for '$fullPath'; expected $ExpectedPlatform (0x$($expectedMachine.ToString('X4')))."
    }

    [uint32]$sizeOfHeaders =
        Get-DeskBoxPeUInt32 -Bytes $bytes -Offset ($optionalOffset + 60)
    [uint32]$sizeOfImage =
        Get-DeskBoxPeUInt32 -Bytes $bytes -Offset ($optionalOffset + 56)
    [uint32]$numberOfRvaAndSizes =
        Get-DeskBoxPeUInt32 -Bytes $bytes -Offset ($optionalOffset + 108)
    if ($numberOfRvaAndSizes -lt 1) {
        throw "'$fullPath' has no PE export data directory."
    }

    [uint32]$exportRva = Get-DeskBoxPeUInt32 -Bytes $bytes -Offset ($optionalOffset + 112)
    [uint32]$exportSize = Get-DeskBoxPeUInt32 -Bytes $bytes -Offset ($optionalOffset + 116)
    if ($exportRva -eq 0 -or $exportSize -lt 40) {
        throw "'$fullPath' has no PE export table."
    }

    $sectionTableOffset = $optionalOffset + $optionalHeaderSize
    $sections = @(
        for ($index = 0; $index -lt $sectionCount; $index++) {
            $sectionOffset = $sectionTableOffset + ($index * 40)
            [pscustomobject]@{
                VirtualSize = Get-DeskBoxPeUInt32 -Bytes $bytes -Offset ($sectionOffset + 8)
                VirtualAddress = Get-DeskBoxPeUInt32 -Bytes $bytes -Offset ($sectionOffset + 12)
                SizeOfRawData = Get-DeskBoxPeUInt32 -Bytes $bytes -Offset ($sectionOffset + 16)
                PointerToRawData = Get-DeskBoxPeUInt32 -Bytes $bytes -Offset ($sectionOffset + 20)
            }
        }
    )

    $imports = @()
    if ($numberOfRvaAndSizes -ge 2) {
        [uint32]$importRva = Get-DeskBoxPeUInt32 -Bytes $bytes -Offset ($optionalOffset + 120)
        [uint32]$importSize = Get-DeskBoxPeUInt32 -Bytes $bytes -Offset ($optionalOffset + 124)
        if ($importRva -ne 0 -and $importSize -ge 20) {
            $importOffset = Convert-DeskBoxPeRvaToOffset `
                -Rva $importRva `
                -Sections $sections `
                -SizeOfHeaders $sizeOfHeaders `
                -FileLength $bytes.Length
            $terminated = $false
            $imports = @(
                @(
                for ($descriptorIndex = 0; $descriptorIndex -lt 4096; $descriptorIndex++) {
                    $descriptorOffset = $importOffset + ($descriptorIndex * 20)
                    [uint32]$originalFirstThunk =
                        Get-DeskBoxPeUInt32 -Bytes $bytes -Offset $descriptorOffset
                    [uint32]$timeDateStamp =
                        Get-DeskBoxPeUInt32 -Bytes $bytes -Offset ($descriptorOffset + 4)
                    [uint32]$forwarderChain =
                        Get-DeskBoxPeUInt32 -Bytes $bytes -Offset ($descriptorOffset + 8)
                    [uint32]$nameRva =
                        Get-DeskBoxPeUInt32 -Bytes $bytes -Offset ($descriptorOffset + 12)
                    [uint32]$firstThunk =
                        Get-DeskBoxPeUInt32 -Bytes $bytes -Offset ($descriptorOffset + 16)
                    if ($originalFirstThunk -eq 0 -and
                        $timeDateStamp -eq 0 -and
                        $forwarderChain -eq 0 -and
                        $nameRva -eq 0 -and
                        $firstThunk -eq 0) {
                        $terminated = $true
                        break
                    }
                    if ($nameRva -eq 0) {
                        throw "'$fullPath' contains an import descriptor without a module name."
                    }

                    $nameOffset = Convert-DeskBoxPeRvaToOffset `
                        -Rva $nameRva `
                        -Sections $sections `
                        -SizeOfHeaders $sizeOfHeaders `
                        -FileLength $bytes.Length
                    Get-DeskBoxPeAsciiString -Bytes $bytes -Offset $nameOffset
                }
                ) | Sort-Object -Unique
            )
            if (-not $terminated) {
                throw "'$fullPath' import descriptor table is not terminated."
            }
        }
    }

    $exportOffset = Convert-DeskBoxPeRvaToOffset `
        -Rva $exportRva `
        -Sections $sections `
        -SizeOfHeaders $sizeOfHeaders `
        -FileLength $bytes.Length
    [uint32]$nameCount = Get-DeskBoxPeUInt32 -Bytes $bytes -Offset ($exportOffset + 24)
    [uint32]$namesRva = Get-DeskBoxPeUInt32 -Bytes $bytes -Offset ($exportOffset + 32)
    if ($nameCount -gt 65536) {
        throw "'$fullPath' exposes an unreasonable PE export-name count: $nameCount."
    }

    $namesOffset = Convert-DeskBoxPeRvaToOffset `
        -Rva $namesRva `
        -Sections $sections `
        -SizeOfHeaders $sizeOfHeaders `
        -FileLength $bytes.Length
    $exports = @(
        @(
        for ([uint32]$index = 0; $index -lt $nameCount; $index++) {
            [uint32]$nameRva =
                Get-DeskBoxPeUInt32 -Bytes $bytes -Offset ($namesOffset + ([int]$index * 4))
            $nameOffset = Convert-DeskBoxPeRvaToOffset `
                -Rva $nameRva `
                -Sections $sections `
                -SizeOfHeaders $sizeOfHeaders `
                -FileLength $bytes.Length
            Get-DeskBoxPeAsciiString -Bytes $bytes -Offset $nameOffset
        }
        ) | Sort-Object -Unique
    )

    $missingExports = @($RequiredExports | Where-Object { -not ($exports -ccontains $_) })
    if ($missingExports.Count -gt 0) {
        throw "'$fullPath' is missing required exports: $($missingExports -join ', ')."
    }

    [pscustomobject]@{
        Path = $fullPath
        Platform = $ExpectedPlatform
        Machine = $machine
        MachineHex = "0x$($machine.ToString('X4'))"
        MachineName = if ($machine -eq 0xAA64) { "ARM64" } else { "x64" }
        SizeOfImage = $sizeOfImage
        ExportCount = $exports.Count
        Exports = $exports
        RequiredExports = @($RequiredExports)
        MissingExports = $missingExports
        ImportCount = $imports.Count
        ImportedModules = $imports
    }
}
