param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [Parameter(Mandatory=$false)]
    [switch]$SkipTests
)

# Navigate to repository root (2 levels up from scripts/Build/)
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location (Join-Path $ScriptDir "../..")

$ErrorActionPreference = "Stop"

Write-Host "======================================" -ForegroundColor Cyan
Write-Host "  Zstandard.Native Build Script" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""

Write-Host "Configuration: $Configuration" -ForegroundColor Green
Write-Host "Skip Tests:    $SkipTests" -ForegroundColor Green
Write-Host ""

Write-Host "======================================" -ForegroundColor Yellow
Write-Host "  Step 1/4: Cleaning solution..." -ForegroundColor Yellow
Write-Host "======================================" -ForegroundColor Yellow
Write-Host ""

dotnet clean Zstandard.Native.sln -c $Configuration
if ($LASTEXITCODE -ne 0) { Write-Host "Error: Clean failed!" -ForegroundColor Red; exit 1 }

Write-Host ""
Write-Host "Clean completed successfully!" -ForegroundColor Green
Write-Host ""

Write-Host "======================================" -ForegroundColor Yellow
Write-Host "  Step 2/4: Restoring dependencies..." -ForegroundColor Yellow
Write-Host "======================================" -ForegroundColor Yellow
Write-Host ""

dotnet restore Zstandard.Native.sln
if ($LASTEXITCODE -ne 0) { Write-Host "Error: Restore failed!" -ForegroundColor Red; exit 1 }

Write-Host ""
Write-Host "Restore completed successfully!" -ForegroundColor Green
Write-Host ""

Write-Host "======================================" -ForegroundColor Yellow
Write-Host "  Step 3/4: Building solution..." -ForegroundColor Yellow
Write-Host "======================================" -ForegroundColor Yellow
Write-Host ""

dotnet build Zstandard.Native.sln -c $Configuration --no-restore -warnaserror
if ($LASTEXITCODE -ne 0) { Write-Host "Error: Build failed!" -ForegroundColor Red; exit 1 }

Write-Host ""
Write-Host "Build completed successfully!" -ForegroundColor Green
Write-Host ""

if (-not $SkipTests) {
    Write-Host "======================================" -ForegroundColor Yellow
    Write-Host "  Step 4/4: Running tests..." -ForegroundColor Yellow
    Write-Host "======================================" -ForegroundColor Yellow
    Write-Host ""

    dotnet test tests/Zstandard.Native.Tests/Zstandard.Native.Tests.csproj -c $Configuration --no-build --verbosity normal
    if ($LASTEXITCODE -ne 0) { Write-Host "Error: Tests failed!" -ForegroundColor Red; exit 1 }

    Write-Host ""
    Write-Host "Tests completed successfully!" -ForegroundColor Green
    Write-Host ""
} else {
    Write-Host "======================================" -ForegroundColor Gray
    Write-Host "  Step 4/4: Tests skipped" -ForegroundColor Gray
    Write-Host "======================================" -ForegroundColor Gray
    Write-Host ""
}

Write-Host "======================================" -ForegroundColor Green
Write-Host "  Build process completed!" -ForegroundColor Green
Write-Host "======================================" -ForegroundColor Green
Write-Host ""
Write-Host "Configuration: $Configuration" -ForegroundColor Cyan
Write-Host "Tests run:     $(-not $SkipTests)" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  - Build + test:    .\Build.ps1" -ForegroundColor Gray
Write-Host "  - Build release:   .\Build.ps1 -Configuration Release" -ForegroundColor Gray
Write-Host "  - Skip tests:      .\Build.ps1 -SkipTests" -ForegroundColor Gray
Write-Host "  - Create package:  .\..\NugetPackage\NugetPackage.ps1" -ForegroundColor Gray
Write-Host ""
