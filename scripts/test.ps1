param(
    [string]$Configuration = "Release",
    [string]$ResultsDir = "artifacts/test-results"
)

$ErrorActionPreference = "Stop"

function Log([string]$msg) { Write-Host "[test] $msg" }

$root = (Resolve-Path (Join-Path $PSScriptRoot ".." )).Path
Log "root=$root"
$cwd = (Get-Location).Path
Log "cwd=$cwd"
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

  # 前端 Vitest（生成覆盖率 HTML 报告并自动打开）
  $fe1 = [System.IO.Path]::Combine($root, 'frontend')
  $fe2 = [System.IO.Path]::Combine($cwd, 'frontend')
  $fe = if ($fe1 -and ($fe1.Trim() -ne '') -and (Test-Path -LiteralPath $fe1)) { $fe1 } elseif ($fe2 -and ($fe2.Trim() -ne '') -and (Test-Path -LiteralPath $fe2)) { $fe2 } else { $null }
  Log "frontend dir: $fe"
  if ($fe) {
    Log "frontend: npm run test:report"
    Push-Location $fe
    # 若 node_modules 不存在则安装依赖
    if (-not (Test-Path -LiteralPath (Join-Path $fe 'node_modules'))) {
      $lockFile = Join-Path $fe 'package-lock.json'
      if (Test-Path -LiteralPath $lockFile) {
        Log "frontend: installing deps via npm ci"
        npm ci
      } else {
        Log "frontend: installing deps via npm install (no package-lock.json)"
        npm install
      }
    }
    npm run test:report
    $covDir = Resolve-Path (Join-Path $fe '../artifacts/test-results/frontend/coverage') -ErrorAction SilentlyContinue
    if (-not $covDir) { $covDir = Resolve-Path (Join-Path $fe 'coverage') -ErrorAction SilentlyContinue }
    if ($covDir) {
      $covIndex = Join-Path -Path $covDir.Path -ChildPath 'index.html'
      if (Test-Path -LiteralPath $covIndex) {
        Log "frontend: open coverage report $covIndex"
        Start-Process $covIndex | Out-Null
      } else {
        Log "frontend: coverage index not found at $covIndex"
      }
    } else {
      Log "frontend: coverage directory not found"
    }
    Pop-Location
  } else {
    Log "frontend: skip (path not found) -> candidates: $fe1 ; $fe2"
  }

  # Rust cargo test（Tauri 后端）
  $td1 = [System.IO.Path]::Combine($root, 'frontend', 'src-tauri')
  $td2 = [System.IO.Path]::Combine($cwd, 'frontend', 'src-tauri')
  $tauriDir = if ($td1 -and ($td1.Trim() -ne '') -and (Test-Path -LiteralPath $td1)) { $td1 } elseif ($td2 -and ($td2.Trim() -ne '') -and (Test-Path -LiteralPath $td2)) { $td2 } else { $null }
  Log "tauri dir: $tauriDir"
  if ($tauriDir) {
    Log "rust: cargo test"
    Push-Location $tauriDir
    cargo test
    Pop-Location
  } else {
    Log "rust: skip (path not found) -> candidates: $td1 ; $td2"
  }

Log "All tests done. Results at $results"
