param(
    [string]$Configuration = "Release",
    [string]$ResultsDir = "artifacts/test-results"
)

$ErrorActionPreference = "Stop"

function Log([string]$msg) { Write-Host "[test] $msg" }

$root = (Resolve-Path (Join-Path $PSScriptRoot ".."))
$results = Join-Path $root $ResultsDir
if (-not (Test-Path $results)) { New-Item -ItemType Directory -Force -Path $results | Out-Null }

Log "dotnet test -c $Configuration"
$cmd = @(
  "test", "-c", $Configuration,
  "--logger", "trx;LogFileName=TestResults.trx",
  "--results-directory", $results,
  "/nologo"
)
& dotnet @cmd

Log "Done. Results at $results"
