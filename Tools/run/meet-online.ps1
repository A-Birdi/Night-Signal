#requires -Version 5.1
<#
.SYNOPSIS
    Three-client online meet evidence run: three windowed clients (development accounts from the project's seed file)
    sign in through the real screens and join the same public Cedar Lantern Terrace room on the local control plane
    (FrontEndApp.MeetTourOnline): arrivals announced, a guest walks, waves, quick-chats and likes the host's car, the host
    jogs to the boombox and queues a cue the room plays for everyone, one guest leaves (announced "left", the car fades),
    the other drops its connection (announced "disconnected").

.DESCRIPTION
    Automation, not a human playtest. Loopback only: the meet is hosted by the control plane on 127.0.0.1:5080; no game
    server and no other port is opened. Screenshots: Builds/Screenshots/meet-online. Raw logs stay under Builds/
    (git-ignored: they contain local paths).
#>
param([int]$HostAccount = 0, [int]$Guest1Account = 1, [int]$Guest2Account = 2, [int]$TimeoutSeconds = 300,
    # Two friends instead: a convoy meet answered Ready from inside the meet, then a friend's meet by invitation.
    [switch]$Convoy)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $repo
$exe = Join-Path $repo 'Builds\Game\NightSignal.exe'
if (-not (Test-Path $exe)) { throw "Game build not found: $exe (build it first)" }
try { Invoke-RestMethod -Uri 'http://127.0.0.1:5080/healthz' -TimeoutSec 5 | Out-Null }
catch { throw 'Control plane is not running on 127.0.0.1:5080 (run Tools/run/start-control-plane.ps1).' }

$logs = Join-Path $repo 'Builds\NetRuns\meet-online'
New-Item -ItemType Directory -Force $logs | Out-Null
$shots = Join-Path $repo 'Builds\Screenshots\meet-online'
New-Item -ItemType Directory -Force $shots | Out-Null
Remove-Item (Join-Path $shots '*.png') -ErrorAction SilentlyContinue

$tour = if ($Convoy) { '-nsMeetTourConvoy' } else { '-nsMeetTourOnline' }
function Start-Client([string]$role, [int]$account, [int]$x) {
    $a = @($tour, $role, '-nsDevAccount', "$account",
        '-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720', '-monitor', '1',
        '-nsPrefsFolder', "`"Builds/NetRuns/meet-online/prefs-$role`"",
        '-logFile', "`"$logs\$role.log`"")
    Start-Process -FilePath $exe -PassThru -WorkingDirectory $repo -ArgumentList $a
}
$clients = if ($Convoy) {
    @(@{ n = 'host'; p = (Start-Client 'host' $HostAccount 0) }, @{ n = 'guest'; p = (Start-Client 'guest' $Guest1Account 1) })
} else {
    @(@{ n = 'host'; p = (Start-Client 'host' $HostAccount 0) },
      @{ n = 'guest1'; p = (Start-Client 'guest1' $Guest1Account 1) },
      @{ n = 'guest2'; p = (Start-Client 'guest2' $Guest2Account 2) })
}
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline -and ($clients | Where-Object { -not $_.p.HasExited }).Count -gt 0) { Start-Sleep -Seconds 2 }
foreach ($c in $clients) {
    if (-not $c.p.HasExited) { Stop-Process -Id $c.p.Id -Force; Write-Output "$($c.n): TIMEOUT (killed)" } else { Write-Output "$($c.n): exit $($c.p.ExitCode)" }
}
$files = if ($Convoy) { @("$logs\host.log", "$logs\guest.log") } else { @("$logs\host.log", "$logs\guest1.log", "$logs\guest2.log") }
Select-String -Path $files -Pattern 'NightSignal.Meet(Online|Convoy)' | ForEach-Object { $_.Line }
