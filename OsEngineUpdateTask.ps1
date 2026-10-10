<#
    Registers/removes a Windows Scheduled Task that runs the OsEngine
    updater for THIS copy automatically.

    Usage:
        powershell -ExecutionPolicy Bypass -File OsEngineUpdateTask.ps1 -Action Install
        ... -Action Install -IntervalMinutes 30
        ... -Action Status
        ... -Action Remove

    The task name is unique per copy (derived from the folder path),
    so the 5 copies do not clash. Creating a scheduled task may require
    running once as Administrator.
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

# interval: parameter > config > default
$interval = $IntervalMinutes
if ($interval -le 0 -and (Test-Path -LiteralPath $configPath)) {
    try { $interval = [int](Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json).WatchIntervalMinutes } catch {}
}
if ($interval -le 0) { $interval = 15 }

# unique task name per copy
$sha1 = [System.Security.Cryptography.SHA1]::Create()
$hashBytes = $sha1.ComputeHash([Text.Encoding]::UTF8.GetBytes($repoRoot.ToLowerInvariant()))
$hash = -join ($hashBytes[0..3] | ForEach-Object { $_.ToString('x2') })
$taskName = "OsEngineUpdate_$hash"

switch ($Action) {
    'Install' {
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' `
            -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$ps1Path`" -Silent"
        $trigger = New-ScheduledTaskTrigger -AtLogOn
        $once = New-ScheduledTaskTrigger -Once -At (Get-Date) `
            -RepetitionInterval (New-TimeSpan -Minutes $interval) `
            -RepetitionDuration (New-TimeSpan -Days 3650)
        $trigger.Repetition = $once.Repetition
        $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
        $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries `
            -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero)
        Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
            -Principal $principal -Settings $settings -Force `
            -Description "OsEngine updater for $repoRoot" | Out-Null
        Write-Host "Installed scheduled task '$taskName' for $repoRoot (every $interval min + at logon)." -ForegroundColor Green
    }
    'Remove' {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
        Write-Host "Removed scheduled task '$taskName'." -ForegroundColor Green
    }
    'Status' {
        $t = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        if ($t) {
            $i = $t | Get-ScheduledTaskInfo
            Write-Host "Task: $taskName"
            Write-Host "  State       : $($t.State)"
            Write-Host "  LastRunTime : $($i.LastRunTime)"
            Write-Host "  LastResult  : $($i.LastTaskResult)"
            Write-Host "  NextRunTime : $($i.NextRunTime)"
        }
        else { Write-Host "Task '$taskName' is not installed." -ForegroundColor Yellow }
    }
}
