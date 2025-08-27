# 构建便携式发布版本脚本
# 该脚本会构建前后端的单文件可执行程序，用于在任何Windows电脑上运行

param(
    [string]$Configuration = "Release",
    [string]$OutputDir = "artifacts/portable",
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

function Log([string]$msg) { 
    Write-Host "[build-portable] $msg" -ForegroundColor Cyan
}

# 获取项目根目录
$root = (Resolve-Path (Join-Path $PSScriptRoot ".."))
Set-Location $root

# 设置输出目录
$artifacts = Join-Path $root $OutputDir

# 清理旧的构建结果
if ($Clean) {
    Log "Cleaning artifacts at $artifacts"
    if (Test-Path $artifacts) { 
        Remove-Item -Recurse -Force $artifacts 
    }
}

# 创建输出目录
if (-not (Test-Path $artifacts)) { 
    New-Item -ItemType Directory -Force -Path $artifacts | Out-Null 
}

Log "开始构建便携式发布版本..."

# 1. 构建后端服务（单文件发布）
Log "构建后端服务..."
$backendProject = "src/SystemMonitor.Service/SystemMonitor.Service.csproj"
$backendOutput = Join-Path $artifacts "backend"

# 确保输出目录存在
if (-not (Test-Path $backendOutput)) { 
    New-Item -ItemType Directory -Force -Path $backendOutput | Out-Null 
}

# 使用dotnet publish构建单文件可执行程序
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
    throw "后端服务构建失败，退出代码: $LASTEXITCODE" 
}

# 2. 构建前端应用
Log "构建前端应用..."
$frontendDir = Join-Path $root "frontend"
$frontendOutput = Join-Path $artifacts "frontend"

# 确保输出目录存在
if (-not (Test-Path $frontendOutput)) { 
    New-Item -ItemType Directory -Force -Path $frontendOutput | Out-Null 
}

# 进入前端目录并构建
Push-Location $frontendDir

try {
    # 使用Tauri构建单文件可执行程序
    Log "使用Tauri构建前端应用..."
    & npm run tauri:build
    if ($LASTEXITCODE -ne 0) { 
        throw "前端应用构建失败，退出代码: $LASTEXITCODE" 
    }
    
    # 复制构建结果到输出目录
    $tauriTarget = Join-Path $frontendDir ".tauri-target/release/sys-sensor-v3-app.exe"
    if (Test-Path $tauriTarget) {
        Copy-Item -Path $tauriTarget -Destination $frontendOutput
        Log "前端应用已复制到 $frontendOutput"
    } else {
        throw "未找到Tauri构建输出文件: $tauriTarget"
    }
} finally {
    Pop-Location
}

# 3. 复制启动脚本
Log "复制启动脚本..."
$startScript = Join-Path $PSScriptRoot "start-portable.ps1"
if (Test-Path $startScript) {
    Copy-Item -Path $startScript -Destination $artifacts
} else {
    Log "警告: 未找到启动脚本 $startScript"
}

# 4. 创建版本信息文件
Log "创建版本信息文件..."
$versionInfo = @{
    BuildTime = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
    Configuration = $Configuration
    BackendExecutable = "SystemMonitor.Service.exe"
    FrontendExecutable = "sys-sensor-v3-app.exe"
    StartScript = "start-portable.ps1"
} | ConvertTo-Json

$versionInfo | Out-File -FilePath (Join-Path $artifacts "version.json") -Encoding UTF8

Log "便携式发布版本构建完成!"
Log "输出目录: $artifacts"
Log "包含文件:"
Get-ChildItem -Path $artifacts -Recurse | ForEach-Object {
    Log "  $($_.FullName.Replace($artifacts, ''))"
}