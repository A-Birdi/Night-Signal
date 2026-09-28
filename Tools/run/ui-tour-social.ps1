#requires -Version 5.1
<#
.SYNOPSIS
    Two-client social evidence run: two windowed clients (development accounts from the project's seed file) drive the
    REAL Friends and Convoy screens — usernames, friend request by @username, accept, convoy invitation from the friend
    list, join from the invitation (FrontEndApp.UiTourSocial). Requires only the local control plane.

.DESCRIPTION
    Automation, not a human playtest. Screenshots: Builds/Screenshots/tour-social. Raw logs stay under Builds/
    (git-ignored: they contain local paths). Usernames claimed: nsdriver<N> (kept in the local development database).
#>
param([int]$HostAccount = 0, [int]$GuestAccount = 1, [int]$TimeoutSeconds = 420,
    # Race together instead of the tables (starts a dedicated game server); the guest leaves mid-race, rejoins and spectates.
    [switch]$Race, [int]$Port = 7792,
    # Addendum 04: loopback unless a separately authorized LAN test passes -AllowLan with its addresses.
    [string]$BindHost = '127.0.0.1', [string]$PublicHost = '127.0.0.1', [switch]$AllowLan)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $repo
$exe = Join-Path $repo 'Builds\Game\NightSignal.exe'
if (-not (Test-Path $exe)) { throw "Game build not found: $exe (build it first)" }
try { Invoke-RestMethod -Uri 'http://127.0.0.1:5080/healthz' -TimeoutSec 5 | Out-Null }
catch { throw 'Control plane is not running on 127.0.0.1:5080 (run Tools/run/start-control-plane.ps1).' }

$logs = Join-Path $repo 'Builds\NetRuns\tour-social'
New-Item -ItemType Directory -Force $logs | Out-Null
Remove-Item (Join-Path $repo 'Builds\Screenshots\tour-social\*.png') -ErrorAction SilentlyContinue

function Start-Client([string]$role, [int]$account, [string]$peer) {
    $a = @('-nsUiTourSocial', $role, '-nsDevAccount', "$account", '-nsPeerHandle', $peer,
        '-screen-fullscreen', '0', '-screen-width', '1600', '-screen-height', '900', '-monitor', '1',
        '-logFile', "`"$logs\$role.log`"")
    if ($Race) { $a += '-nsUiTourSocialRace' }
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
        '-nsEvidence', 'Builds/NetRuns/tour-social/evidence', '-logFile', "`"$logs\server.log`"") + (Get-ServerArgs $endpoint))
    Start-Sleep -Seconds 4
}
$hostProc = Start-Client 'host' $HostAccount "nsdriver$GuestAccount"
Start-Sleep -Seconds 2
$guestProc = Start-Client 'guest' $GuestAccount "nsdriver$HostAccount"

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline -and -not ($hostProc.HasExited -and $guestProc.HasExited)) { Start-Sleep -Seconds 2 }
foreach ($p in @(@{ n = 'host'; p = $hostProc }, @{ n = 'guest'; p = $guestProc })) {
    if (-not $p.p.HasExited) { Stop-Process -Id $p.p.Id -Force; Write-Output "$($p.n): TIMEOUT (killed)" } else { Write-Output "$($p.n): exit $($p.p.ExitCode)" }
}
if ($server) {
    Start-Sleep -Seconds 3
    if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force; Write-Output 'server: stopped' } else { Write-Output "server: exit $($server.ExitCode)" }
    Write-Output ("udp $Port released: " + (Wait-PortReleased -Port $Port))
}
Select-String -Path "$logs\host.log", "$logs\guest.log" -Pattern 'NightSignal.UiTourSocial' | ForEach-Object { $_.Line }
