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
    server and no other port is opened, except with -Convoy -Race: one dedicated game server bound to 127.0.0.1 (UDP
    7792, the same guarded endpoint as ui-tour-social.ps1 -Race) for the race started from the meet.
    Screenshots: Builds/Screenshots/meet-online (meet-convoy for -Convoy). Raw logs stay under Builds/
    (git-ignored: they contain local paths).
#>
param([int]$HostAccount = 0, [int]$Guest1Account = 1, [int]$Guest2Account = 2, [int]$TimeoutSeconds = 300,
    # Two friends instead: a convoy meet answered Ready from inside the meet, then a friend's meet by invitation.
    [switch]$Convoy,
    # With -Convoy: the leader then starts the event from the meet (starts a dedicated game server), both race, and both
    # return to the meet afterwards.
    [switch]$Race, [int]$Port = 7792,
    # Public meet: guest2 leaves for the Garage, repaints and comes back; the host must draw the new livery.
    [switch]$Livery,
    # Latency on every client's control channel, 'delayMs,jitterMs' each way (e.g. '80,20'): the meet under impairment.
    [string]$ImpairControl = '',
    # String bounds of the meet (Gate 4/5): every client at Text 150 % / HUD 130 % in its own fresh prefs folder, audited.
    [switch]$BoundsAudit,
    # Addendum 04: loopback unless a separately authorized LAN test passes -AllowLan with its addresses.
    [string]$BindHost = '127.0.0.1', [string]$PublicHost = '127.0.0.1', [switch]$AllowLan)

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

if ($Race -and -not $Convoy) { throw '-Race needs -Convoy.' }
if ($Livery -and $Convoy) { throw '-Livery is a public-meet leg (no -Convoy).' }
$tour = if ($Convoy) { '-nsMeetTourConvoy' } else { '-nsMeetTourOnline' }
function Prefs([string]$role) {
    if (-not $BoundsAudit) { return "Builds/NetRuns/meet-online/prefs-$role" }
    $p = "Builds/NetRuns/meet-online/prefs-bounds-$role"
    Remove-Item -Recurse -Force $p -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $p | Out-Null
    @{ Schema = 1; TextScale = 1.5; HudScale = 1.3 } | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $p 'driving.json')
    return $p
}
function Start-Client([string]$role, [int]$account, [int]$x) {
    $a = @($tour, $role, '-nsDevAccount', "$account",
        '-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720', '-monitor', '1',
        '-nsPrefsFolder', "`"$(Prefs $role)`"",
        '-logFile', "`"$logs\$role.log`"")
    if ($Race) { $a += '-nsMeetTourConvoyRace' }
    if ($Livery) { $a += '-nsMeetTourLivery' }
    if ($ImpairControl) { $a += @('-nsImpairControl', $ImpairControl) }
    if ($BoundsAudit) { $a += '-nsBoundsAudit' }
    Start-Process -FilePath $exe -PassThru -WorkingDirectory $repo -ArgumentList $a
}
$server = $null
if ($Race) {
    if ($TimeoutSeconds -lt 900) { $TimeoutSeconds = 900 }
    Import-Module (Join-Path $PSScriptRoot 'NetGuard.psm1') -Force
    $endpoint = Resolve-ServerEndpoint -BindHost $BindHost -PublicHost $PublicHost -Port $Port -AllowLan:$AllowLan -Executable $exe
    Write-Output "server endpoint: bind $($endpoint.bindHost) advertise $($endpoint.publicHost) udp $($endpoint.port) ($($endpoint.classification))"
    $server = Start-Process -FilePath $exe -PassThru -WorkingDirectory $repo -ArgumentList (@(
        '-batchmode', '-nographics', '-nsServer', '-nsExitAfterMatch',
        '-nsEvidence', 'Builds/NetRuns/meet-online/evidence', '-logFile', "`"$logs\server.log`"") + (Get-ServerArgs $endpoint))
    Start-Sleep -Seconds 4
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
if ($server) {
    Start-Sleep -Seconds 3
    if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force; Write-Output 'server: stopped' } else { Write-Output "server: exit $($server.ExitCode)" }
    Write-Output ("udp $Port released: " + (Wait-PortReleased -Port $Port))
}
$files = if ($Convoy) { @("$logs\host.log", "$logs\guest.log") } else { @("$logs\host.log", "$logs\guest1.log", "$logs\guest2.log") }
Select-String -Path $files -Pattern 'NightSignal.Meet(Online|Convoy)|NightSignal.Bounds]' | ForEach-Object { $_.Line }
