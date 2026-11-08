# End-to-end check for the built exe: start it headless on a private port,
# hit every read endpoint, exercise the settings write path, then shut it down.
#
#   powershell -ExecutionPolicy Bypass -File tools\smoke-test.ps1
#
# The build output has a localized file name, so the exe is discovered with a
# wildcard instead of being spelled out here (this file stays ASCII-only for
# Windows PowerShell 5.1, which reads BOM-less files as ANSI).

param(
    [string]$Exe = "",
    [int]$Port = 0,
    [int]$Seconds = 25
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Exe) {
    $found = @(Get-ChildItem -Path (Join-Path $root 'bin') -Filter *.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notlike '*.old' })
    if ($found.Count -eq 0) { throw "no exe in bin\ - run tools\build-widget.ps1 first" }
    $Exe = $found[0].FullName
}
if (-not (Test-Path $Exe)) { throw "missing exe: $Exe" }
if ($Port -le 0) { $Port = Get-Random -Minimum 18000 -Maximum 19000 }

$dataDir = Join-Path ([System.IO.Path]::GetTempPath()) ("hw-monitor-smoke-" + $Port)
if (Test-Path $dataDir) { [System.IO.Directory]::Delete($dataDir, $true) }
New-Item -ItemType Directory -Path $dataDir -Force | Out-Null

$base = "http://127.0.0.1:$Port"
$proc = $null
$fail = 0

function Check([string]$Name, [scriptblock]$Body) {
    try {
        & $Body
        "  PASS  $Name"
    } catch {
        "  FAIL  $Name - $($_.Exception.Message)"
        $script:fail++
    }
}

function GetJson([string]$Path) {
    $r = Invoke-WebRequest -UseBasicParsing -TimeoutSec 4 -Uri ($base + $Path)
    return $r.Content | ConvertFrom-Json
}

try {
    $cliArgs = @('--headless', '--panel', '--no-elevate', '--no-save',
        '--port', "$Port", '--autoclose', "$($Seconds + 20)", '--data-dir', $dataDir)
    "starting $Exe on port $Port"
    $proc = Start-Process -FilePath $Exe -ArgumentList $cliArgs -PassThru

    $up = $false
    for ($i = 0; $i -lt 60; $i++) {
        try {
            $ping = GetJson '/api/ping'
            if ($ping.ok) { $up = $true; break }
        } catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $up) { throw "server did not answer on $base within 15 s" }

    Check 'GET /api/ping' {
        $d = GetJson '/api/ping'
        if (-not $d.ok) { throw "ok=false" }
        if ($d.app -ne 'hw-monitor') { throw "unexpected app=$($d.app)" }
    }

    Check 'GET /api/info' {
        $d = GetJson '/api/info'
        if (-not $d.info.cpuName) { throw "no cpuName" }
        # A CI VM usually has no discrete GPU, and the app is expected to
        # degrade instead of failing. So require the field, not a non-empty
        # value; emptiness is a valid answer for gpuName.
        if (-not ($d.info.PSObject.Properties.Name -contains 'gpuName')) { throw "gpuName field missing" }
    }

    Check 'GET /api/snapshot' {
        $d = GetJson '/api/snapshot'
        if ($null -eq $d.sample) { throw "no sample" }
        if ($null -eq $d.sample.cpu) { throw "no cpu block" }
        if ($null -eq $d.settings) { throw "no settings block" }
    }

    Check 'GET /api/settings' {
        $d = GetJson '/api/settings'
        if (-not $d.lang) { throw "no lang" }
        if ($null -eq $d.cpu) { throw "no cpu switch" }
        if ($null -eq $d.proc) { throw "no proc switch" }
    }

    Check 'GET /api/history (live ring)' {
        $d = GetJson '/api/history'
        if ($null -eq $d.samples) { throw "no samples array" }
    }

    Check 'GET /api/history?range=1h (archive)' {
        $d = GetJson '/api/history?range=1h'
        if ($null -eq $d.samples) { throw "no samples array" }
    }

    Check 'POST /api/settings (write path)' {
        $body = '{"key":"lang","value":"en"}'
        $r = Invoke-WebRequest -UseBasicParsing -TimeoutSec 4 -Method Post -Uri ($base + '/api/settings') `
            -ContentType 'application/json' -Body $body
        $d = $r.Content | ConvertFrom-Json
        if ($d.settings.lang -ne 'en') { throw "lang not applied: $($d.settings.lang)" }
    }

    Check 'unknown setting is rejected' {
        $body = '{"key":"nope","value":"1"}'
        try {
            Invoke-WebRequest -UseBasicParsing -TimeoutSec 4 -Method Post -Uri ($base + '/api/settings') `
                -ContentType 'application/json' -Body $body | Out-Null
            throw "expected HTTP 400"
        } catch {
            if ($_.Exception.Response.StatusCode.value__ -ne 400) { throw }
        }
    }
}
finally {
    if ($proc -and -not $proc.HasExited) {
        try { $proc.Kill() } catch { }
    }
    if (Test-Path $dataDir) {
        try { [System.IO.Directory]::Delete($dataDir, $true) } catch { }
    }
}

if ($fail -gt 0) {
    "SMOKE FAILED ($fail check(s))"
    exit 1
}
"SMOKE OK - all endpoints answered"
exit 0
