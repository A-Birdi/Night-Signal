#requires -Version 5.1
<#
.SYNOPSIS
    Online UI evidence run: one dedicated game server process plus one windowed client that drives the REAL menu buttons
    through sign-in, convoy, Mode Ready, event proposal, Event Ready, a server-authoritative race and the post-event
    decision (FrontEndApp.UiTourOnline). Requires the local control plane (start-control-plane.ps1).

.DESCRIPTION
    Automation, not a human playtest: the client signs in with a development account from the project's seed file and
    races with the validator autopilot through normal inputs. Screenshots: Builds/Screenshots/tour-online. Raw logs stay
    under Builds/ (git-ignored: they contain local paths).
#>
param([int]$DevAccount = 0, [int]$Port = 7777, [int]$TimeoutSeconds = 600, [switch]$Freeplay, [switch]$Garage, [switch]$Appearance, [int]$Intent = -1, [string]$Trial = "", [string]$Course = "", [string]$CupLegs = "",
    # Addendum 04: loopback unless a separately authorized LAN test passes -AllowLan with its addresses.
    [string]$BindHost = '127.0.0.1', [string]$PublicHost = '127.0.0.1', [switch]$AllowLan)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $repo
$exe = Join-Path $repo 'Builds\Game\NightSignal.exe'
if (-not (Test-Path $exe)) { throw "Game build not found: $exe (build it first)" }
try { Invoke-RestMethod -Uri 'http://127.0.0.1:5080/healthz' -TimeoutSec 5 | Out-Null }
catch { throw 'Control plane is not running on 127.0.0.1:5080 (run Tools/run/start-control-plane.ps1).' }

$logs = Join-Path $repo 'Builds\NetRuns\tour-online'
New-Item -ItemType Directory -Force $logs | Out-Null
Import-Module (Join-Path $PSScriptRoot 'NetGuard.psm1') -Force
$endpoint = Resolve-ServerEndpoint -BindHost $BindHost -PublicHost $PublicHost -Port $Port -AllowLan:$AllowLan -Executable $exe
Write-Output "server endpoint: bind $($endpoint.bindHost) advertise $($endpoint.publicHost) udp $($endpoint.port) ($($endpoint.classification))"
# A Custom Cup races several matches on the same game server: it stays up (and is stopped below) instead of exiting after one.
$serverRole = if ($CupLegs) { @('-batchmode', '-nographics', '-nsServer') } else { @('-batchmode', '-nographics', '-nsServer', '-nsExitAfterMatch') }
$server = Start-Process -FilePath $exe -PassThru -WorkingDirectory $repo -ArgumentList ($serverRole + @(
    '-nsEvidence', 'Builds/NetRuns/tour-online/evidence', '-logFile', "`"$logs\server.log`"") + (Get-ServerArgs $endpoint))
Start-Sleep -Seconds 4
$clientArgs = @('-nsUiTourOnline', '-nsDevAccount', "$DevAccount", '-screen-fullscreen', '0', '-screen-width', '1920', '-screen-height', '1080',
    '-logFile', "`"$logs\client.log`"")
if ($Freeplay) { $clientArgs += '-nsUiTourFreeplay' } # Freeplay sprint decided by a course vote
if ($Garage) { $clientArgs += "-nsUiTourGarage" } # online Garage tyre change before the event
if ($Appearance) { $clientArgs += "-nsUiTourAppearance" } # online livery applied before the event, checked on the race car
if ($Intent -ge 0) { $clientArgs += @('-nsUiTourIntent', "$Intent") } # 4 = Freeplay Time Attack (group, non-contact)
if ($Course) { $clientArgs += @('-nsUiTourCourse', $Course) }       # freeplay course, e.g. C01 for Drift Attack
if ($Trial) { $clientArgs += @('-nsUiTourTrial', $Trial) }          # Team Trial id, e.g. TT_BEST
if ($CupLegs) { $clientArgs += @('-nsUiTourCupLegs', $CupLegs) }    # Custom Cup legs "C01,C02,C03" with -Intent 7
$client = Start-Process -FilePath $exe -PassThru -WorkingDirectory $repo -ArgumentList $clientArgs

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$serverUdp = New-Object System.Collections.Generic.HashSet[string]
while ((Get-Date) -lt $deadline -and -not $client.HasExited) {
    foreach ($u in @(Get-NetUDPEndpoint -OwningProcess $server.Id -ErrorAction SilentlyContinue)) { [void]$serverUdp.Add("$($u.LocalAddress):$($u.LocalPort)") }
    Start-Sleep -Seconds 2
}
if (-not $client.HasExited) { Stop-Process -Id $client.Id -Force; Write-Output 'client: TIMEOUT (killed)' } else { Write-Output "client: exit $($client.ExitCode)" }
Start-Sleep -Seconds 3
if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force; Write-Output 'server: stopped' } else { Write-Output "server: exit $($server.ExitCode)" }
Write-Output ("server udp sockets: " + (@($serverUdp) -join ', ') + "; udp $Port released: " + (Wait-PortReleased -Port $Port))
Select-String -Path "$logs\client.log" -Pattern 'NightSignal.UiTourOnline' | ForEach-Object { $_.Line }
