<#
    Installs/removes the Windows Scheduled Task for automatic updates
    of THIS OsEngine copy. Mode and interval come from
    UpdateOsEngine.config.json (AutoMode / AutoIntervalMinutes).

    Task name is unique per copy (derived from the folder path).

    Usage:
        OsEngineUpdateTask.ps1 -Action Install [-IntervalMinutes N]
        OsEngineUpdateTask.ps1 -Action Status
        OsEngineUpdateTask.ps1 -Action Remove
#>
#requires -version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Install', 'Remove', 'Status')][string]$Action = 'Install',
    [int]$IntervalMinutes = 0
)

$ErrorActionPreference = 'Stop'
$scriptDir  = $PSScriptRoot
$configPath = Join-Path $scriptDir 'UpdateOsEngine.config.json'
$ps1Path    = Join-Path $scriptDir 'UpdateOsEngine.ps1'

function Find-RepoRoot {
    $d = Get-Item -LiteralPath $scriptDir
    while ($d) {
        if (Test-Path -LiteralPath (Join-Path $d.FullName 'project\OsEngine\OsEngine.csproj')) { return $d.FullName }
        $d = $d.Parent
    }
    return $null
}
$repoRoot = Find-RepoRoot
if (-not $repoRoot) { throw "Repo root not found. Put this updater inside an OsEngine copy." }

$cfg = $null
if (Test-Path -LiteralPath $configPath) { $cfg = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json }

$interval = $IntervalMinutes
if ($interval -le 0 -and $cfg) {
    if ($cfg.AutoIntervalMinutes) { $interval = [int]$cfg.AutoIntervalMinutes }
    elseif ($cfg.PSObject.Properties.Name -contains 'WatchIntervalMinutes' -and $cfg.WatchIntervalMinutes) { $interval = [int]$cfg.WatchIntervalMinutes }
}
if ($interval -le 0) { $interval = 1440 }

$mode = 'Mirror'
if ($cfg -and $cfg.AutoMode) { $mode = [string]$cfg.AutoMode }
$modeArg = ''
if ($mode -eq 'Mirror') { $modeArg = ' -Mirror' }

$sha1 = [System.Security.Cryptography.SHA1]::Create()
$hashBytes = $sha1.ComputeHash([Text.Encoding]::UTF8.GetBytes($repoRoot.ToLowerInvariant()))
$hash = -join ($hashBytes[0..3] | ForEach-Object { $_.ToString('x2') })
$taskName = "OsEngineUpdate_$hash"

switch ($Action) {
    'Install' {
        $taskAction = New-ScheduledTaskAction -Execute 'powershell.exe' `
            -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$ps1Path`" -Silent$modeArg"
        $trigger = New-ScheduledTaskTrigger -AtLogOn
        $once = New-ScheduledTaskTrigger -Once -At (Get-Date) `
            -RepetitionInterval (New-TimeSpan -Minutes $interval) `
            -RepetitionDuration (New-TimeSpan -Days 3650)
        $trigger.Repetition = $once.Repetition
        $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
        $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries `
            -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero)
        Register-ScheduledTask -TaskName $taskName -Action $taskAction -Trigger $trigger `
            -Principal $principal -Settings $settings -Force `
            -Description "OsEngine auto update ($mode) for $repoRoot" | Out-Null
        Write-Host "Auto update ON: task '$taskName' ($mode) every $interval min + at logon." -ForegroundColor Green
    }
    'Remove' {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
        Write-Host "Auto update OFF: task '$taskName' removed." -ForegroundColor Green
    }
    'Status' {
        $t = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        if ($t) {
            $i = $t | Get-ScheduledTaskInfo
            Write-Host "Task      : $taskName"
            Write-Host "  State   : $($t.State)"
            Write-Host "  LastRun : $($i.LastRunTime)"
            Write-Host "  Result  : $($i.LastTaskResult)"
            Write-Host "  NextRun : $($i.NextRunTime)"
        }
        else { Write-Host "Auto update is OFF (task '$taskName' not installed)." -ForegroundColor Yellow }
    }
}
