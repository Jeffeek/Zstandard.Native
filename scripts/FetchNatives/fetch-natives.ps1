#requires -Version 7.0
<#
.SYNOPSIS
    Download libzstd native binaries into runtimes/<rid>/native/ so the build
    can copy them next to test/sample outputs and pack them into the nupkg.

.DESCRIPTION
    By default fetches the binary for the *current* host (RID inferred from
    [RuntimeInformation]). Use -All to fetch every RID we can satisfy from the
    official upstream release (Windows x64/arm64). Linux/macOS RIDs aren't
    shipped as pre-built binaries by facebook/zstd; the script emits a hint
    pointing at the system package manager for those targets.

.PARAMETER Version
    libzstd version to fetch. Default: 1.5.6.

.PARAMETER All
    Try to fetch every supported RID instead of just the host.

.EXAMPLE
    pwsh scripts/FetchNatives/fetch-natives.ps1
    pwsh scripts/FetchNatives/fetch-natives.ps1 -Version 1.5.6 -All
#>

[CmdletBinding()]
param(
    [string]$Version = '1.5.6',
    [switch]$All
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot   = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$runtimes   = Join-Path $repoRoot 'runtimes'

function Get-HostRid {
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    $archName = switch ($arch) {
        'X64'   { 'x64' }
        'Arm64' { 'arm64' }
        default { throw "Unsupported host arch: $arch" }
    }
    if ($IsWindows) { return "win-$archName" }
    if ($IsLinux)   { return "linux-$archName" }
    if ($IsMacOS)   { return "osx-$archName" }
    throw "Unsupported host OS."
}

function Save-WindowsBinary([string]$rid, [string]$ver) {
    $zipUrl  = "https://github.com/facebook/zstd/releases/download/v$ver/zstd-v$ver-win64.zip"
    $destDir = Join-Path $runtimes "$rid/native"
    New-Item -ItemType Directory -Force -Path $destDir | Out-Null

    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("zstd-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        $zip = Join-Path $tmp "zstd.zip"
        Write-Host "[$rid] downloading $zipUrl"
        Invoke-WebRequest -Uri $zipUrl -OutFile $zip -UseBasicParsing
        Write-Host "[$rid] extracting"
        Expand-Archive -Path $zip -DestinationPath $tmp -Force

        # The official zip puts the DLL at zstd-v$ver-win64/dll/libzstd.dll
        $dll = Get-ChildItem -Path $tmp -Recurse -Filter 'libzstd.dll' | Select-Object -First 1
        if (-not $dll) {
            throw "[$rid] libzstd.dll not found inside $zipUrl"
        }
        Copy-Item -LiteralPath $dll.FullName -Destination (Join-Path $destDir 'libzstd.dll') -Force
        Write-Host "[$rid] -> $(Join-Path $destDir 'libzstd.dll')"
    }
    finally {
        Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    }
}

function Show-UnixHint([string]$rid) {
    Write-Warning "[$rid] facebook/zstd does not publish a pre-built Linux/macOS binary. Install via your system package manager and copy the .so/.dylib into runtimes/$rid/native/, e.g.:"
    switch -Regex ($rid) {
        '^linux-x64$'   { Write-Host "    sudo apt-get install -y libzstd1 && cp /usr/lib/x86_64-linux-gnu/libzstd.so.1 runtimes/$rid/native/libzstd.so" }
        '^linux-arm64$' { Write-Host "    sudo apt-get install -y libzstd1 && cp /usr/lib/aarch64-linux-gnu/libzstd.so.1 runtimes/$rid/native/libzstd.so" }
        '^osx-'         { Write-Host "    brew install zstd && cp `"`$(brew --prefix zstd)/lib/libzstd.dylib`" runtimes/$rid/native/libzstd.dylib" }
    }
}

$targets = if ($All) {
    @('win-x64','win-arm64','linux-x64','linux-arm64','osx-x64','osx-arm64')
} else {
    @((Get-HostRid))
}

foreach ($rid in $targets) {
    switch -Regex ($rid) {
        '^win-x64$'   { Save-WindowsBinary $rid $Version }
        '^win-arm64$' { Write-Warning "[$rid] facebook/zstd v$Version ships an x64 zip only. Bring your own arm64 build." }
        default       { Show-UnixHint $rid }
    }
}

Write-Host ""
Write-Host "Done. Binaries land under: $runtimes"
