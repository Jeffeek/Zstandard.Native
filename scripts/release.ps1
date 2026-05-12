#requires -Version 7.0
<#
.SYNOPSIS
    Compute the release version and tag for Zstandard.Native.

.DESCRIPTION
    Branch policy
    -------------
      * release/v*  -> STABLE release. The version comes from the branch name
                       (e.g. release/v1.4.0 -> 1.4.0). Tag: v1.4.0
      * master      -> PRE-RELEASE. Version = <VersionPrefix>-preview.<run>+<sha>
                       Tag: v<VersionPrefix>-preview.<run>

    All other branches are rejected. The script is idempotent and prints a
    set of GITHUB_OUTPUT lines for downstream workflow steps.

.PARAMETER Branch
    Branch ref (e.g. "master", "release/v1.2.3"). Defaults to $env:GITHUB_REF_NAME.

.PARAMETER RunNumber
    Monotonic build counter. Defaults to $env:GITHUB_RUN_NUMBER.

.PARAMETER Sha
    Commit SHA to embed in pre-release metadata. Defaults to $env:GITHUB_SHA.

.PARAMETER VersionPrefix
    Fallback when computing a pre-release version on master. Read from
    Directory.Build.props if not supplied.
#>

[CmdletBinding()]
param(
    [string]$Branch        = $env:GITHUB_REF_NAME,
    [string]$RunNumber     = $env:GITHUB_RUN_NUMBER,
    [string]$Sha           = $env:GITHUB_SHA,
    [string]$VersionPrefix
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($Branch)) {
    throw "Branch is required (pass -Branch or set GITHUB_REF_NAME)."
}

function Read-VersionPrefixFromProps {
    $props = Join-Path $PSScriptRoot '..\Directory.Build.props'
    if (-not (Test-Path $props)) { throw "Directory.Build.props not found at $props" }
    [xml]$xml = Get-Content -LiteralPath $props
    $node = $xml.Project.PropertyGroup.VersionPrefix | Where-Object { $_ } | Select-Object -First 1
    if (-not $node) { throw "VersionPrefix not found in Directory.Build.props" }
    return [string]$node
}

if ([string]::IsNullOrWhiteSpace($VersionPrefix)) {
    $VersionPrefix = Read-VersionPrefixFromProps
}

$kind   = $null
$prefix = $null
$suffix = $null
$tag    = $null

if ($Branch -match '^release/v(?<v>\d+\.\d+\.\d+(?:-[A-Za-z0-9.\-]+)?)$') {
    $kind   = 'stable'
    $prefix = $Matches.v
    $suffix = ''
    $tag    = "v$prefix"
}
elseif ($Branch -eq 'master') {
    if ([string]::IsNullOrWhiteSpace($RunNumber)) { $RunNumber = '0' }
    $kind   = 'prerelease'
    $prefix = $VersionPrefix
    $shortSha = if ($Sha) { $Sha.Substring(0, [Math]::Min(7, $Sha.Length)) } else { 'local' }
    $suffix = "preview.$RunNumber+$shortSha"
    $tag    = "v$prefix-preview.$RunNumber"
}
else {
    throw "Refusing to release from branch '$Branch'. Allowed: master, release/v<semver>."
}

$fullVersion = if ($suffix) { "$prefix-$suffix" } else { $prefix }

Write-Host "Release kind  : $kind"
Write-Host "Version       : $fullVersion"
Write-Host "VersionPrefix : $prefix"
Write-Host "VersionSuffix : $suffix"
Write-Host "Tag           : $tag"

if ($env:GITHUB_OUTPUT) {
    @(
        "kind=$kind",
        "version=$fullVersion",
        "version_prefix=$prefix",
        "version_suffix=$suffix",
        "tag=$tag",
        "is_stable=$([string]($kind -eq 'stable').ToString().ToLower())"
    ) | Add-Content -LiteralPath $env:GITHUB_OUTPUT
}

[pscustomobject]@{
    Kind          = $kind
    Version       = $fullVersion
    VersionPrefix = $prefix
    VersionSuffix = $suffix
    Tag           = $tag
    IsStable      = ($kind -eq 'stable')
}
