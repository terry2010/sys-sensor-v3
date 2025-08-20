param(
  [switch]$VerboseOnly
)
# 一键结束 SystemMonitor.Service 后台进程
# 优先使用 logs/service.pid，其次按命令行匹配 dotnet.exe 运行的 SystemMonitor.Service.dll/EXE
# 使用方法：
#   powershell -ExecutionPolicy Bypass -File .\scripts\kill-service.ps1
#   或: ./scripts/kill-service.ps1

$ErrorActionPreference = 'SilentlyContinue'

function Kill-ByPidFile {
  $pidFile = Join-Path $PSScriptRoot '..\logs\service.pid' | Resolve-Path -ErrorAction SilentlyContinue
  if (-not $pidFile) { return $false }
  $svcPid = (Get-Content $pidFile | Select-Object -First 1).Trim()
  if ([string]::IsNullOrWhiteSpace($svcPid)) { return $false }
  if ($VerboseOnly) { Write-Host "[PID] would kill $svcPid"; return $true }
  taskkill /F /PID $svcPid | Out-Null
  Remove-Item $pidFile -ErrorAction SilentlyContinue
  Write-Host "Killed service PID $svcPid (via pid file)."
  return $true
}

function Kill-ByCmdlineMatch {
  $patterns = @(
    'SystemMonitor\.Service\\bin\\Debug\\net8\.0\\SystemMonitor\.Service\.dll',
    'SystemMonitor\.Service\.exe'
  )
  $candidates = Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'dotnet.exe' -or $_.Name -eq 'SystemMonitor.Service.exe' }
  if (-not $candidates) { return $false }

  $targets = @()
  foreach ($p in $candidates) {
    $cmd = $p.CommandLine
    $matched = $false
    foreach ($pat in $patterns) {
      if ($cmd -match $pat) { $matched = $true; break }
    }
    if ($matched) { $targets += $p }
  }

  if (-not $targets) { return $false }

  $targets | Select-Object ProcessId, Name, CommandLine | ForEach-Object {
    if ($VerboseOnly) {
      Write-Host "[CMDLINE] would kill $($_.ProcessId)"
    } else {
      taskkill /F /PID $_.ProcessId | Out-Null
      Write-Host "Killed service PID $($_.ProcessId) (via cmdline match)."
    }
  }
  return $true
}

$k1 = Kill-ByPidFile
$k2 = $false
if (-not $k1) { $k2 = Kill-ByCmdlineMatch }
if (-not $k1 -and -not $k2) { Write-Host 'No matching SystemMonitor.Service process found.' }
