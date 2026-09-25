<#
.SYNOPSIS
    Records a real, densely related capture for qualifying the communication graph (plan §6.3, §19.4).

.DESCRIPTION
    Copies the TestWorkloads build under 13 executable names, then records live with `icat record` while nine loopback
    TCP hubs exchange with their clients, plus a pool of worker processes on one queue: by default 65 workload processes
    and 56 relationships beside whatever else the machine runs. The session lands in <Root>\dense-session; qualify it with

        $env:INTERCAT_REAL_SESSION = '<Root>\dense-session'
        dotnet test tests/InterCat.Ui.Tests -c Release --filter FullyQualifiedName~RealSessionGraphTests

    Needs an elevated shell (real kernel ETW) and Release builds of InterCat.Cli and InterCat.TestWorkloads. The capture
    holds this machine's process names and must stay local: never commit a session it produces.

.EXAMPLE
    ./tools/Record-DenseGraphCapture.ps1 -Root $env:TEMP\intercat-dense
#>
param(
    [Parameter(Mandatory)] [string] $Root,
    [string] $Workload = (Join-Path $PSScriptRoot '..\src\InterCat.TestWorkloads\bin\Release\net10.0'),
    [string] $Icat = (Join-Path $PSScriptRoot '..\src\InterCat.Cli\bin\Release\net10.0\InterCat.Cli.exe'),
    [int] $Duration = 50,
    [int] $Workers = 27)

$ErrorActionPreference = 'Stop'
$bin = Join-Path $Root 'bin'
$truth = Join-Path $Root 'truth'
$session = Join-Path $Root 'dense-session'
if (Test-Path $session) { throw "$session already exists; choose a fresh root." }

$names = @('gateway', 'auth-service', 'billing-api', 'orders-api', 'inventory-db', 'cache', 'search-indexer',
    'web-ui', 'mobile-sync', 'report-worker', 'metrics-agent', 'queue', 'worker')
foreach ($name in $names) {
    $dir = Join-Path $bin $name
    New-Item -ItemType Directory -Force $dir | Out-Null
    Copy-Item (Join-Path $Workload '*') $dir -Recurse -Force
    Copy-Item (Join-Path $dir 'InterCat.TestWorkloads.exe') (Join-Path $dir "$name.exe") -Force
}

# A hub is one server process and the client processes that connect to it; each client makes two connections.
$hubs = @(
    @{ Server = 'gateway'; Clients = @('web-ui', 'web-ui', 'web-ui', 'web-ui', 'mobile-sync', 'mobile-sync') },
    @{ Server = 'auth-service'; Clients = @('gateway', 'gateway', 'mobile-sync') },
    @{ Server = 'orders-api'; Clients = @('gateway', 'web-ui', 'report-worker') },
    @{ Server = 'billing-api'; Clients = @('orders-api', 'report-worker') },
    @{ Server = 'inventory-db'; Clients = @('orders-api', 'orders-api', 'search-indexer', 'report-worker') },
    @{ Server = 'cache'; Clients = @('gateway', 'orders-api', 'billing-api', 'auth-service') },
    @{ Server = 'search-indexer'; Clients = @('web-ui', 'web-ui') },
    @{ Server = 'metrics-agent'; Clients = @('gateway', 'orders-api', 'billing-api', 'cache', 'queue') },
    @{ Server = 'queue'; Clients = @(1..$Workers | ForEach-Object { 'worker' }) }
)

$recording = Start-Process -FilePath $Icat -ArgumentList @('record', $session, '--duration', $Duration, '--json') `
    -RedirectStandardOutput (Join-Path $Root 'record.json') -RedirectStandardError (Join-Path $Root 'record.err') `
    -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 4

$messages = 24
$delay = 400
$sequence = 0
$servers = @()
$clients = @()
foreach ($hub in $hubs) {
    $connections = 2 * $hub.Clients.Count
    $sequence++
    $serverTruth = Join-Path $truth ("{0:D3}-{1}-server" -f $sequence, $hub.Server)
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $bin "$($hub.Server)\$($hub.Server).exe"
    $start.Arguments = "tcp-loopback-server --truth `"$serverTruth`" --connections $connections --concurrency $connections --messages $messages"
    $start.RedirectStandardOutput = $true
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $server = [System.Diagnostics.Process]::Start($start)
    $port = ($server.StandardOutput.ReadLine() | ConvertFrom-Json).port
    $servers += $server
    foreach ($client in $hub.Clients) {
        $sequence++
        $clientTruth = Join-Path $truth ("{0:D3}-{1}-client" -f $sequence, $client)
        $clients += Start-Process -FilePath (Join-Path $bin "$client\$client.exe") -PassThru -WindowStyle Hidden `
            -ArgumentList @('tcp-loopback-client', '--truth', $clientTruth, '--port', $port, '--connections', 2,
                '--concurrency', 2, '--messages', $messages, '--delay', $delay)
    }
}

"started $($servers.Count) servers and $($clients.Count) clients"
$clients | ForEach-Object { $_.WaitForExit() }
$servers | ForEach-Object { $_.WaitForExit() }
$failed = @($clients + $servers | Where-Object { $_.ExitCode -ne 0 }).Count
"workloads finished; $failed exited non-zero"
$recording.WaitForExit()
"recording exited with $($recording.ExitCode); session: $session"
