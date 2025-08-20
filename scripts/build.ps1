param(
    [string]$Configuration = "Release",
    [string]$Framework = "net8.0",
    [switch]$Restore,
    [switch]$Pack,
    [string]$ArtifactsDir = "artifacts",
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

function Log([string]$msg) { Write-Host "[build] $msg" }

$solution = Join-Path $PSScriptRoot "..\SysSensorV3.sln"
$root = (Resolve-Path (Join-Path $PSScriptRoot ".."))
$artifacts = Join-Path $root $ArtifactsDir

if ($Clean) {
    Log "Cleaning artifacts at $artifacts"
    if (Test-Path $artifacts) { Remove-Item -Recurse -Force $artifacts }
}

if (-not (Test-Path $artifacts)) { New-Item -ItemType Directory -Force -Path $artifacts | Out-Null }

if ($Restore) {
    Log "dotnet restore"
    dotnet restore "$solution"
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }
}

Log "dotnet build -c $Configuration"
$buildArgs = @("build", "$solution", "-c", $Configuration)
if ($Framework) { $buildArgs += @("-f", $Framework) }
$buildArgs += @("/nologo")

& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

# Collect binaries (service + client + tests)
$targets = @(
    "src/SystemMonitor.Service/bin/$Configuration/$Framework",
    "src/SystemMonitor.Client/bin/$Configuration/$Framework"
)

foreach ($t in $targets) {
    $src = Join-Path $root $t
    if (Test-Path $src) {
        # 目标子目录使用项目名，避免不同项目同为 net8.0 时互相覆盖
        # 路径示例：src/SystemMonitor.Service/bin/Release/net8.0
        # 需要上溯到 src/SystemMonitor.Service 再取 Leaf
        $projectDir = Split-Path (Split-Path (Split-Path (Split-Path $t -Parent) -Parent) -Parent) -Leaf
        $name = $projectDir
        $dest = Join-Path $artifacts $name
        Log "Copy [$projectDir] $src -> $dest"
        if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
        Copy-Item -Recurse -Force $src $dest
    }
}

if ($Pack) {
    Log "Packing zip"
    $stamp = (Get-Date -Format "yyyyMMdd-HHmmss")
    $packSrc = Join-Path $artifacts "_package"
    if (Test-Path $packSrc) { Remove-Item -Recurse -Force $packSrc }
    New-Item -ItemType Directory -Force -Path $packSrc | Out-Null

    # 仅将项目子目录复制到打包源，避免把历史 zip 一起打进去
    Get-ChildItem -Path $artifacts -Directory | Where-Object { $_.Name -ne "_package" } | ForEach-Object {
        $from = $_.FullName
        $to = Join-Path $packSrc $_.Name
        Log "Stage $_ -> $to"
        Copy-Item -Recurse -Force $from $to
    }

    $zip = Join-Path $artifacts ("build-" + $stamp + ".zip")
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($packSrc, $zip)
    Log "Packed -> $zip"
}

Log "Done. Artifacts at $artifacts"
