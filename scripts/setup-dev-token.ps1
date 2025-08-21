<#
  生成开发用 token 并写入 %ProgramData%\sys-sensor-v3\token，设置尽量收紧的 ACL。
  幂等：若已存在且非空则跳过（可加 -Force 覆盖）。
#>
param(
  [switch]$Force
)
$ErrorActionPreference = 'Stop'

# UTF-8 控制台
chcp 65001 1>$null 2>$null
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$progData = [Environment]::GetFolderPath('CommonApplicationData')
$dir = Join-Path $progData 'sys-sensor-v3'
$tokenFile = Join-Path $dir 'token'

Write-Host "[token] target: $tokenFile"
New-Item -ItemType Directory -Force -Path $dir | Out-Null

if ((Test-Path $tokenFile) -and -not $Force) {
  $existing = (Get-Content -Raw -Encoding UTF8 $tokenFile).Trim()
  if ($existing.Length -gt 0) { Write-Host '[token] exists, skip (use -Force to overwrite)'; exit 0 }
}

# 生成 128-bit 随机数并以 Base64Url 编码
$bytes = New-Object byte[] 16
[System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
$b64 = [Convert]::ToBase64String($bytes)
$base64Url = $b64.TrimEnd('=') -replace '\+', '-' -replace '/', '_'

$base64Url | Out-File -FilePath $tokenFile -Encoding UTF8 -Force

# 设置 ACL：SYSTEM、Administrators、当前用户
try {
  $acl = New-Object System.Security.AccessControl.FileSecurity
  $inherit = [System.Security.AccessControl.InheritanceFlags]::None
  $prop = [System.Security.AccessControl.PropagationFlags]::None
  $allow = [System.Security.AccessControl.AccessControlType]::Allow

  $ruleSystem = New-Object System.Security.AccessControl.FileSystemAccessRule('SYSTEM','FullControl',$inherit,$prop,$allow)
  $ruleAdmins = New-Object System.Security.AccessControl.FileSystemAccessRule('Administrators','FullControl',$inherit,$prop,$allow)
  $curr = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
  $ruleUser = New-Object System.Security.AccessControl.FileSystemAccessRule($curr,'FullControl',$inherit,$prop,$allow)

  $acl.SetAccessRuleProtection($true,$false)
  $acl.AddAccessRule($ruleSystem)
  $acl.AddAccessRule($ruleAdmins)
  $acl.AddAccessRule($ruleUser)
  Set-Acl -Path $tokenFile -AclObject $acl
  Write-Host '[token] ACL set'
} catch {
  Write-Warning "[token] set ACL failed: $_"
}

Write-Host '[token] done'
