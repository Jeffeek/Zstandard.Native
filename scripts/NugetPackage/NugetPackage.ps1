param(
    [Parameter(Mandatory=$false)]
    [string]$Version = "0.0.0-local-test",

    [Parameter(Mandatory=$false)]
    [ValidateSet("Core", "All")]
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

$Projects = @{
    "Core" = "src\Zstandard.Native\Zstandard.Native.csproj"
}

$ProjectsToPack = if ($Project -eq "All") { $Projects.Values } else { @($Projects[$Project]) }

$OutputDir  = ".\test-packages"
$ExtractDir = ".\test-extract"

Write-Host "Version:       $Version" -ForegroundColor Green
Write-Host "Projects:      $Project ($($ProjectsToPack.Count) package(s))" -ForegroundColor Green
Write-Host "Output Dir:    $OutputDir" -ForegroundColor Green
Write-Host "Extract Dir:   $ExtractDir" -ForegroundColor Green
Write-Host ""

if (Test-Path $OutputDir)  { Write-Host "Cleaning previous packages..." -ForegroundColor Yellow; Remove-Item $OutputDir  -Recurse -Force }
if (Test-Path $ExtractDir) { Write-Host "Cleaning previous extracts..." -ForegroundColor Yellow; Remove-Item $ExtractDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDir | Out-Null

$packageCount = 0
foreach ($projectPath in $ProjectsToPack) {
    $packageCount++
    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($projectPath)

    Write-Host ""
    Write-Host "======================================" -ForegroundColor Yellow
    Write-Host "  Building $projectName ($packageCount/$($ProjectsToPack.Count))" -ForegroundColor Yellow
    Write-Host "======================================" -ForegroundColor Yellow
    Write-Host ""

    dotnet pack $projectPath -c Release --output $OutputDir -p:PackageVersion=$Version
    if ($LASTEXITCODE -ne 0) { Write-Host "Error: Package build failed for $projectName!" -ForegroundColor Red; exit 1 }

    Write-Host ""
    Write-Host "$projectName package built successfully!" -ForegroundColor Green
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

    Get-ChildItem $extractPath -Recurse -Include *.dll,*.xml,README.md,libzstd.dll,libzstd.so,libzstd.dylib | ForEach-Object {
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
    $extractPath = Join-Path $ExtractDir $packageName

    Write-Host "  $packageName`:" -ForegroundColor Cyan

    $expectedFiles = @("README.md")
    foreach ($file in $expectedFiles) {
        $found = Get-ChildItem $extractPath -Recurse -Filter $file -ErrorAction SilentlyContinue
        if ($found) {
            Write-Host "    [OK] $file found" -ForegroundColor Green
        } else {
            Write-Host "    [MISSING] $file not found!" -ForegroundColor Red
            $allValid = $false
        }
    }

    $projectName = $packageName -replace '\.\d+\.\d+\.\d+.*$', ''
    $dllName = "$projectName.dll"
    $dllFiles = Get-ChildItem $extractPath -Recurse -Filter $dllName
    if ($dllFiles) {
        Write-Host "    [OK] $dllName found in $($dllFiles.Count) target(s)" -ForegroundColor Green
    } else {
        Write-Host "    [MISSING] $dllName not found!" -ForegroundColor Red
        $allValid = $false
    }

    # Native binary presence (RID-specific; warn rather than fail).
    $natives = Get-ChildItem $extractPath -Recurse -Include libzstd.dll,libzstd.so,libzstd.dylib -ErrorAction SilentlyContinue
    if ($natives) {
        Write-Host "    [OK] Native binaries: $($natives.Count) file(s)" -ForegroundColor Green
        foreach ($n in $natives) {
            Write-Host "         $($n.FullName.Substring($extractPath.Length + 1))" -ForegroundColor Gray
        }
    } else {
        Write-Host "    [WARN] No native libzstd binaries bundled — consumers must supply their own." -ForegroundColor Yellow
    }
}

Write-Host ""
if ($allValid) { Write-Host "All packages valid!" -ForegroundColor Green }
else { Write-Host "Warning: Some packages have missing files." -ForegroundColor Yellow }

Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  - Build specific:    .\NugetPackage.ps1 -Project Core" -ForegroundColor Gray
Write-Host "  - Build all:         .\NugetPackage.ps1 -Project All" -ForegroundColor Gray
Write-Host "  - Test locally:      dotnet add package Zstandard.Native --source ./test-packages" -ForegroundColor Gray
Write-Host ""
