<#
  Smoke 测试脚本：启动客户端用例调用 hello/snapshot/burst_subscribe/query_history，并采集日志与报告。
  约定：
  - 需先运行 .\scripts\dev.ps1（或已手动启动服务与前端）。
  - 输出：logs/client-smoke-<timestamp>.log 与 artifacts/test-results/smoke-<timestamp>.txt
#>

$ErrorActionPreference = 'Continue'

# 统一 UTF-8，避免日志乱码
chcp 65001 1>$null 2>$null
[Console]::InputEncoding  = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

# 统一以字符串路径解析根目录，避免 PathInfo 参与字符串拼接导致空参数
$rootPathInfo = Resolve-Path -LiteralPath (Join-Path -Path $PSScriptRoot -ChildPath '..')
$root = $rootPathInfo.Path
Set-Location -LiteralPath $root

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$logDir = Join-Path -Path $root -ChildPath 'logs'
$reportDir = Join-Path -Path $root -ChildPath 'artifacts\test-results'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
New-Item -ItemType Directory -Force -Path $reportDir | Out-Null

$clientExe = Join-Path $root 'src\SystemMonitor.Client\bin\Debug\net8.0\SystemMonitor.Client.exe'
# 每次执行前强制重建，避免旧二进制残留
Write-Host '[smoke] rebuilding client ...'
if (Test-Path $clientExe) { Remove-Item -Force -ErrorAction SilentlyContinue $clientExe }
dotnet build 'src/SystemMonitor.Client/SystemMonitor.Client.csproj' -c Debug -v minimal --nologo -t:Rebuild | Out-Host

if (-not (Test-Path $clientExe)) {
  Write-Host '[smoke] ERROR: client exe not found after build' -ForegroundColor Red
  exit 1
}

# 运行一组典型调用
$script:buf = [System.Text.StringBuilder]::new()
function Append($s){
  if ($null -eq $script:buf) { $script:buf = [System.Text.StringBuilder]::new() }
  [void]$script:buf.AppendLine([string]$s)
}

Append ("[smoke] start: $stamp")
Append '[smoke] step: hello'
$hello = & $clientExe hello 2>&1
Append ($hello | Out-String)

Append '[smoke] step: snapshot'
$snap = & $clientExe snapshot 2>&1
Append ($snap | Out-String)

Append '[smoke] step: set_config base_interval_ms=1000 cpu=300 memory=1200'
$cfg = & $clientExe set_config --base-interval-ms 1000 --module cpu=300 --module memory=1200 2>&1
Append ($cfg | Out-String)

Append '[smoke] step: burst_subscribe(200ms, 3s)'
$burst = & $clientExe burst_subscribe --interval-ms 200 --ttl-ms 3000 2>&1
Append ($burst | Out-String)

Append '[smoke] step: query_history last 3s'
$now = [int64]([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())
$from = $now - 3000
$qh = & $clientExe query_history --from-ts $from --to-ts 0 --modules cpu --modules memory --step-ms 500 2>&1
Append ($qh | Out-String)

# 写入日志与报告（加强健壮性：避免空路径）
$clientLog = [System.IO.Path]::Combine([string]$logDir, ("client-smoke-" + [string]$stamp + ".log"))
$report    = [System.IO.Path]::Combine([string]$reportDir, ("smoke-" + [string]$stamp + ".txt"))

if ([string]::IsNullOrWhiteSpace($clientLog)) { throw "clientLog path is null/empty" }
if ([string]::IsNullOrWhiteSpace($report))    { throw "report path is null/empty" }

$null = New-Item -ItemType Directory -Force -Path ([System.IO.Path]::GetDirectoryName($clientLog))
$null = New-Item -ItemType Directory -Force -Path ([System.IO.Path]::GetDirectoryName($report))

$script:buf.ToString() | Out-File -FilePath $clientLog -Encoding utf8
$script:buf.ToString() | Out-File -FilePath $report -Encoding utf8

Write-Host "[smoke] done -> $clientLog`n[smoke] report -> $report"
