<#
  简化且兼容的 dev 脚本，避免复杂函数/多层大括号导致的解析问题。
  流程：清理 -> 构建(重试) -> 启动后端 -> 等待命名管道 -> 启动前端 -> 等待 -> 清理
#>

$ErrorActionPreference = 'Continue'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..'))
Set-Location $root

$ServiceProject = 'src/SystemMonitor.Service/SystemMonitor.Service.csproj'
$FrontendDir = 'frontend'
$ServiceExePath = Join-Path $root 'src\SystemMonitor.Service\bin\Debug\net8.0\SystemMonitor.Service.exe'

Write-Host '[dev] cleanup residual processes'
Get-Process rustc,cargo,tauri,node -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
try { taskkill /F /T /IM SystemMonitor.Service.exe | Out-Null } catch { }
try { $procs = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue; foreach ($p in $procs) { if ($p.CommandLine -and $p.CommandLine -like '*SystemMonitor.Service*') { taskkill /PID $p.ProcessId /T /F | Out-Null } } } catch { }

Write-Host '[dev] build backend (retry up to 3)'
$ok = $false
for ($i=1; $i -le 3; $i++) {
  Write-Host ("[dev] build attempt {0}/3" -f $i)
  & dotnet build $ServiceProject -c Debug -v minimal --nologo
  if ($LASTEXITCODE -eq 0) { $ok = $true; break }
  Write-Host '[dev] build failed, retry after releasing locks...'
  try { taskkill /F /T /IM SystemMonitor.Service.exe | Out-Null } catch { }
  try { if (Test-Path $ServiceExePath) { $ps = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue; foreach ($p in $ps) { if ($p.ExecutablePath -and ($p.ExecutablePath -ieq $ServiceExePath)) { taskkill /PID $p.ProcessId /T /F | Out-Null } } } } catch { }
  Start-Sleep -Seconds 2
}
if (-not $ok) { Write-Host '[dev] build failed after retries' -ForegroundColor Red; exit 1 }

Write-Host '[dev] start backend: dotnet run'
$backend = Start-Process -FilePath 'dotnet' -ArgumentList @('run','-c','Debug','--project',$ServiceProject) -PassThru -WindowStyle Hidden

Write-Host '[dev] waiting for RPC pipe: \\.\pipe\sys_sensor_v3.rpc'
function _WaitPipe { param([int]$TimeoutSec = 15); $deadline=(Get-Date).AddSeconds($TimeoutSec); while ((Get-Date) -lt $deadline) { try { $c = New-Object System.IO.Pipes.NamedPipeClientStream '.', 'sys_sensor_v3.rpc', ([System.IO.Pipes.PipeDirection]::InOut), ([System.IO.Pipes.PipeOptions]::Asynchronous); $c.Connect(200); $c.Dispose(); return $true } catch { Start-Sleep -Milliseconds 200 } } return $false }
$ready = _WaitPipe -TimeoutSec 15
if ($ready) { Write-Host '[dev] RPC pipe ready' } else { Write-Host '[dev] WARN: RPC pipe not ready' -ForegroundColor Yellow }

Write-Host '[dev] start frontend'
Push-Location $FrontendDir
if (-not (Test-Path 'node_modules')) { npm install }
$frontend = Start-Process -FilePath 'npm' -ArgumentList @('run','tauri:dev') -PassThru
Pop-Location

Write-Host '[dev] dev started, waiting for frontend exit'
if ($frontend) { try { Wait-Process -Id $frontend.Id } catch { } } else { Start-Sleep -Seconds 1 }

Write-Host '[dev] cleanup'
try { if ($frontend) { taskkill /PID $frontend.Id /T /F | Out-Null } } catch { }
try { if ($backend)  { taskkill /PID $backend.Id  /T /F | Out-Null } } catch { }
Get-Process rustc,cargo,tauri,node -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Write-Host '[dev] done'
