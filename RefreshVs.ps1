<#
    Refresh Visual Studio so newly added files/folders under
    project\OsEngine\Robots and project\OsEngine\Indicators become visible.

    The OsEngine project is SDK-style, so those .cs files are ALREADY part of
    the build automatically - Visual Studio just does not always re-scan the
    folder when files are dropped in from Explorer. This tool:
      1) touches OsEngine.csproj / OsEngine.sln (VS notices the change),
      2) clears the .vs cache (only if Visual Studio is closed),
      3) optionally rebuilds (-Build).

    Usage:
        RefreshVs.ps1
        RefreshVs.ps1 -Build
        RefreshVs.ps1 -KeepCache    (do not delete the .vs folder)
#>
#requires -version 5.1
[CmdletBinding()]
param(
    [switch]$Build,
    [switch]$KeepCache
)

$ErrorActionPreference = 'Stop'
$scriptDir = $PSScriptRoot

function Find-RepoRoot {
    $d = Get-Item -LiteralPath $scriptDir
    while ($d) {
        if (Test-Path -LiteralPath (Join-Path $d.FullName 'project\OsEngine\OsEngine.csproj')) { return $d.FullName }
        $d = $d.Parent
    }
    return $null
}
$repoRoot = Find-RepoRoot
if (-not $repoRoot) { throw "Repo root not found. Put this tool inside an OsEngine copy." }

$projectDir = Join-Path $repoRoot 'project'
$proj = Join-Path $projectDir 'OsEngine\OsEngine.csproj'
$sln  = Join-Path $projectDir 'OsEngine.sln'
$vsDirs = @((Join-Path $projectDir '.vs'), (Join-Path $projectDir 'OsEngine\.vs'))

Write-Host "Repo root: $repoRoot"

# Report how many Compile items the project sees under Robots/Indicators,
# so you can confirm the files are part of the build (not a VS-only issue).
try {
    $items = & dotnet msbuild $proj -getItem:Compile -v:quiet 2>$null
    $txt = ($items | Out-String)
    $robots = ([regex]::Matches($txt, '"Identity": "(Robots|Indicators)\\\\?')).Count
    Write-Host "Project Compile items lookups for Robots/Indicators: $robots"
}
catch { Write-Host "Compile-items check skipped: $($_.Exception.Message)" -ForegroundColor Yellow }

# 1) touch project + solution so VS re-evaluates the project on next open/refresh
foreach ($f in @($proj, $sln)) {
    if (Test-Path -LiteralPath $f) { (Get-Item -LiteralPath $f).LastWriteTime = Get-Date; Write-Host "Touched: $f" }
}

# 2) clear the Visual Studio cache (only meaningful/possible when VS is closed)
$devenv = Get-Process devenv -ErrorAction SilentlyContinue
if ($devenv) {
    Write-Host "Visual Studio is running. Close it and run this tool again to clear the .vs cache." -ForegroundColor Yellow
}
elseif (-not $KeepCache) {
    foreach ($d in $vsDirs) {
        if (Test-Path -LiteralPath $d) {
            try { Remove-Item -LiteralPath $d -Recurse -Force; Write-Host "Cleared VS cache: $d" }
            catch { Write-Host "Could not remove $d : $($_.Exception.Message)" -ForegroundColor Yellow }
        }
    }
}

# 3) optional rebuild
if ($Build) {
    Write-Host "Building..."
    & dotnet build $proj -v:minimal
}

Write-Host ""
Write-Host "Done. Open project\OsEngine.sln - files in Robots/Indicators will be visible." -ForegroundColor Green
Write-Host "(If Visual Studio is already open: right-click the OsEngine project -> Reload Project.)"
