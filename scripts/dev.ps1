param(
  [switch]$UseInstalledService = $false,
  [string]$ServiceProject = "src/SystemMonitor.Service/SystemMonitor.Service.csproj",
  [string]$FrontendDir = "frontend"
)

$ErrorActionPreference = 'Continue'

Write-Host '[dev] start dev helper'

$root = (Resolve-Path (Join-Path $PSScriptRoot ".."))
Push-Location $root

# 清理占用进程
Get-Process rustc,cargo,tauri,node -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# 启动后端
$backend = $null
if ($UseInstalledService) {
  Write-Host '[dev] using installed service, skip dotnet run'
}
else {
  Write-Host '[dev] start backend: dotnet run -c Debug --project ' $ServiceProject
  $backend = Start-Process -FilePath "dotnet" -ArgumentList @("run","-c","Debug","--project",$ServiceProject) -PassThru -WindowStyle Hidden
}

# 启动前端（Tauri Dev）
Write-Host '[dev] enter frontend dir: ' $FrontendDir
Push-Location $FrontendDir
if (-not (Test-Path "node_modules")) {
  Write-Host '[dev] npm install'
  npm install
}

Write-Host '[dev] start tauri: npm run tauri:dev'
$frontend = Start-Process -FilePath "npm" -ArgumentList @("run","tauri:dev") -PassThru
Pop-Location

Write-Host '[dev] dev started, press Ctrl+C to stop tauri'
if ($frontend) {
  Wait-Process -Id $frontend.Id 2>$null
}

Pop-Location
