param(
    [string]$Configuration = "Release",
    [string]$ResultsDir = "artifacts/test-results"
)

$ErrorActionPreference = "Stop"

function Log([string]$msg) { Write-Host "[test] $msg" }

$root = (Resolve-Path (Join-Path $PSScriptRoot ".." )).Path
$results = Join-Path -Path $root -ChildPath $ResultsDir
if (-not (Test-Path $results)) { New-Item -ItemType Directory -Force -Path $results | Out-Null }

Log "dotnet test -c $Configuration"
$cmd = @(
  "test", "-c", $Configuration,
  "--logger", "trx;LogFileName=TestResults.trx",
  "--results-directory", $results,
  "/nologo"
)
& dotnet @cmd

# 生成并打开 HTML 测试报告（基于 TRX），避免使用 try/catch 以兼容 WinPS 5.1 解析
$trx = Join-Path -Path $results -ChildPath "TestResults.trx"
if (Test-Path -LiteralPath $trx) {
  $reportScript = Join-Path -Path $PSScriptRoot -ChildPath "trx-report.ps1"
  if (Test-Path -LiteralPath $reportScript) {
    Log "generate html report from $trx"
    & $reportScript -TrxPath $trx -OutDir (Join-Path $results 'report') -OutFile 'index.html' -Lang 'zh-CN' -Open
  } else {
    Log "report script not found: $reportScript"
  }
} else {
  Log "no TRX found at $trx"
}

# 前端 Vitest
$fe = Join-Path -Path $root -ChildPath "frontend"
if ([string]::IsNullOrWhiteSpace($fe)) {
  Log "frontend: skip (path empty)"
} elseif (Test-Path -LiteralPath $fe) {
  Log "frontend: npm run test"
  Push-Location $fe
  npm run test
  Pop-Location
} else {
  Log "frontend: skip (path not found)"
}

# Rust cargo test（Tauri 后端）
$tauriDir = Join-Path -Path $root -ChildPath "frontend/src-tauri"
if ([string]::IsNullOrWhiteSpace($tauriDir)) {
  Log "rust: skip (path empty)"
} elseif (Test-Path -LiteralPath $tauriDir) {
  Log "rust: cargo test"
  Push-Location $tauriDir
  cargo test
  Pop-Location
} else {
  Log "rust: skip (path not found)"
}

Log "All tests done. Results at $results"
