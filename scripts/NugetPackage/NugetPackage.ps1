param(
    [Parameter(Mandatory=$false)]
    [string]$Version = "0.0.0-local-test",

    [Parameter(Mandatory=$false)]
    [ValidateSet("Core", "Runtimes", "All")]
    [string]$Project = "All"
)

$ErrorActionPreference = "Stop"

# Navigate to repository root (2 levels up from scripts/NugetPackage/)
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location (Join-Path $ScriptDir "../..")

Write-Host "======================================" -ForegroundColor Cyan
Write-Host "  NuGet Package Builder & Inspector" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""

$OutputDir  = ".\test-packages"
$ExtractDir = ".\test-extract"

Write-Host "Version:       $Version" -ForegroundColor Green
Write-Host "Output Dir:    $OutputDir" -ForegroundColor Green
Write-Host "Extract Dir:   $ExtractDir" -ForegroundColor Green
Write-Host ""

if (Test-Path $OutputDir)  { Write-Host "Cleaning previous packages..." -ForegroundColor Yellow; Remove-Item $OutputDir  -Recurse -Force }
if (Test-Path $ExtractDir) { Write-Host "Cleaning previous extracts..." -ForegroundColor Yellow; Remove-Item $ExtractDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDir | Out-Null

# ----------------------------------------------------------------
# Pack: Core (managed-only)
# ----------------------------------------------------------------
if ($Project -in @("Core", "All")) {
    Write-Host ""
    Write-Host "======================================" -ForegroundColor Yellow
    Write-Host "  Building Zstandard.Native (managed)" -ForegroundColor Yellow
    Write-Host "======================================" -ForegroundColor Yellow
    dotnet pack src\Zstandard.Native\Zstandard.Native.csproj `
        -c Release --output $OutputDir -p:PackageVersion=$Version
    if ($LASTEXITCODE -ne 0) { Write-Host "Error: Core pack failed!" -ForegroundColor Red; exit 1 }
}

# ----------------------------------------------------------------
# Pack: per-RID runtime packages
# ----------------------------------------------------------------
if ($Project -in @("Runtimes", "All")) {
    $rids = @("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")
    foreach ($rid in $rids) {
        Write-Host ""
        Write-Host "======================================" -ForegroundColor Yellow
        Write-Host "  Building Zstandard.Native.$rid" -ForegroundColor Yellow
        Write-Host "======================================" -ForegroundColor Yellow
        dotnet pack src\Zstandard.Native.Runtime\Zstandard.Native.Runtime.csproj `
            -c Release --output $OutputDir `
            -p:PackageVersion=$Version `
            -p:RuntimePackageRid=$rid
        if ($LASTEXITCODE -ne 0) { Write-Host "Error: $rid pack failed!" -ForegroundColor Red; exit 1 }
    }

    # Pack the meta-package last (depends on per-RID packages being in OutputDir)
    Write-Host ""
    Write-Host "======================================" -ForegroundColor Yellow
    Write-Host "  Building Zstandard.Native.Runtimes" -ForegroundColor Yellow
    Write-Host "======================================" -ForegroundColor Yellow

    # Write a temporary NuGet.config so restore can find the just-packed per-RID packages
    $tmpConfig = Join-Path $OutputDir "NuGet.config"
    $absOutputDir = Resolve-Path $OutputDir
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$absOutputDir" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local">
      <package pattern="Zstandard.Native.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content $tmpConfig

    dotnet nuget add source $absOutputDir --name local-runtimes-temp --configfile src\Zstandard.Native.Runtimes\NuGet.config 2>$null
    dotnet pack src\Zstandard.Native.Runtimes\Zstandard.Native.Runtimes.csproj `
        -c Release --output $OutputDir -p:PackageVersion=$Version
    if ($LASTEXITCODE -ne 0) { Write-Host "Error: Runtimes meta-package pack failed!" -ForegroundColor Red; exit 1 }
    Remove-Item $tmpConfig -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "======================================" -ForegroundColor Green
Write-Host "  All packages built successfully!" -ForegroundColor Green
Write-Host "======================================" -ForegroundColor Green
Write-Host ""

Write-Host "Created packages:" -ForegroundColor Yellow
Get-ChildItem $OutputDir -Filter *.nupkg | ForEach-Object {
    $size = "{0:N2}" -f ($_.Length / 1KB)
    Write-Host "  $($_.Name) ($size KB)" -ForegroundColor Cyan
}

Write-Host ""
Write-Host "Inspecting packages..." -ForegroundColor Yellow

foreach ($nupkgFile in Get-ChildItem $OutputDir -Filter *.nupkg) {
    $packageName = [System.IO.Path]::GetFileNameWithoutExtension($nupkgFile.Name)
    $extractPath = Join-Path $ExtractDir $packageName

    Write-Host ""
    Write-Host "Extracting $($nupkgFile.Name)..." -ForegroundColor Yellow

    $zipPath = "$($nupkgFile.FullName).zip"
    Copy-Item $nupkgFile.FullName $zipPath
    New-Item -ItemType Directory -Path $extractPath | Out-Null
    Expand-Archive $zipPath -DestinationPath $extractPath
    Remove-Item $zipPath

    Write-Host "Package contents for $packageName`:" -ForegroundColor Cyan
    Write-Host ""

    Get-ChildItem $extractPath -Recurse -Include *.dll,*.xml,*.md,libzstd.dll,libzstd.so,libzstd.dylib | ForEach-Object {
        $size = "{0:N2}" -f ($_.Length / 1KB)
        Write-Host "  $($_.FullName.Substring($extractPath.Length + 1)) ($size KB)" -ForegroundColor Gray
    }
}

Write-Host ""
Write-Host "======================================" -ForegroundColor Green
Write-Host "  Package inspection completed!" -ForegroundColor Green
Write-Host "======================================" -ForegroundColor Green
Write-Host ""

Write-Host "Verification:" -ForegroundColor Yellow
$allValid = $true

foreach ($nupkgFile in Get-ChildItem $OutputDir -Filter *.nupkg) {
    $packageName = [System.IO.Path]::GetFileNameWithoutExtension($nupkgFile.Name)

    # Strip version suffix: "Zstandard.Native.win-x64.1.0.0" -> "Zstandard.Native.win-x64"
    $pkgId = $packageName -replace '\.\d+\.\d+\.\d+.*$', ''

    $extractPath = Join-Path $ExtractDir $packageName
    Write-Host "  $pkgId`:" -ForegroundColor Cyan

    # Determine package kind
    $isNativeOnly = $pkgId -match '^Zstandard\.Native\.(win|linux|osx)-'
    $isMetaPackage = $pkgId -eq 'Zstandard.Native.Runtimes'
    $isManaged = !$isNativeOnly -and !$isMetaPackage

    if ($isManaged) {
        # Managed package: expect README and managed DLL
        $readme = Get-ChildItem $extractPath -Recurse -Filter README.nuget.md -ErrorAction SilentlyContinue
        if ($readme) { Write-Host "    [OK] README.nuget.md found" -ForegroundColor Green }
        else { Write-Host "    [MISSING] README.nuget.md not found!" -ForegroundColor Red; $allValid = $false }

        $dll = Get-ChildItem $extractPath -Recurse -Filter "Zstandard.Native.dll"
        if ($dll) { Write-Host "    [OK] Zstandard.Native.dll found in $($dll.Count) TFM(s)" -ForegroundColor Green }
        else { Write-Host "    [MISSING] Zstandard.Native.dll not found!" -ForegroundColor Red; $allValid = $false }
    }
    elseif ($isNativeOnly) {
        # Per-RID package: expect the native binary for its RID
        $rid = $pkgId -replace '^Zstandard\.Native\.', ''
        $natives = Get-ChildItem $extractPath -Recurse -Include libzstd.dll,libzstd.so,libzstd.dylib -ErrorAction SilentlyContinue
        if ($natives) {
            Write-Host "    [OK] Native binary present: $($natives[0].FullName.Substring($extractPath.Length + 1))" -ForegroundColor Green
        } else {
            Write-Host "    [WARN] No native binary for $rid — was it fetched before packing?" -ForegroundColor Yellow
        }
    }
    elseif ($isMetaPackage) {
        # Meta-package: expect nuspec with dependency entries
        $nuspecFile = Get-ChildItem $extractPath -Filter *.nuspec | Select-Object -First 1
        if ($nuspecFile) {
            $deps = Select-String -Path $nuspecFile.FullName -Pattern '<dependency' -Quiet
            if ($deps) { Write-Host "    [OK] nuspec contains <dependency> entries" -ForegroundColor Green }
            else { Write-Host "    [MISSING] nuspec has no <dependency> entries!" -ForegroundColor Red; $allValid = $false }
        } else {
            Write-Host "    [MISSING] nuspec not found!" -ForegroundColor Red; $allValid = $false
        }
    }
}

Write-Host ""
if ($allValid) { Write-Host "All packages valid!" -ForegroundColor Green }
else { Write-Host "Warning: Some packages have missing files." -ForegroundColor Yellow }

Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  - Core only:         .\NugetPackage.ps1 -Project Core" -ForegroundColor Gray
Write-Host "  - Runtime pkgs only: .\NugetPackage.ps1 -Project Runtimes" -ForegroundColor Gray
Write-Host "  - Build all:         .\NugetPackage.ps1 -Project All" -ForegroundColor Gray
Write-Host "  - Test locally:      dotnet add package Zstandard.Native --source ./test-packages" -ForegroundColor Gray
Write-Host ""
