$ErrorActionPreference = 'SilentlyContinue'

# Collect possible Windows SDK Include roots
$roots = @()

# Registry 64-bit
try {
  $kr = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -ErrorAction Stop).KitsRoot10
  if ($kr) { $roots += (Join-Path $kr 'Include') }
} catch {}

# Registry 32-bit
try {
  $kr32 = (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots' -ErrorAction Stop).KitsRoot10
  if ($kr32) { $roots += (Join-Path $kr32 'Include') }
} catch {}

# Common default paths
$roots += @(
  'C:\Program Files (x86)\Windows Kits\10\Include',
  'C:\Program Files\Windows Kits\10\Include'
)

$roots = $roots | Where-Object { Test-Path $_ } | Select-Object -Unique
if (-not $roots) {
  Write-Output "[ERR] No Windows SDK Include root found."
  return
}

# Enumerate version subdirs and pick the newest
$incDirs = foreach ($r in $roots) {
  Get-ChildItem -Path $r -Directory -ErrorAction SilentlyContinue
}

if (-not $incDirs) {
  Write-Output "[ERR] No Include version subdirectories found."
  return
}

# Sort by version if possible
$incDirs = $incDirs | Sort-Object @{
  Expression = {
    try { [version]$_.Name } catch { [version]'0.0.0.0' }
  }
} -Descending

$incDir = $incDirs | Select-Object -First 1
$incPath = $incDir.FullName

$winioctl = Join-Path $incPath 'shared\winioctl.h'
$ntddstor = Join-Path $incPath 'um\ntddstor.h'

Write-Output "[INFO] SDK Include version: $($incDir.Name)"
Write-Output "[PATH] winioctl.h: $winioctl"
Write-Output "[PATH] ntddstor.h: $ntddstor"

function Show-HeadAndFind {
  param(
    [string]$filePath,
    [string]$token = 'IOCTL_STORAGE_PROTOCOL_COMMAND'
  )
  if (Test-Path $filePath) {
    Write-Output ""
    Write-Output "===== $(Split-Path $filePath -Leaf) first 60 lines ====="
    Get-Content -Path $filePath -TotalCount 60 | ForEach-Object { $_ }

    Write-Output ""
    Write-Output "===== $(Split-Path $filePath -Leaf): $token (with 2 lines context) ====="
    $matches = Select-String -Path $filePath -Pattern $token -CaseSensitive:$false -Context 0,2
    if ($matches) {
      foreach ($m in $matches) {
        Write-Output ("Line {0}: {1}" -f $m.LineNumber, $m.Line)
        foreach ($pc in $m.Context.PostContext) { Write-Output $pc }
      }
    } else {
      Write-Output "[WARN] Token not found: $token"
    }
  } else {
    Write-Output "[WARN] File not found: $filePath"
  }
}

Show-HeadAndFind -filePath $winioctl -token 'IOCTL_STORAGE_PROTOCOL_COMMAND'
Show-HeadAndFind -filePath $ntddstor -token 'IOCTL_STORAGE_PROTOCOL_COMMAND'