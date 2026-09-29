#requires -Version 5.1
<#
.SYNOPSIS
    String bounds audit (Gate 4): offline tours with -nsBoundsAudit — at 1280x720 with Text size 150 % / HUD size 130 %
    (the hardest fit) every offline tour that walks the screens by their buttons; at 2560x1080 with the same sizes the UI
    and instrument tours; at 1920x1080 with the defaults the UI tour. Every visible label on every screen a tour passes
    must fit its box; each run writes Builds/Screenshots/bounds/bounds-<tour>-<size>-text<N>.txt.

.DESCRIPTION
    Automation, not a human check. Each run has its own preferences and Local profile folders under Builds/BoundsRuns
    (git-ignored), never the player's own settings. Runs one window at a time. Raw logs stay under Builds/.
#>
param([int]$TimeoutSeconds = 900, [string[]]$Only)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $repo
$exe = Join-Path $repo 'Builds\Game\NightSignal.exe'
if (-not (Test-Path $exe)) { throw "Game build not found: $exe (build it first)" }

$every = 'UiTour', 'InstrumentTour', 'DriverCardTour', 'DiaryTour', 'CupTour', 'TutorialTour', 'StoryTour', 'AppearanceTour'
$runs = @(
    @{ Name = '720p-large'; Width = 1280; Height = 720; Text = 1.5; Hud = 1.3; Tours = $every },
    @{ Name = 'ultrawide-large'; Width = 2560; Height = 1080; Text = 1.5; Hud = 1.3; Tours = @('UiTour', 'InstrumentTour') },
    @{ Name = '1080p-default'; Width = 1920; Height = 1080; Text = 1.0; Hud = 1.0; Tours = @('UiTour') }
)
if ($Only) { $runs = $runs | Where-Object { $Only -contains $_.Name } }

foreach ($r in $runs) { foreach ($tour in $r.Tours) {
    $root = Join-Path $repo "Builds\BoundsRuns\$($r.Name)-$tour"
    Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
    $prefs = Join-Path $root 'prefs'
    $profiles = Join-Path $root 'profiles'
    New-Item -ItemType Directory -Force $prefs, $profiles | Out-Null
    # Only the presentation sizes are seeded; everything else takes its defaults.
    @{ Schema = 1; TextScale = $r.Text; HudScale = $r.Hud } | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $prefs 'driving.json')
    $log = Join-Path $root 'player.log'
    $p = Start-Process -FilePath $exe -PassThru -WorkingDirectory $repo -ArgumentList @(
        "-ns$tour", '-nsBoundsAudit', '-nsPrefsFolder', "`"$prefs`"", '-nsLocalProfiles', "`"$profiles`"",
        '-screen-fullscreen', '0', '-screen-width', "$($r.Width)", '-screen-height', "$($r.Height)", '-logFile', "`"$log`"")
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) { Stop-Process -Id $p.Id -Force; Write-Output "$($r.Name) $($tour): TIMEOUT (stopped)" }
    else { Write-Output "$($r.Name) $($tour): exit $($p.ExitCode)" }
    Select-String -Path $log -Pattern 'NightSignal.Bounds\]|Tour[A-Za-z]*\] (PASS|FAILED)' | ForEach-Object { $_.Line }
} }
