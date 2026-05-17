#requires -Version 7.0
<#
.SYNOPSIS
    Download (or build) libzstd native binaries into runtimes/<rid>/native/ so
    the build can copy them next to test/sample outputs and pack them into the
    nupkg.

.DESCRIPTION
    By default fetches the binary for the *current* host (RID inferred from
    [RuntimeInformation]). Use -All to fetch every RID we can satisfy from this
    host.

    Acquisition strategy per RID:
      win-x64    : download the prebuilt DLL from facebook/zstd v$Version release.
      win-arm64  : build libzstd_shared from source (CMake + MSVC) because
                   facebook/zstd does not publish a prebuilt ARM64 Windows DLL.
      linux-*    : copy from the system package (apt/dnf/apk) -- emits a hint
                   here because .ps1 is only expected to run on Windows hosts.
      osx-*      : copy from Homebrew -- hint-only here for the same reason.

    The script asserts (post-build) that the *host* RID's native directory is
    non-empty. Cross-RID failures in -All mode are logged but non-fatal.

.PARAMETER Version
    libzstd version to fetch/build. Default: 1.5.6.

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

function Assert-PEMachine([string]$path, [string]$rid) {
    $expected = switch ($rid) {
        'win-x64'   { 0x8664 }
        'win-arm64' { 0xAA64 }
        default { return }
    }
    $bytes = [System.IO.File]::ReadAllBytes($path)
    if ($bytes.Length -lt 0x40 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
        throw "[$rid] $path is not a valid PE file (missing MZ header)."
    }
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
    if ($peOffset -lt 0 -or $peOffset + 6 -ge $bytes.Length) {
        throw "[$rid] $path has invalid e_lfanew."
    }
    if ($bytes[$peOffset] -ne 0x50 -or $bytes[$peOffset + 1] -ne 0x45) {
        throw "[$rid] $path missing PE\0\0 signature."
    }
    $machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
    if ($machine -ne $expected) {
        throw ("[{0}] {1} has PE Machine 0x{2:X4}, expected 0x{3:X4}." -f $rid, $path, $machine, $expected)
    }
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

        $dll = Get-ChildItem -Path $tmp -Recurse -Filter 'libzstd.dll' | Select-Object -First 1
        if (-not $dll) {
            throw "[$rid] libzstd.dll not found inside $zipUrl"
        }
        $finalPath = Join-Path $destDir 'libzstd.dll'
        Copy-Item -LiteralPath $dll.FullName -Destination $finalPath -Force
        Assert-PEMachine $finalPath $rid
        Write-Host "[$rid] -> $finalPath"
    }
    finally {
        Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    }
}

function Build-WindowsBinaryFromSource([string]$rid, [string]$ver, [string]$cmakeArch) {
    if (-not (Get-Command 'cmake' -ErrorAction SilentlyContinue)) {
        throw "[$rid] 'cmake' is required to build libzstd from source but is not on PATH."
    }
    # Prefer the Windows built-in bsdtar (System32\tar.exe). It handles 'C:\...'
    # paths natively. MSYS GNU tar (Git for Windows) mangles backslashes / escapes
    # the colon when given native Windows paths.
    $tarExe = Join-Path $env:SystemRoot 'System32\tar.exe'
    if (-not (Test-Path -LiteralPath $tarExe)) {
        throw "[$rid] Windows bsdtar not found at $tarExe."
    }

    $srcUrl  = "https://github.com/facebook/zstd/releases/download/v$ver/zstd-$ver.tar.gz"
    $destDir = Join-Path $runtimes "$rid/native"
    New-Item -ItemType Directory -Force -Path $destDir | Out-Null

    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("zstd-src-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        $tarball = Join-Path $tmp "zstd.tar.gz"
        Write-Host "[$rid] downloading source $srcUrl"
        Invoke-WebRequest -Uri $srcUrl -OutFile $tarball -UseBasicParsing

        Write-Host "[$rid] extracting source (via $tarExe)"
        & $tarExe -xzf $tarball -C $tmp
        if ($LASTEXITCODE -ne 0) { throw "[$rid] tar extraction failed (exit $LASTEXITCODE)." }

        $srcRoot  = Join-Path $tmp "zstd-$ver"
        $cmakeSrc = Join-Path $srcRoot 'build\cmake'
        $buildDir = Join-Path $tmp 'build'

        Write-Host "[$rid] cmake configure (-A $cmakeArch)"
        & cmake -S $cmakeSrc -B $buildDir -A $cmakeArch `
            -DZSTD_BUILD_SHARED=ON `
            -DZSTD_BUILD_STATIC=OFF `
            -DZSTD_BUILD_PROGRAMS=OFF `
            -DZSTD_BUILD_TESTS=OFF
        if ($LASTEXITCODE -ne 0) { throw "[$rid] cmake configure failed (exit $LASTEXITCODE)." }

        Write-Host "[$rid] cmake build Release (libzstd_shared)"
        & cmake --build $buildDir --config Release --target libzstd_shared
        if ($LASTEXITCODE -ne 0) { throw "[$rid] cmake build failed (exit $LASTEXITCODE)." }

        $dll = Get-ChildItem -Path $buildDir -Recurse -Include 'zstd.dll', 'libzstd.dll' | Select-Object -First 1
        if (-not $dll) {
            throw "[$rid] built DLL not found under $buildDir."
        }
        $finalPath = Join-Path $destDir 'libzstd.dll'
        Copy-Item -LiteralPath $dll.FullName -Destination $finalPath -Force
        Assert-PEMachine $finalPath $rid
        Write-Host "[$rid] -> $finalPath (built from v$ver source)"
    }
    finally {
        Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    }
}

function Show-UnixHint([string]$rid) {
    Write-Warning "[$rid] this RID is acquired from a system package manager on its native host; .ps1 cannot satisfy it on Windows. Use fetch-natives.sh on a Linux/macOS host."
    switch -Regex ($rid) {
        '^linux-x64$'   { Write-Host "    sudo apt-get install -y libzstd1 && cp /usr/lib/x86_64-linux-gnu/libzstd.so.1 runtimes/$rid/native/libzstd.so" }
        '^linux-arm64$' { Write-Host "    sudo apt-get install -y libzstd1 && cp /usr/lib/aarch64-linux-gnu/libzstd.so.1 runtimes/$rid/native/libzstd.so" }
        '^osx-'         { Write-Host "    brew install zstd && cp `"`$(brew --prefix zstd)/lib/libzstd.dylib`" runtimes/$rid/native/libzstd.dylib" }
    }
}

$hostRid = Get-HostRid
$targets = if ($All) {
    @('win-x64','win-arm64','linux-x64','linux-arm64','osx-x64','osx-arm64')
} else {
    @($hostRid)
}

foreach ($rid in $targets) {
    $isHost = ($rid -eq $hostRid)
    try {
        switch -Regex ($rid) {
            '^win-x64$'   { Save-WindowsBinary $rid $Version }
            '^win-arm64$' { Build-WindowsBinaryFromSource $rid $Version 'ARM64' }
            default       { Show-UnixHint $rid }
        }
    }
    catch {
        if ($isHost) { throw }
        Write-Warning "[$rid] skipped (cross-RID, non-fatal): $($_.Exception.Message)"
    }
}

# Post-build assertion: the host RID *must* have produced at least one file.
$hostDir = Join-Path $runtimes "$hostRid/native"
$hostFiles = @(Get-ChildItem -Path $hostDir -File -ErrorAction SilentlyContinue)
if ($hostFiles.Count -eq 0) {
    throw "[$hostRid] no binary produced under $hostDir (post-build assertion failed)."
}

Write-Host ""
Write-Host "Done. Binaries land under: $runtimes"
