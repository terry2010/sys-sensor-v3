# Build portable release script
# This script builds single-file executables for frontend and backend, 
# which can be run on any Windows computer

param(
    [string]$Configuration = "Release",
    [string]$OutputDir = "artifacts/portable",
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

function Log([string]$msg) { 
    Write-Host "[build-portable] $msg" -ForegroundColor Cyan
}

# Get project root

$root = (Resolve-Path (Join-Path $PSScriptRoot ".."))
Set-Location $root

# Set output directory

$artifacts = Join-Path $root $OutputDir

# Clean previous build outputs

if ($Clean) {
    Log "Cleaning artifacts at $artifacts"

    if (Test-Path $artifacts) { 
        Remove-Item -Recurse -Force $artifacts 
    }
}

# Create output directory

if (-not (Test-Path $artifacts)) { 
    New-Item -ItemType Directory -Force -Path $artifacts | Out-Null 
}

Log "Start building portable release..."

# 1. Build backend service (single-file publish)
Log "Building backend service..."

$backendProject = "src/SystemMonitor.Service/SystemMonitor.Service.csproj"
$backendOutput = Join-Path $artifacts "backend"

# Ensure backend output directory exists

if (-not (Test-Path $backendOutput)) { 
    New-Item -ItemType Directory -Force -Path $backendOutput | Out-Null 
}

# Use dotnet publish to build single-file executable

$publishArgs = @(
    "publish", 
    $backendProject, 
    "-c", $Configuration, 
    "-r", "win-x64", 
    "--self-contained", "true", 
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-o", $backendOutput
)

& dotnet @publishArgs

if ($LASTEXITCODE -ne 0) { 
    throw "Backend build failed, exit code: $LASTEXITCODE" 
}

# 2. Build frontend app
Log "Building frontend app..."

$frontendDir = Join-Path $root "frontend"
$frontendOutput = Join-Path $artifacts "frontend"

# Ensure frontend output directory exists

if (-not (Test-Path $frontendOutput)) { 
    New-Item -ItemType Directory -Force -Path $frontendOutput | Out-Null 
}

# Enter frontend directory and build

Push-Location $frontendDir

try {
    # Build Tauri app
    Log "Building frontend with Tauri..."

    & npm run tauri:build
    if ($LASTEXITCODE -ne 0) { 
        throw "Frontend build failed, exit code: $LASTEXITCODE" 
    }

    # Copy build artifact to output directory

    $tauriTarget = Join-Path $frontendDir ".tauri-target/release/sys-sensor-v3-app.exe"
    if (Test-Path $tauriTarget) {
        Copy-Item -Path $tauriTarget -Destination $frontendOutput
        Log "Frontend app copied to $frontendOutput"

    } else {
        throw "Tauri build artifact not found: $tauriTarget"

    }
} finally {
    Pop-Location
}

# 3. Copy start script (force UTF-8 with BOM to avoid mojibake on Windows PowerShell 5.1)
Log "Copying start script..."
$startScript = Join-Path $PSScriptRoot "start-portable.ps1"
if (Test-Path $startScript) {
    try {
        $startTarget = Join-Path $artifacts "start-portable.ps1"
        $content = Get-Content -LiteralPath $startScript -Raw
        # Windows PowerShell 5.1 uses BOM for UTF-8 by default with -Encoding UTF8
        Set-Content -LiteralPath $startTarget -Value $content -Encoding UTF8
    } catch {
        throw "Failed to copy start script with UTF-8 BOM: $($_.Exception.Message)"
    }
} else {
    Log "Warning: start script not found: $startScript"
}

# 4. Create version info file
Log "Creating version info..."

$versionInfo = @{
    BuildTime = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    Configuration = $Configuration
    BackendExecutable = "SystemMonitor.Service.exe"
    FrontendExecutable = "sys-sensor-v3-app.exe"
    StartScript = "start-portable.ps1"
} | ConvertTo-Json

$versionInfo | Out-File -FilePath (Join-Path $artifacts "version.json") -Encoding UTF8

Log "Portable build completed!"
Log "Output directory: $artifacts"
Log "Included files:"

Get-ChildItem -Path $artifacts -Recurse | ForEach-Object {
    Log "  $($_.FullName.Replace($artifacts, ''))"
}