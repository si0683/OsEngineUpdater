<#
    OsEngine updater  (GitHub: AlexWan/OsEngine)
    ------------------------------------------------------------------
    Official-only rule: only files that exist in the official repo are
    ever written or deleted. Your own files/folders are never touched.

    Profile (UpdateOsEngine.config.json -> "Profile"):
        "Prebuilt"    you use the shipped engine (bin/Debug/OsEngine.exe)
                      and your code lives in bin/Debug/Custom/... . bin IS
                      updated (engine binaries stay current). No rebuild.
        "FromSource"  you compile the solution yourself and build custom
                      code into the engine. bin is protected; rebuild runs.

    Simple commands (double-click):
        1_Update_Now.cmd    update this copy now (fast, full 1:1 engine)
        2_Auto_On.cmd       turn on automatic daily update (Task Scheduler)
        3_Auto_Off.cmd      turn off automatic update
        4_Auto_Status.cmd   show the scheduled task status
        5_New_Copy.cmd      create a fresh copy from the archive

    Advanced (direct):
        powershell -ExecutionPolicy Bypass -File UpdateOsEngine.ps1 [switches]
        -Mirror  -FullSync  -Bootstrap  -CheckOnly  -NoBuild  -Watch
        -Silent  -TargetDir <path>
#>
#requires -version 5.1
[CmdletBinding()]
param(
    [switch]$FullSync,
    [switch]$Mirror,
    [switch]$Bootstrap,
    [switch]$CheckOnly,
    [switch]$NoBuild,
    [switch]$Watch,
    [switch]$Silent,
    [switch]$Force,
    [string]$TargetDir = ''
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ProgressPreference = 'SilentlyContinue'

$scriptDir  = $PSScriptRoot
$configPath = Join-Path $scriptDir 'UpdateOsEngine.config.json'

function Write-Log([string]$m, $color = 'Gray') {
    if ($Silent) { Write-Host $m } else { Write-Host $m -ForegroundColor $color }
    if ($script:logPath) { try { Add-Content -LiteralPath $script:logPath -Value ("[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $m) -ErrorAction SilentlyContinue } catch {} }
}
function Info($m) { Write-Log $m 'Cyan' }
function Ok($m)   { Write-Log $m 'Green' }
function Warn($m) { Write-Log $m 'Yellow' }
function Fail($m) { Write-Log $m 'Red' }

# ----------------------------- config -----------------------------
$defaults = [pscustomobject]@{
    Owner   = 'AlexWan'
    Repo    = 'OsEngine'
    Branch  = 'master'
    GitHubToken = ''
    Profile = 'Prebuilt'
    AutoMode = 'Mirror'
    AutoIntervalMinutes = 1440
    UserProtectedPaths = @()
    BuildAfterUpdate = $null
}
if (-not (Test-Path -LiteralPath $configPath)) {
    ($defaults | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $configPath -Encoding UTF8
    Ok "Created config: $configPath"
}
$cfg = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json

# profile -> protected paths / rebuild
$profile = 'Prebuilt'
if ($cfg.Profile) { $profile = [string]$cfg.Profile }

$binProtected = ($profile -eq 'FromSource')
$script:buildAfter = ($profile -eq 'FromSource')
if (($cfg.PSObject.Properties.Name -contains 'BuildAfterUpdate') -and ($null -ne $cfg.BuildAfterUpdate)) {
    $script:buildAfter = [bool]$cfg.BuildAfterUpdate
}

$script:intervalMinutes = 1440
if ($cfg.AutoIntervalMinutes) { $script:intervalMinutes = [int]$cfg.AutoIntervalMinutes }
elseif ($cfg.PSObject.Properties.Name -contains 'WatchIntervalMinutes' -and $cfg.WatchIntervalMinutes) { $script:intervalMinutes = [int]$cfg.WatchIntervalMinutes }
if ($script:intervalMinutes -lt 1) { $script:intervalMinutes = 1 }

$protectGlobs = @('**/obj/**', '**/.vs/**', '**/.git/**', '**/*.user')
if ($binProtected) { $protectGlobs += '**/bin/**' }
# legacy/extra user patterns
if ($cfg.PSObject.Properties.Name -contains 'ProtectedPaths' -and $cfg.ProtectedPaths) { $protectGlobs += @($cfg.ProtectedPaths) }
if ($cfg.UserProtectedPaths) { $protectGlobs += @($cfg.UserProtectedPaths) }

# ------------------------------ github ----------------------------
$headers = @{ 'User-Agent' = 'OsEngine-Updater'; 'Accept' = 'application/vnd.github+json' }
if ($cfg.GitHubToken) { $headers['Authorization'] = "Bearer $($cfg.GitHubToken)" }
$o = $cfg.Owner; $r = $cfg.Repo; $br = $cfg.Branch

# --------------------- repo root / bootstrap ----------------------
function Find-RepoRoot {
    $d = Get-Item -LiteralPath $scriptDir
    while ($d) {
        if (Test-Path -LiteralPath (Join-Path $d.FullName 'project\OsEngine\OsEngine.csproj')) { return $d.FullName }
        $d = $d.Parent
    }
    return $null
}

function Invoke-Bootstrap([string]$target) {
    if (-not (Test-Path -LiteralPath $target)) { New-Item -ItemType Directory -Path $target -Force | Out-Null }
    if (@(Get-ChildItem -LiteralPath $target -Force -ErrorAction SilentlyContinue).Count -gt 0) {
        Warn "Target is not empty: $target (official files overwritten, your extra files kept)."
    }
    $zip = Join-Path $env:TEMP ("osengine_" + [guid]::NewGuid().ToString('N') + ".zip")
    $dst = Join-Path $env:TEMP ("osengine_" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $dst -Force | Out-Null
    Info "Bootstrap: downloading archive from GitHub (big, be patient)..."
    Invoke-WebRequest -Uri "https://codeload.github.com/$o/$r/zip/refs/heads/$br" -OutFile $zip -UseBasicParsing
    Info "Extracting..."
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $dst)
    $root = Get-ChildItem -LiteralPath $dst -Directory | Where-Object { $_.Name -like "$r-*" } | Select-Object -First 1
    if (-not $root) { throw "Archive root not found" }
    Get-ChildItem -LiteralPath $root.FullName -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $target -Recurse -Force
    }
    Get-ChildItem -LiteralPath $target -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $dst -Recurse -Force -ErrorAction SilentlyContinue
    Ok "Bootstrap: files extracted to $target"
}

$repoRoot = Find-RepoRoot
$bootstrapped = $false
if ($Bootstrap -or -not $repoRoot) {
    if (-not $Bootstrap) {
        throw "Repo root not found. Put this updater inside a copy (folder with 'project'), or run 5_New_Copy.cmd."
    }
    $targetRoot = $TargetDir
    if (-not $targetRoot) { $targetRoot = Split-Path $scriptDir -Parent }
    Invoke-Bootstrap $targetRoot
    $repoRoot = $targetRoot
    $bootstrapped = $true
}
if (-not (Test-Path -LiteralPath $repoRoot)) { throw "Repo root does not exist: $repoRoot" }

# per-copy state lives in the repo root (not in the tool folder)
$dataDir = Join-Path $repoRoot '.osengine-updater'
if (-not (Test-Path -LiteralPath $dataDir)) { New-Item -ItemType Directory -Path $dataDir -Force | Out-Null }
$statePath    = Join-Path $dataDir 'state.json'
$manifestPath = Join-Path $dataDir 'official-manifest.txt'
$logPath      = Join-Path $dataDir 'update.log'

if (Test-Path -LiteralPath $logPath) {
    try { if ((Get-Item -LiteralPath $logPath).Length -gt 5MB) { Remove-Item -LiteralPath $logPath -Force } } catch {}
}

# ------------------------ protected paths -------------------------
function Convert-GlobToRegex([string]$glob) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('^')
    $norm = $glob.Replace('\', '/')
    for ($i = 0; $i -lt $norm.Length; $i++) {
        $c = $norm[$i]
        if ($c -eq '*') {
            if (($i + 1) -lt $norm.Length -and $norm[$i + 1] -eq '*') {
                [void]$sb.Append('.*'); $i++
                if (($i + 1) -lt $norm.Length -and $norm[$i + 1] -eq '/') { $i++ }
            }
            else { [void]$sb.Append('[^/]*') }
        }
        elseif ($c -eq '?') { [void]$sb.Append('[^/]') }
        else { [void]$sb.Append([Regex]::Escape([string]$c)) }
    }
    [void]$sb.Append('$')
    return $sb.ToString()
}
$protectedRegex = New-Object System.Collections.Generic.List[regex]
foreach ($g in $protectGlobs) {
    if ($g) { $protectedRegex.Add([regex]::new((Convert-GlobToRegex $g))) }
}
function Is-Protected([string]$rel) {
    $p = $rel.Replace('\', '/')
    foreach ($re in $protectedRegex) { if ($re.IsMatch($p)) { return $true } }
    return $false
}

# ---------------------------- helpers -----------------------------
function Api([string]$url) { Invoke-RestMethod -Uri $url -Headers $headers -Method Get }
function RawUrl([string]$path, [string]$sha) {
    $seg = $path.Split('/') | ForEach-Object { [uri]::EscapeDataString($_) }
    "https://raw.githubusercontent.com/$o/$r/$sha/" + ($seg -join '/')
}
function Get-HeadSha { (Api "https://api.github.com/repos/$o/$r/commits/$br").sha }
function Save-State([string]$sha) {
    ([pscustomobject]@{ LastSha = $sha; UpdatedUtc = (Get-Date).ToUniversalTime().ToString('o') } | ConvertTo-Json) |
        Set-Content -LiteralPath $statePath -Encoding UTF8
}
function Load-State {
    if (Test-Path -LiteralPath $statePath) { Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json } else { $null }
}
function Get-GitBlobSha([string]$file) {
    $bytes = [System.IO.File]::ReadAllBytes($file)
    $header = [System.Text.Encoding]::ASCII.GetBytes("blob " + $bytes.Length + [char]0)
    $sha1 = [System.Security.Cryptography.SHA1]::Create()
    try {
        [void]$sha1.TransformBlock($header, 0, $header.Length, $header, 0)
        [void]$sha1.TransformFinalBlock($bytes, 0, $bytes.Length)
        return (($sha1.Hash | ForEach-Object { $_.ToString('x2') }) -join '')
    }
    finally { $sha1.Dispose() }
}
function Get-OfficialTree([string]$sha) { Api "https://api.github.com/repos/$o/$r/git/trees/$sha`?recursive=1" }
function Get-OfficialSet([string]$sha) {
    try { $tree = Get-OfficialTree $sha }
    catch { Warn "Cannot fetch official tree: $($_.Exception.Message)"; return $null }
    if ($tree.truncated) { Warn "Official tree is truncated; auto-protect disabled for this run."; return $null }
    $set = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($e in $tree.tree) { if ($e.type -eq 'blob' -and $e.path) { [void]$set.Add($e.path) } }
    Info "Official files: $($set.Count)"
    return $set
}
function Load-Manifest {
    if (Test-Path -LiteralPath $manifestPath) { return @(Get-Content -LiteralPath $manifestPath | Where-Object { $_ -ne '' }) }
    return @()
}
function Save-Manifest($officialSet) {
    if (-not $officialSet) { return }
    $managed = @($officialSet | Where-Object { -not (Is-Protected $_) })
    Set-Content -LiteralPath $manifestPath -Value $managed -Encoding UTF8
}
function Remove-EmptyDirs([string]$dir) {
    $root = $repoRoot.TrimEnd('\')
    while ($dir -and $dir.Length -gt $root.Length -and $dir.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
        $hasFiles = @(Get-ChildItem -LiteralPath $dir -Force -ErrorAction SilentlyContinue).Count
        if ($hasFiles -eq 0) { Remove-Item -LiteralPath $dir -Force -ErrorAction SilentlyContinue; $dir = Split-Path $dir -Parent } else { break }
    }
}
function Invoke-Prune($oldManifest, $officialSet) {
    if (-not $officialSet) { return 0 }
    $removed = 0
    foreach ($p in $oldManifest) {
        if (Is-Protected $p) { continue }
        if (-not $officialSet.Contains($p)) {
            $local = Join-Path $repoRoot $p
            if (Test-Path -LiteralPath $local) {
                Remove-Item -LiteralPath $local -Force
                Remove-EmptyDirs (Split-Path $local -Parent)
                Write-Log "  x $p"; $removed++
            }
        }
    }
    if ($removed -gt 0) { Ok "Removed $removed file(s) deleted upstream." }
    return $removed
}

# ----------------------------- mirror -----------------------------
function Invoke-Mirror {
    $head = Get-HeadSha
    Info "Latest commit: $head"
    $tree = Get-OfficialTree $head
    if ($tree.truncated) { throw "Official tree is truncated; mirror is not possible." }
    $blobs = @($tree.tree | Where-Object { $_.type -eq 'blob' })
    Info "Official blobs: $($blobs.Count)"

    $officialSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($b in $blobs) { [void]$officialSet.Add($b.path) }

    $oldManifest = Load-Manifest
    $managed = New-Object System.Collections.Generic.List[string]
    $added = 0; $updated = 0; $same = 0; $skipped = 0

    foreach ($b in $blobs) {
        $path = $b.path
        if (Is-Protected $path) { $skipped++; continue }
        [void]$managed.Add($path)
        $local = Join-Path $repoRoot $path
        if (-not (Test-Path -LiteralPath $local)) {
            $dir = Split-Path $local -Parent
            if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
            Invoke-WebRequest -Uri (RawUrl $path $head) -OutFile $local -UseBasicParsing
            $added++; Write-Log "  + $path"; continue
        }
        if ((Get-GitBlobSha $local) -ne $b.sha) {
            Invoke-WebRequest -Uri (RawUrl $path $head) -OutFile $local -UseBasicParsing
            $updated++; Write-Log "  ~ $path"
        }
        else { $same++ }
    }

    $removed = 0
    foreach ($p in $oldManifest) {
        if (Is-Protected $p) { continue }
        if ($officialSet.Contains($p)) { continue }
        $local = Join-Path $repoRoot $p
        if (Test-Path -LiteralPath $local) {
            Remove-Item -LiteralPath $local -Force
            Remove-EmptyDirs (Split-Path $local -Parent)
            $removed++; Write-Log "  x $p"
        }
    }

    Save-State $head
    Set-Content -LiteralPath $manifestPath -Value $managed -Encoding UTF8
    Ok "Done: added $added, updated $updated, unchanged $same, removed $removed, skipped(protected) $skipped"
}

# ----------------------------- delta ------------------------------
function Invoke-Delta([string]$fromSha, [string]$toSha, $officialSet) {
    Info "Comparing $($fromSha.Substring(0,8)) -> $($toSha.Substring(0,8)) ..."
    try { $cmp = Api "https://api.github.com/repos/$o/$r/compare/$fromSha...$toSha" }
    catch { Warn "Cannot get the change list (range too large?). Re-run with -FullSync."; throw }
    $files = @($cmp.files)
    Info "Changed files in official repo: $($files.Count)"
    $n = 0; $skip = 0
    foreach ($f in $files) {
        $path = $f.filename
        if ($f.status -eq 'renamed' -and $f.previous_filename) {
            $prev = $f.previous_filename
            if (-not (Is-Protected $prev)) {
                $pl = Join-Path $repoRoot $prev
                if (Test-Path -LiteralPath $pl) { Remove-Item -LiteralPath $pl -Force; Remove-EmptyDirs (Split-Path $pl -Parent) }
            }
        }
        if (Is-Protected $path) { $skip++; continue }
        if ($officialSet -and $f.status -ne 'removed' -and -not $officialSet.Contains($path)) { $skip++; continue }
        $local = Join-Path $repoRoot $path
        if ($f.status -eq 'removed') {
            if (Test-Path -LiteralPath $local) { Remove-Item -LiteralPath $local -Force; Remove-EmptyDirs (Split-Path $local -Parent); $n++; Write-Log "  - $path" }
            continue
        }
        $dir = Split-Path $local -Parent
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        Invoke-WebRequest -Uri (RawUrl $path $toSha) -OutFile $local -UseBasicParsing
        $n++; Write-Log "  + $path"
    }
    Ok "Applied $n change(s), skipped(protected) $skip"
}

# --------------------------- full sync ----------------------------
function Invoke-FullSync($officialSet, $oldManifest) {
    if ($officialSet) { Invoke-Prune $oldManifest $officialSet | Out-Null }
    $zip = Join-Path $env:TEMP ("osengine_" + [guid]::NewGuid().ToString('N') + ".zip")
    $dst = Join-Path $env:TEMP ("osengine_" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $dst -Force | Out-Null
    Warn "Full sync: downloading archive (big, be patient)..."
    Invoke-WebRequest -Uri "https://codeload.github.com/$o/$r/zip/refs/heads/$br" -OutFile $zip -UseBasicParsing
    Warn "Extracting..."
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $dst)
    $root = Get-ChildItem -LiteralPath $dst -Directory | Where-Object { $_.Name -like "$r-*" } | Select-Object -First 1
    if (-not $root) { throw "Archive root not found" }
    $copied = 0; $skipped = 0
    Get-ChildItem -LiteralPath $root.FullName -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($root.FullName.Length + 1)
        if (Is-Protected $rel) { $skipped++; return }
        $dest = Join-Path $repoRoot $rel
        $dir = Split-Path $dest -Parent
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        Copy-Item -LiteralPath $_.FullName -Destination $dest -Force
        $copied++
    }
    Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $dst -Recurse -Force -ErrorAction SilentlyContinue
    Ok "Full sync done: copied $copied file(s), skipped(protected) $skipped"
}

function Invoke-Build {
    if ($NoBuild -or -not $script:buildAfter) { return }
    $cmdStr = 'dotnet build "project\OsEngine\OsEngine.csproj" -c Debug'
    if (($cfg.PSObject.Properties.Name -contains 'BuildCommand') -and $cfg.BuildCommand) { $cmdStr = [string]$cfg.BuildCommand }
    Info "Building: $cmdStr"
    Push-Location $repoRoot
    try { & cmd /c $cmdStr } finally { Pop-Location }
}

# --------------------------- one pass -----------------------------
function Invoke-Pass([bool]$apply) {
    $head = Get-HeadSha
    Info "Latest commit: $head"
    $state = Load-State
    $base = $null
    if ($state) { $base = $state.LastSha }

    if (-not $base) {
        Save-State $head
        if ($apply) { Save-Manifest (Get-OfficialSet $head) }
        Warn "First run: baseline saved. Use 1_Update_Now.cmd to sync now."
        return
    }
    if ($base -eq $head) {
        if ($apply -and -not (Test-Path -LiteralPath $manifestPath)) { Save-Manifest (Get-OfficialSet $head) }
        Ok "Already up to date ($head)."
        return
    }
    if (-not $apply) { Warn "Update available: $base -> $head"; return }

    $officialSet = Get-OfficialSet $head
    Invoke-Prune (Load-Manifest) $officialSet | Out-Null
    Invoke-Delta $base $head $officialSet
    Save-State $head
    Save-Manifest $officialSet
    Invoke-Build
    Ok "Updated to $head."
}

# ----------------------------- flow -------------------------------
Info "Repo root: $repoRoot"
Info "Source: $o/$r@$br (profile: $profile, rebuild: $([bool]$script:buildAfter))"

if ($bootstrapped) {
    $head = Get-HeadSha
    Save-State $head
    Save-Manifest (Get-OfficialSet $head)
    Invoke-Build
    Ok "Bootstrap complete."
    if (-not $Watch) { return }
}

# Не обновлять, пока запущен терминал этой же копии: файлы bin заняты, а для профиля
# Prebuilt это ещё и затрёт вашу сборку. Закрой OsEngine — обновление применится в
# следующий прогон. Флаг -Force обходит проверку.
function Test-ThisCopyRunning {
    $procs = Get-Process OsEngine -ErrorAction SilentlyContinue
    if (-not $procs) { return $false }
    foreach ($p in $procs) {
        try {
            if ($p.Path -and $p.Path.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
        } catch {}
    }
    return $false
}

if (Test-ThisCopyRunning) {
    if ($Force) { Warn "OsEngine is running but -Force is set; continuing." }
    else {
        Warn "OsEngine is running from this copy - update skipped. Close the terminal to apply updates (or pass -Force)."
        return
    }
}

if ($Mirror) {
    Invoke-Mirror
    Invoke-Build
    if (-not $Watch) { return }
}

if ($FullSync) {
    $head = Get-HeadSha
    $officialSet = Get-OfficialSet $head
    Invoke-FullSync $officialSet (Load-Manifest)
    Save-State $head
    Save-Manifest $officialSet
    Invoke-Build
    if (-not $Watch) { return }
}

if ($Watch) {
    Info "Auto mode: checking every $($script:intervalMinutes) minute(s). Press Ctrl+C to stop."
    while ($true) {
        try { Invoke-Pass $true } catch { Fail "Auto error: $($_.Exception.Message)" }
        Start-Sleep -Seconds ($script:intervalMinutes * 60)
    }
}
else {
    if (-not ($Mirror -or $FullSync -or $bootstrapped)) { Invoke-Pass (-not $CheckOnly) }
}
