#requires -Version 5.1
<#
.SYNOPSIS
    Network-boundary guard for the local automation harnesses (Addendum 04).

.DESCRIPTION
    Every harness that starts a dedicated game server resolves its endpoint through Resolve-ServerEndpoint first:
      - the ordinary local mode binds the IPv4 loopback (127.0.0.1) and advertises it;
      - a non-loopback or wildcard bind is refused BEFORE any process starts unless the caller passed its explicit
        LAN opt-in (-AllowLan);
      - the UDP port must be free; if it is taken the owning process is named and the run stops (nothing is killed).
    After the server starts, Get-ServerSockets records the real sockets the server process owns (read-only
    Get-NetUDPEndpoint / Get-NetTCPConnection), and Wait-PortReleased verifies the listener is gone at the end.
    Nothing here reads or changes Windows Firewall policy: firewall rules are the user's.
#>

Set-StrictMode -Version 2.0

function Test-IsLoopback([string]$address) {
    $ip = $null
    if (-not [System.Net.IPAddress]::TryParse($address, [ref]$ip)) { return $false }
    return [System.Net.IPAddress]::IsLoopback($ip)
}

function Resolve-ServerEndpoint {
    <#
    .SYNOPSIS Validates bind/advertise/port for a local run; throws (fail closed) before anything is launched.
    #>
    param(
        [string]$BindHost = '127.0.0.1',
        [string]$PublicHost = '127.0.0.1',
        [int]$Port = 7777,
        [switch]$AllowLan,
        [string]$Executable
    )
    $ip = $null
    if ([string]::IsNullOrWhiteSpace($BindHost) -or -not [System.Net.IPAddress]::TryParse($BindHost.Trim(), [ref]$ip)) {
        throw "NetGuard: bind address '$BindHost' is not a numeric IP address (host names, LAN discovery and 'best interface' are not used)."
    }
    $loopback = [System.Net.IPAddress]::IsLoopback($ip)
    if (-not $loopback -and -not $AllowLan) {
        throw "NetGuard: refusing to start a server bound to $BindHost without the explicit LAN opt-in (-AllowLan). Local automation binds 127.0.0.1 only (Addendum 04)."
    }
    if ($loopback -and -not (Test-IsLoopback $PublicHost)) {
        throw "NetGuard: a loopback listener ($BindHost) cannot be reached at the advertised address $PublicHost."
    }
    if ($Port -lt 1 -or $Port -gt 65535) { throw "NetGuard: invalid UDP port $Port." }
    $owners = @(Get-NetUDPEndpoint -LocalPort $Port -ErrorAction SilentlyContinue)
    if ($owners.Count -gt 0) {
        $who = ($owners | ForEach-Object {
            $p = Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue
            "$($_.LocalAddress):$($_.LocalPort) pid $($_.OwningProcess) ($(if ($p) { $p.ProcessName } else { 'unknown' }))"
        }) -join '; '
        throw "NetGuard: UDP port $Port is already in use: $who. Not starting another server and not killing anything; free the port or pick another."
    }
    $class = if ($loopback) { 'local' } elseif ($ip.Equals([System.Net.IPAddress]::Any) -or $ip.Equals([System.Net.IPAddress]::IPv6Any)) { 'wildcard' } else { 'lan' }
    return [pscustomobject]@{
        bindHost = $BindHost.Trim(); publicHost = $PublicHost.Trim(); port = $Port; protocol = 'udp'
        classification = $class; lanOptIn = [bool]$AllowLan
        executable = if ($Executable) { Split-Path $Executable -Leaf } else { '' }
        role = 'dedicated-server'
    }
}

function Get-ServerArgs($endpoint) {
    $a = @('-nsBindHost', $endpoint.bindHost, '-nsPublicHost', $endpoint.publicHost, '-nsPort', "$($endpoint.port)")
    if ($endpoint.lanOptIn) { $a += '-nsAllowLan' }
    return $a
}

function Get-ServerSockets {
    <#
    .SYNOPSIS The sockets a process really owns (read-only), for the evidence.
    #>
    param([int]$ProcessId, [int]$Port, [int]$WaitSeconds = 30)
    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    $udp = @()
    do {
        $udp = @(Get-NetUDPEndpoint -OwningProcess $ProcessId -ErrorAction SilentlyContinue)
        if ($udp | Where-Object { $_.LocalPort -eq $Port }) { break }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    $tcp = @(Get-NetTCPConnection -OwningProcess $ProcessId -State Listen -ErrorAction SilentlyContinue)
    return [pscustomobject]@{
        pid = $ProcessId
        udp = @($udp | ForEach-Object { "$($_.LocalAddress):$($_.LocalPort)" })
        tcpListen = @($tcp | ForEach-Object { "$($_.LocalAddress):$($_.LocalPort)" })
        gameListener = @($udp | Where-Object { $_.LocalPort -eq $Port } | ForEach-Object { "$($_.LocalAddress):$($_.LocalPort)" })
    }
}

function Wait-PortReleased {
    <#
    .SYNOPSIS True when no process holds the UDP port any more (after graceful exit or the harness's own timeout).
    #>
    param([int]$Port, [int]$WaitSeconds = 20)
    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    while ((Get-Date) -lt $deadline) {
        if (@(Get-NetUDPEndpoint -LocalPort $Port -ErrorAction SilentlyContinue).Count -eq 0) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

Export-ModuleMember -Function Resolve-ServerEndpoint, Get-ServerArgs, Get-ServerSockets, Wait-PortReleased, Test-IsLoopback
