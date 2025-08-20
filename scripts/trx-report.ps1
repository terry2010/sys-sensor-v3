param(
    [Parameter(Mandatory=$true)][string]$TrxPath,
    [string]$OutDir = "artifacts/test-results/report",
    [string]$OutFile = "index.html",
    [ValidateSet('en','zh-CN')][string]$Lang = 'zh-CN',
    [switch]$Open
)

# Normalize paths
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$TrxFull = Resolve-Path -Path $TrxPath -ErrorAction Stop
$OutDirFull = Join-Path -Path (Resolve-Path -Path ".").Path -ChildPath $OutDir
$null = New-Item -ItemType Directory -Force -Path $OutDirFull | Out-Null
$outPath = Join-Path $OutDirFull $OutFile

[xml]$xml = Get-Content -LiteralPath $TrxFull -Raw

# Load i18n resources (external JSON, UTF-8 BOM recommended)
function Get-I18n([string]$lang){
  $res = @{}
  try {
    $candidate = Join-Path $scriptDir ("trx-report.i18n." + $lang + ".json")
    if (-not (Test-Path -LiteralPath $candidate)) {
      if ($lang -ne 'en') { $candidate = Join-Path $scriptDir 'trx-report.i18n.en.json' }
    }
    if (Test-Path -LiteralPath $candidate) {
      $sr = New-Object System.IO.StreamReader($candidate, $true)
      $json = $sr.ReadToEnd()
      $sr.Close()
      $obj = $json | ConvertFrom-Json -ErrorAction Stop
      # Convert PSCustomObject to Hashtable for PS5.1 compatibility
      $ht = @{}
      if ($null -ne $obj) {
        $obj.PSObject.Properties | ForEach-Object { $ht[$_.Name] = $_.Value }
      }
      $res = $ht
    }
  } catch {
    Write-Warning "Failed to load i18n resources for '$lang'. Falling back to English. $_"
    $res = @{}
  }
  return $res
}

$I18N = Get-I18n $Lang
function T([string]$k, [string]$def){
  try {
    if ($I18N -is [hashtable]) {
      if ($I18N.ContainsKey($k)) { return [string]$I18N[$k] }
      else { return $def }
    } else {
      $prop = $I18N.PSObject.Properties[$k]
      if ($null -ne $prop) { return [string]$prop.Value }
      else { return $def }
    }
  } catch { return $def }
}

# Env check: recommend PowerShell 7 for better UTF-8 handling
try {
  $isPSCore = ($PSVersionTable.PSEdition -eq 'Core') -or ($PSVersionTable.PSVersion.Major -ge 6)
} catch { $isPSCore = $false }
if (-not $isPSCore) {
  Write-Warning "Running on Windows PowerShell. For best UTF-8 support, prefer 'pwsh' (PowerShell 7). If you stay on Windows PowerShell, save this script as UTF-8 with BOM."
}

function Get-Attr {
  param($node, $name)
  if ($null -eq $node) { return $null }
  if ($null -ne $node.Attributes[$name]) { return $node.Attributes[$name].Value }
  return $null
}
function Get-AttrInt {
  param($node, $name, [int]$default = 0)
  $v = Get-Attr $node $name
  if ([string]::IsNullOrWhiteSpace($v)) { return $default }
  $n = 0
  if ([int]::TryParse($v, [ref]$n)) { return $n }
  return $default
}

# Use XPath + local-name() to ignore namespaces
$times = $xml.SelectSingleNode("/*[local-name()='TestRun']/*[local-name()='Times']")
$start = Get-Attr $times 'start'
$finish = Get-Attr $times 'finish'

$summaryNode = $xml.SelectSingleNode("/*[local-name()='TestRun']/*[local-name()='ResultSummary']")
$counters = $xml.SelectSingleNode("/*[local-name()='TestRun']/*[local-name()='ResultSummary']/*[local-name()='Counters']")
$outcome = Get-Attr $summaryNode 'outcome'
$passed = Get-AttrInt $counters 'passed' 0
$failed = Get-AttrInt $counters 'failed' 0
$total = Get-AttrInt $counters 'total' ($passed + $failed)
$skipped = [Math]::Max(0, $total - $passed - $failed)
$duration = if ($start -and $finish) { ([datetime]$finish - [datetime]$start).ToString() } else { '' }

# Collect test results
$unitTests = @{}
foreach ($ut in $xml.SelectNodes("/*[local-name()='TestRun']/*[local-name()='TestDefinitions']/*[local-name()='UnitTest']")) {
  $id = Get-Attr $ut 'id'
  if (-not $id) { $id = Get-Attr $ut 'Id' }
  $name = Get-Attr $ut 'name'
  if (-not $name) { $name = Get-Attr $ut 'Name' }
  $tm = $ut.SelectSingleNode("*[local-name()='TestMethod']")
  $className = if ($tm) { (Get-Attr $tm 'className') } else { '' }
  $unitTests[$id] = @{ Name = $name; ClassName = $className }
}

$testResults = @()
foreach ($tr in $xml.SelectNodes("/*[local-name()='TestRun']/*[local-name()='Results']/*[local-name()='UnitTestResult']")) {
  $testId = Get-Attr $tr 'testId'
  $meta = $unitTests[$testId]
  if ($null -eq $meta) { $meta = @{ Name = (Get-Attr $tr 'testName'); ClassName = '' } }
  $r = [pscustomobject]@{
    Name = $meta.Name
    ClassName = $meta.ClassName
    Outcome = (Get-Attr $tr 'outcome')
    Duration = (Get-Attr $tr 'duration')
    StartTime = (Get-Attr $tr 'startTime')
    EndTime = (Get-Attr $tr 'endTime')
    ErrorMessage = ''
    ErrorStackTrace = ''
  }
  $out = $tr.SelectSingleNode("*[local-name()='Output']/*[local-name()='ErrorInfo']")
  if ($out) {
    $msg = $out.SelectSingleNode("*[local-name()='Message']")
    $st = $out.SelectSingleNode("*[local-name()='StackTrace']")
    if ($msg) { $r.ErrorMessage = $msg.InnerText }
    if ($st) { $r.ErrorStackTrace = $st.InnerText }
  }
  $testResults += $r
}

# HTML template
$html = @"
<!DOCTYPE html>
<html lang="$Lang">
<head>
<meta charset="utf-8" />
<meta http-equiv="Content-Type" content="text/html; charset=utf-8" />
<title>$(T 'title_prefix' 'Test Report') - $($total) $(T 'label_total' 'Total') ($(T 'label_passed' 'Passed') $($passed), $(T 'label_failed' 'Failed') $($failed), $(T 'label_skipped' 'Skipped') $($skipped))</title>
<style>
body{font-family:-apple-system,BlinkMacSystemFont,Segoe UI,Roboto,Helvetica,Arial,sans-serif;margin:24px;color:#222}
.badge{display:inline-block;padding:2px 8px;border-radius:999px;font-size:12px}
.badge-pass{background:#e6ffed;color:#0366d6}
.badge-fail{background:#ffeef0;color:#d73a49}
.badge-skip{background:#f1f8ff;color:#6a737d}
.summary{display:flex;gap:16px;align-items:center;margin-bottom:16px}
.card{border:1px solid #e1e4e8;border-radius:6px;padding:12px}
.table{width:100%;border-collapse:collapse;margin-top:12px}
.table th,.table td{border:1px solid #e1e4e8;padding:8px;text-align:left;vertical-align:top}
.table th{background:#f6f8fa}
.out-pass{color:#22863a}
.out-fail{color:#cb2431;font-weight:600}
pre{background:#f6f8fa;padding:8px;border-radius:6px;white-space:pre-wrap}
</style>
</head>
<body>
  <h1>$(T 'title_prefix' 'Test Report')</h1>
  <div class="summary">
    <div class="card"><div>$(T 'label_total' 'Total')</div><div style="font-size:20px">$total</div></div>
    <div class="card"><div>$(T 'label_passed' 'Passed')</div><div style="font-size:20px" class="out-pass">$passed</div></div>
    <div class="card"><div>$(T 'label_failed' 'Failed')</div><div style="font-size:20px" class="out-fail">$failed</div></div>
    <div class="card"><div>$(T 'label_skipped' 'Skipped')</div><div style="font-size:20px">$skipped</div></div>
  </div>
  <div>$(T 'label_time_range' 'Time Range'): $start -> $finish ($(T 'label_duration' 'Duration'): $duration)</div>
  <h2>$(T 'label_test_details' 'Test Details')</h2>
  <table class="table">
    <thead>
      <tr><th>$(T 'col_outcome' 'Outcome')</th><th>$(T 'col_test' 'Test')</th><th>$(T 'col_class' 'Class')</th><th>$(T 'col_duration' 'Duration')</th><th>$(T 'col_error' 'Error')</th></tr>
    </thead>
    <tbody>
"@

function HtmlEnc([string]$s){
  if ($null -eq $s) { return '' }
  return [System.Net.WebUtility]::HtmlEncode($s)
}

foreach ($r in $testResults | Sort-Object { $_.Outcome -ne 'Passed' } ) {
  $badgeClass = if ($r.Outcome -eq 'Passed') { 'badge badge-pass' } elseif ($r.Outcome -eq 'Failed') { 'badge badge-fail' } else { 'badge badge-skip' }
  $outText = switch ($r.Outcome) {
    'Passed' { T 'outcome_passed' 'Passed' }
    'Failed' { T 'outcome_failed' 'Failed' }
    'Skipped' { T 'outcome_skipped' 'Skipped' }
    default { $r.Outcome }
  }
  $err = ''
  if ($r.Outcome -ne 'Passed' -and $r.ErrorMessage) {
    $safeMsg = HtmlEnc $r.ErrorMessage
    $safeSt = HtmlEnc $r.ErrorStackTrace
    $err = "<div><div><b>$safeMsg</b></div><pre>$safeSt</pre></div>"
  }
  $html += "<tr><td><span class='$badgeClass'>$(HtmlEnc $outText)</span></td><td>$(HtmlEnc $r.Name)</td><td>$(HtmlEnc $r.ClassName)</td><td>$(HtmlEnc $r.Duration)</td><td>$err</td></tr>"
}

$html += @"
    </tbody>
  </table>
</body>
</html>
"@

# Always write with UTF-8 BOM to maximize compatibility across PowerShell/browser versions
$utf8bom = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllText($outPath, $html, $utf8bom)
Write-Host "Report generated:" $outPath
if ($Open) { Start-Process $outPath }

