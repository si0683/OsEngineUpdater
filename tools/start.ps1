# Запуск OsEngine Updater из скачанного архива репозитория («Code → Download ZIP»).
# В архиве нет самой программы: она лежит в GitHub Releases. Скрипт скачивает последнюю версию
# OsEngineUpdater.exe рядом с «Запустить OsEngineUpdater.cmd», сверяет SHA-256 и запускает её.
# Если exe уже скачан — просто запускает (дальше программа обновляет себя сама).
param([switch]$NoStart)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # без этого Invoke-WebRequest в PowerShell 5.1 качает в разы медленнее
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$baseUrl = 'https://github.com/si0683/OsEngineUpdater/releases/latest/download/'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'OsEngineUpdater.exe'

try
{
    if (-not (Test-Path $exe))
    {
        Write-Host 'Скачиваю OsEngineUpdater.exe из GitHub Releases (около 130 МБ)...'
        $tmp = "$exe.download"
        $shaFile = "$exe.sha256"

        Invoke-WebRequest ($baseUrl + 'OsEngineUpdater.exe') -OutFile $tmp -UseBasicParsing
        Invoke-WebRequest ($baseUrl + 'OsEngineUpdater.exe.sha256') -OutFile $shaFile -UseBasicParsing

        $want = ((Get-Content $shaFile -Raw).Trim() -split '\s+')[0]
        $got = (Get-FileHash $tmp -Algorithm SHA256).Hash
        Remove-Item $shaFile -Force

        if ($got -ne $want)
        {
            Remove-Item $tmp -Force
            throw 'Контрольная сумма скачанного файла не совпала. Попробуйте ещё раз.'
        }

        Move-Item $tmp $exe
        Write-Host "Готово: $exe"
    }

    if (-not $NoStart)
    {
        Start-Process -FilePath $exe -WorkingDirectory $root
    }
}
catch
{
    Write-Host ''
    Write-Host ('Не получилось: ' + $_.Exception.Message) -ForegroundColor Red
    Write-Host 'Скачайте программу вручную: https://github.com/si0683/OsEngineUpdater/releases/latest'
    exit 1
}
