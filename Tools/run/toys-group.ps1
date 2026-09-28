#requires -Version 5.1
<#
.SYNOPSIS
    Group While We Wait evidence run: 3–6 windowed clients (development accounts 0..N-1 from the project's seed file)
    sign in, join one convoy (client 0 creates it and shares the code through a file under Builds/), and play the
    convoy's hosted tables — Greenlight, Cap Clash, Pit-Crew, Canvas — each seeing every other client's actions
    (FrontEndApp.ToyTourGroup).

.DESCRIPTION
    Automation, not a human playtest. Loopback only: the tables are hosted by the control plane on 127.0.0.1:5080; no
    game server and no other port is opened. Screenshots: Builds/Screenshots/toys-group. Raw logs stay under Builds/
    (git-ignored: they contain local paths).
#>
param([ValidateRange(3, 6)][int]$Humans = 4, [int]$TimeoutSeconds = 900)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $repo
$exe = Join-Path $repo 'Builds\Game\NightSignal.exe'
if (-not (Test-Path $exe)) { throw "Game build not found: $exe (build it first)" }
try { Invoke-RestMethod -Uri 'http://127.0.0.1:5080/healthz' -TimeoutSec 5 | Out-Null }
catch { throw 'Control plane is not running on 127.0.0.1:5080 (run Tools/run/start-control-plane.ps1).' }

$logs = Join-Path $repo 'Builds\NetRuns\toys-group'
New-Item -ItemType Directory -Force $logs | Out-Null
$shots = Join-Path $repo 'Builds\Screenshots\toys-group'
New-Item -ItemType Directory -Force $shots | Out-Null
Remove-Item (Join-Path $shots '*.png') -ErrorAction SilentlyContinue

$clients = @()
for ($i = 0; $i -lt $Humans; $i++) {
    $a = @('-nsToyTourGroup', "$i", "$Humans", '-nsDevAccount', "$i",
        '-screen-fullscreen', '0', '-screen-width', '960', '-screen-height', '540',
        '-nsPrefsFolder', "`"Builds/NetRuns/toys-group/prefs-c$i`"",
        '-logFile', "`"$logs\c$i.log`"")
    $clients += @{ n = "c$i"; p = (Start-Process -FilePath $exe -PassThru -WorkingDirectory $repo -ArgumentList $a) }
}
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline -and ($clients | Where-Object { -not $_.p.HasExited }).Count -gt 0) { Start-Sleep -Seconds 2 }
foreach ($c in $clients) {
    if (-not $c.p.HasExited) { Stop-Process -Id $c.p.Id -Force; Write-Output "$($c.n): TIMEOUT (killed)" } else { Write-Output "$($c.n): exit $($c.p.ExitCode)" }
}
Select-String -Path (Get-ChildItem $logs -Filter 'c*.log' | ForEach-Object { $_.FullName }) -Pattern 'NightSignal.ToyGroup' | ForEach-Object { $_.Line }
