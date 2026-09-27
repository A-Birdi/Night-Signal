#requires -Version 5.1
<#
.SYNOPSIS
    Starts the Night Signal control plane locally: Development environment, loopback only, DevAuth + SQLite.

.DESCRIPTION
    - Binds ONLY to 127.0.0.1 (DevAuth refuses to start on any other address or environment).
    - Uses Services/ControlPlane/appsettings.Development.json: SQLite in Services/ControlPlane/.data/,
      generated dev keys in Services/ControlPlane/.devkeys/ (both git-ignored), seed accounts from
      Backend/seed/dev-accounts.example.json.
    - A local game server authenticates with the generated key in .devkeys/gameserver-dev.key (server id "dev-local").
    - Nothing here is reachable from other machines, and no real credentials are involved.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Tools/run/start-control-plane.ps1
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Tools/run/start-control-plane.ps1 -Port 5090 -NoBuild
#>
param(
    [ValidateRange(1024, 65535)][int]$Port = 5080,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$projectDir = Join-Path $repo 'Services\ControlPlane'
$project = Join-Path $projectDir 'NightSignal.ControlPlane.csproj'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK (10.x) was not found on PATH.'
}
if (-not (Test-Path $project)) {
    throw "Control plane project not found: $project"
}

# Development + loopback only. Clear variables that could widen the binding.
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = "http://127.0.0.1:$Port"
foreach ($name in 'ASPNETCORE_HTTP_PORTS', 'ASPNETCORE_HTTPS_PORTS', 'DOTNET_URLS', 'URLS') {
    Remove-Item "Env:$name" -ErrorAction SilentlyContinue
}

Write-Host "Night Signal control plane (Development, DevAuth, SQLite) -> http://127.0.0.1:$Port"
Write-Host "  health:        http://127.0.0.1:$Port/healthz"
Write-Host "  dev sign-in:   POST http://127.0.0.1:$Port/dev/auth/token   (accounts: Backend/seed/dev-accounts.example.json)"
Write-Host "  control WS:    ws://127.0.0.1:$Port/v1/control?build=<build>&protocol=1&content=<contentHash>"
Write-Host "  game-server key file (after first start): Services/ControlPlane/.devkeys/gameserver-dev.key"
Write-Host 'Press Ctrl+C to stop.'

$runArgs = @('run', '--project', $project, '--no-launch-profile')
if ($NoBuild) { $runArgs += '--no-build' }

Push-Location $projectDir
try {
    & dotnet @runArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}
