param(
    [string]$ServiceName = "SysSensorV3",
    [string]$DisplayName = "System Sensor V3",
    [string]$BinaryPath = "",
    [switch]$Remove,
    [switch]$Start,
    [switch]$Stop
)

$ErrorActionPreference = "Stop"

function Test-Admin {
    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "install-service.ps1 需要以管理员身份运行"
    }
}

function Log([string]$msg) { Write-Host ("install-service: {0}" -f $msg) }

Test-Admin

if ($Remove) {
    Log "停止并删除服务 $ServiceName"
    sc.exe stop $ServiceName | Out-Null 2>&1
    sc.exe delete $ServiceName | Out-Null
    Log "已删除（若不存在则忽略）"
    exit 0
}

if ($Stop) {
    Log "停止服务 $ServiceName"
    sc.exe stop $ServiceName
    exit 0
}

if ($Start) {
    Log "启动服务 $ServiceName"
    sc.exe start $ServiceName
    exit 0
}

if (-not $BinaryPath -or -not (Test-Path $BinaryPath)) {
    throw "请通过 -BinaryPath 指定 SystemMonitor.Service 可执行文件路径"
}

# 以本地服务账户运行，按需改为自定义账户
$bin = '"' + $BinaryPath + '"'

# 如果已存在则更新 bin 路径
$exists = (sc.exe query $ServiceName | Select-String "SERVICE_NAME" -Quiet)
if ($exists) {
    Log "服务已存在，更新配置 $ServiceName"
    sc.exe config $ServiceName binPath= $bin DisplayName= "$DisplayName" start= auto
} else {
    Log "创建服务 $ServiceName"
    sc.exe create $ServiceName binPath= $bin DisplayName= "$DisplayName" start= auto
}

Log "Done. You can use -Start/-Stop to control the service."
