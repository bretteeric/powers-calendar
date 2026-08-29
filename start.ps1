<#
.SYNOPSIS
    啟動 OfficeCal（個人行事曆與會議廳預約系統）開發站台。

.DESCRIPTION
    啟動前會依序確認三件最常導致啟動失敗的事：
      1. 找得到 dotnet SDK
      2. SQL Server LocalDB 執行個體已啟動（本專案的資料庫）
      3. 目標連接埠沒有被舊的站台佔用

    第 3 點特別重要：殘留的 dotnet 行程會鎖住 bin/ 目錄，
    導致下一次建置以「檔案使用中」失敗，而錯誤訊息不會告訴你真正的原因。

.PARAMETER Port
    HTTP 連接埠，預設 5088（對應 launchSettings.json 的 http 設定檔）。

.PARAMETER Https
    改用 https 設定檔（https://localhost:7063 + http://localhost:5088）。

.PARAMETER NoBuild
    跳過建置直接啟動。程式碼沒有變動時可加速啟動。

.PARAMETER Force
    若目標連接埠已被佔用，直接停止佔用的行程後繼續，不再詢問。

.PARAMETER OpenBrowser
    啟動後自動開啟瀏覽器。

.EXAMPLE
    .\start.ps1
    以預設連接埠 5088 建置並啟動。

.EXAMPLE
    .\start.ps1 -Force -NoBuild
    停掉殘留的舊站台，跳過建置直接啟動（重跑驗證時最常用）。
#>
[CmdletBinding()]
param(
    [int]$Port = 5088,
    [switch]$Https,
    [switch]$NoBuild,
    [switch]$Force,
    [switch]$OpenBrowser
)

$ErrorActionPreference = 'Stop'

# 本腳本的訊息含中文；輸出被重新導向時主控台預設編碼會產生亂碼，明確指定 UTF-8。
$OutputEncoding = [System.Text.Encoding]::UTF8
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$root       = Split-Path -Parent $MyInvocation.MyCommand.Path
$webProject = Join-Path $root 'src\OfficeCal.Web'
$localDb    = 'MSSQLLocalDB'

function Write-Step($message) { Write-Host "==> $message" -ForegroundColor Cyan }
function Write-Ok($message)   { Write-Host "    $message" -ForegroundColor DarkGray }
function Write-Warn($message) { Write-Host "    $message" -ForegroundColor Yellow }

# --- 1. dotnet SDK -----------------------------------------------------------
Write-Step '檢查 dotnet SDK'
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw '找不到 dotnet。請先安裝 .NET 10 SDK：https://dotnet.microsoft.com/download'
}
Write-Ok "dotnet $(dotnet --version)"

if (-not (Test-Path $webProject)) {
    throw "找不到 Web 專案：$webProject（請確認這個腳本放在方案根目錄）"
}

# --- 2. LocalDB --------------------------------------------------------------
Write-Step "檢查 SQL Server LocalDB（$localDb）"
$sqlLocalDb = Get-Command sqllocaldb -ErrorAction SilentlyContinue
if (-not $sqlLocalDb) {
    Write-Warn 'sqllocaldb 不在 PATH 上，略過檢查。若啟動後出現連線錯誤，請確認已安裝 SQL Server Express LocalDB。'
}
else {
    $info = & sqllocaldb info $localDb 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        Write-Warn "找不到執行個體 $localDb，嘗試建立…"
        & sqllocaldb create $localDb | Out-Null
    }
    if ($info -notmatch 'Running') {
        Write-Ok "啟動 $localDb…"
        & sqllocaldb start $localDb | Out-Null
    }
    Write-Ok "$localDb 已就緒"
}

# --- 3. 連接埠佔用 -----------------------------------------------------------
Write-Step "檢查連接埠 $Port"
$listeners = @()
if (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue) {
    $listeners = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
}

if ($listeners.Count -gt 0) {
    $owners = $listeners | Select-Object -ExpandProperty OwningProcess -Unique
    foreach ($processId in $owners) {
        $proc = Get-Process -Id $processId -ErrorAction SilentlyContinue
        $name = if ($proc) { $proc.ProcessName } else { '未知行程' }
        Write-Warn "連接埠 $Port 已被佔用：$name (PID $processId)"

        $shouldStop = $Force
        if (-not $Force) {
            $answer = Read-Host "    停止該行程並繼續？(y/N)"
            $shouldStop = ($answer -eq 'y' -or $answer -eq 'Y')
        }

        if (-not $shouldStop) {
            throw "連接埠 $Port 被佔用，已中止。可改用 -Port 指定其他連接埠，或加上 -Force 自動停止。"
        }

        Stop-Process -Id $processId -Force
        Write-Ok "已停止 PID $processId"
    }
    Start-Sleep -Milliseconds 500
}
else {
    Write-Ok "連接埠 $Port 可用"
}

# --- 4. 啟動 -----------------------------------------------------------------
$profileName = if ($Https) { 'https' } else { 'http' }
$baseUrl     = "http://localhost:$Port"

$runArgs = @('run', '--project', $webProject, '--launch-profile', $profileName)
if ($NoBuild) { $runArgs += '--no-build' }
if (-not $Https) { $runArgs += @('--urls', $baseUrl) }

Write-Step "啟動站台（設定檔：$profileName）"
Write-Ok "網址：$baseUrl"
Write-Ok '預設管理員：A0001 / Admin@12345'
Write-Host '    按 Ctrl+C 停止' -ForegroundColor DarkGray
Write-Host ''

if ($OpenBrowser) {
    Start-Job -ScriptBlock {
        param($url)
        Start-Sleep -Seconds 4
        Start-Process $url
    } -ArgumentList $baseUrl | Out-Null
}

& dotnet @runArgs
exit $LASTEXITCODE
