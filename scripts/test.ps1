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
