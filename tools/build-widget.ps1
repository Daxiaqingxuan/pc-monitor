# Compile desktop\Widget.cs into bin\CPU-GPU监视器.exe with the .NET Framework
# compiler that ships with Windows (csc.exe), so no SDK is needed.
#
#   powershell -ExecutionPolicy Bypass -File tools\build-widget.ps1
#
# /title: writes the Win32 FileDescription - this is what Task Manager shows
# for the process, so the widget reads as "CPU-GPU 监视器" there.
#
# NOTE: this file carries a UTF-8 BOM on purpose - PowerShell 5.1 reads BOM-less
# files with the legacy ANSI codepage, which would garble the Chinese exe name.

param([switch]$Quiet, [switch]$AllowRunning, [string]$Out)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$srcDir = Join-Path $root 'desktop'
$outDir = Join-Path $root 'bin'
$out = if ($Out) { $Out } else { Join-Path $outDir 'CPU-GPU监视器.exe' }

# Every .cs under desktop\ is compiled together; Panel.cs holds the web
# dashboard that used to be server.py.
$sources = @(Get-ChildItem -Path $srcDir -Filter *.cs | Sort-Object Name | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) { throw "missing source: $srcDir\*.cs" }
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

# Running instance would lock the file, and a half-written exe is worse than
# a clear message. -AllowRunning is for the rename-aside dance: Windows lets
# you rename a running exe (only the image name entry is locked), so the old
# build is moved to *.old first and the new exe lands next to it; the running
# instance keeps using the old image until it is restarted.
# Ask PSBoundParameters rather than testing $Out itself: an unset [string]
# parameter is "", which is falsy, but so is a caller that passed nothing at
# all - the distinction has to come from what was actually bound.
$targetIsDefault = -not $PSBoundParameters.ContainsKey('Out')
$running = $null
if ($targetIsDefault) { $running = Get-Process -Name 'CPU-GPU监视器' -ErrorAction SilentlyContinue }
if ($running -and -not $AllowRunning) { throw "CPU-GPU监视器.exe is running (pid $($running.Id -join ', ')) - close it first" }

# Replacing the image while the widget runs: Windows lets a running exe be
# renamed (only the loaded image name is pinned), so the previous build steps
# aside to *.old and the new one takes its place. The running process keeps
# executing the old image until it is restarted.
if (Test-Path $out) {
    $aside = $out + '.old'
    if (Test-Path $aside) { Remove-Item $aside -Force -ErrorAction SilentlyContinue }
    try {
        Rename-Item -Path $out -NewName (Split-Path $aside -Leaf) -Force -ErrorAction Stop
    } catch {
        Remove-Item $out -Force -ErrorAction Stop
    }
}

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw "csc.exe not found (is .NET Framework 4.x installed?)" }

$cscArgs = @(
    '/nologo', '/optimize+', '/target:winexe', '/platform:anycpu',
    ('/out:' + $out),
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll'
) + $sources

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "build failed (csc exit $LASTEXITCODE)" }
if (-not (Test-Path $out)) { throw "build produced no output" }

$size = (Get-Item $out).Length
if (-not $Quiet) { "OK  {0}  ({1:N0} bytes)" -f $out, $size }
