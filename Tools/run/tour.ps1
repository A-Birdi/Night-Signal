#requires -Version 5.1
<#
.SYNOPSIS
    Runs one offline built-player tour (-ns<Tour>) of the canonical build in a 1280x720 window, with its own preferences
    and Local profile folders under Builds/TourRuns/<Tour> (git-ignored) — never the player's own settings. Offline tours
    open no network listener. Prints the tour's verdict lines; the raw log stays under Builds/.
#>
param([Parameter(Mandatory = $true)][string]$Tour, [int]$TimeoutSeconds = 1500, [string]$TrialOnly = '')

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $repo
$exe = Join-Path $repo 'Builds\Game\NightSignal.exe'
if (-not (Test-Path $exe)) { throw "Game build not found: $exe (build it first)" }
$root = Join-Path $repo "Builds\TourRuns\$Tour"
Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
$prefs = Join-Path $root 'prefs'
$profiles = Join-Path $root 'profiles'
New-Item -ItemType Directory -Force $prefs, $profiles | Out-Null
$log = Join-Path $root 'player.log'
$arguments = @(
    "-ns$Tour", '-nsPrefsFolder', "`"$prefs`"", '-nsLocalProfiles', "`"$profiles`"",
    '-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720', '-logFile', "`"$log`"")
# -TrialOnly TR-CH39,TR-CH36: the trial tour drives only those trials (a targeted run; the full tour is the regression check).
if ($TrialOnly) { $arguments += @('-nsTrialOnly', $TrialOnly) }
$p = Start-Process -FilePath $exe -PassThru -WorkingDirectory $repo -ArgumentList $arguments
if (-not $p.WaitForExit($TimeoutSeconds * 1000)) { Stop-Process -Id $p.Id -Force; Write-Output "$($Tour): TIMEOUT (stopped)" }
else { Write-Output "$($Tour): exit $($p.ExitCode)" }
Select-String -Path $log -Pattern "NightSignal\.$Tour\]" | ForEach-Object { $_.Line }
