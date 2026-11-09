#requires -Version 5.1
[CmdletBinding()]
param([int] $OwnerPid, [int] $WorkerPid)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
$sequence = 0
$lease = $null
$journal = $null
$journalDirectory = $null
$watchdog = $null
$active = $false
$clock = [Diagnostics.Stopwatch]::StartNew()
$expires = 0L
$seen = New-Object 'Collections.Generic.HashSet[string]'
$baseline = New-Object 'Collections.Generic.HashSet[string]'
function Get-Field($Object, [string] $Name, $Default = $null) {
    if ($null -eq $Object) { return $Default }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    return $property.Value
}
function Send-Event([string] $Type, [hashtable] $Fields = @{}) {
    $script:sequence++
    $value = @{ schema_version = 1; type = $Type; sequence = $script:sequence }
    if ($null -ne $script:lease) {
        $value.job_id = $script:lease.job_id; $value.generation = $script:lease.generation; $value.token = $script:lease.token
    }
    foreach ($key in $Fields.Keys) { $value[$key] = $Fields[$key] }
    [Console]::WriteLine(($value | ConvertTo-Json -Depth 14 -Compress))
}
function Matches-Lease($Command) {
    return $null -ne $script:lease -and $Command.job_id -ceq $script:lease.job_id -and
        $Command.generation -eq $script:lease.generation -and $Command.token -ceq $script:lease.token
}
try {
    Add-Type -Path (Join-Path $PSScriptRoot 'JsonLineChannel.cs')
    Add-Type -Path (Join-Path $PSScriptRoot 'VbaDialogObserver.cs')
    $channel = New-Object CorelMcp.Observer.JsonLineChannel(65536, 32)
    $owner = [CorelMcp.Observer.VbaWindows]::ProcessIdentity($OwnerPid)
    $worker = [CorelMcp.Observer.VbaWindows]::ProcessIdentity($WorkerPid)
    if ($null -eq $owner -or $null -eq $worker) { throw 'Владелец недоступен.' }
    $watchdog = New-Object CorelMcp.Observer.ObserverWatchdog($OwnerPid, $owner.start_time, $WorkerPid, $worker.start_time)
    Send-Event 'ready' @{ owner = $owner; worker = $worker }
    $stop = $false
    while (-not $stop) {
        if ($channel.Fault) { throw 'Ошибка канала.' }
        if ($channel.Ended) { break }
        if (-not [CorelMcp.Observer.VbaWindows]::Alive($OwnerPid, $owner.start_time) -or
            -not [CorelMcp.Observer.VbaWindows]::Alive($WorkerPid, $worker.start_time)) {
            Send-Event 'degraded' @{ code = 'VBA_OBSERVER_LEASE_LOST' }; break
        }
        if ($active -and $clock.ElapsedMilliseconds -ge $expires) {
            $active = $false; Send-Event 'degraded' @{ code = 'VBA_OBSERVER_LEASE_LOST' }; break
        }
        $line = $channel.Take(0)
        while ($null -ne $line) {
            $command = $line | ConvertFrom-Json
            if ($command.schema_version -ne 1) { throw 'Версия протокола не поддерживается.' }
            if ($command.type -eq 'stop' -and ($null -eq $lease -or (Matches-Lease $command))) { $stop = $true; break }
            if ($command.type -eq 'arm') {
                if ($active -or ($null -ne $lease -and ($command.job_id -cne $lease.job_id -or $command.generation -le $lease.generation))) { throw 'Неверное поколение.' }
                if ($command.owner.pid -ne $OwnerPid -or $command.owner.start_time -cne $owner.start_time -or
                    $command.worker.pid -ne $WorkerPid -or $command.worker.start_time -cne $worker.start_time -or
                    $command.token -cnotmatch '^[a-f0-9]{48}$' -or $command.phase -cnotmatch '^[a-z_]{1,32}$') { throw 'Неверная привязка.' }
                $lease = $command
                if ($null -eq $journal) {
                    $journalDirectory = [IO.Path]::GetFullPath([string]$command.run_directory)
                    $journal = New-Object CorelMcp.Observer.BoundedJournal($journalDirectory)
                } elseif (-not [string]::Equals($journalDirectory, [IO.Path]::GetFullPath([string]$command.run_directory), [StringComparison]::OrdinalIgnoreCase)) { throw 'Изменён каталог задания.' }
                $snapshot = [CorelMcp.Observer.VbaWindows]::Snapshot([string]$command.app_hwnd)
                if ($null -eq $snapshot.identity) { Send-Event 'degraded' @{ code = 'VBA_OBSERVER_UNAVAILABLE' }; $stop = $true; break }
                $appIdentity = $snapshot.identity
                $baseline.Clear()
                foreach ($window in $snapshot.windows) { [void]$baseline.Add([string]$window.hwnd) }
                $expires = $clock.ElapsedMilliseconds + 2000; $active = $true
                $watchdog.Renew()
                Send-Event 'armed' @{ identity = $appIdentity }
            } elseif (Matches-Lease $command) {
                switch ($command.type) {
                    'renew' { if ($active) { $expires = $clock.ElapsedMilliseconds + 2000; $watchdog.Renew() } }
                    'disarm' { $active = $false; $watchdog.Disarm(); Send-Event 'disarmed' }
                    default { throw 'Неизвестная команда.' }
                }
            }
            $line = $channel.Take(0)
        }
        if ($stop) { break }
        if ($active) {
            $snapshot = [CorelMcp.Observer.VbaWindows]::Snapshot([string]$lease.app_hwnd)
            if ($null -eq $snapshot.identity -or $snapshot.identity.pid -ne $appIdentity.pid -or
                $snapshot.identity.start_time -cne $appIdentity.start_time -or $snapshot.identity.tid -ne $appIdentity.tid -or
                $snapshot.identity.desktop -cne $appIdentity.desktop -or $snapshot.identity.session -ne $appIdentity.session) {
                Send-Event 'degraded' @{ code = 'VBA_OBSERVER_LEASE_LOST' }; break
            }
            foreach ($window in $snapshot.windows) {
                $candidate = ([string]$appIdentity.pid) + ':' + $appIdentity.start_time + ':' + $window.hwnd
                if (-not $seen.Add($candidate)) { continue }
                $record = @{ job_id = $lease.job_id; generation = $lease.generation; phase = $lease.phase;
                    time = [DateTime]::UtcNow.ToString('o'); identity = $appIdentity; window = $window;
                    baseline = $baseline.Contains([string]$window.hwnd); code = 'VBA_DIALOG_REQUIRES_OPERATOR' }
                if (-not $journal.Append(($record | ConvertTo-Json -Depth 14 -Compress))) {
                    $journal.Truncated(); Send-Event 'degraded' @{ code = 'VBA_OBSERVER_PROTOCOL_ERROR'; events_truncated = $true }; $stop = $true; break
                }
                Send-Event 'observation' @{ candidate_id = $candidate; code = 'VBA_DIALOG_REQUIRES_OPERATOR';
                    artifact = (Join-Path $journalDirectory 'vba-observer.jsonl'); events_truncated = [bool]$snapshot.truncated }
            }
            if ($snapshot.truncated) {
                Send-Event 'degraded' @{ code = 'VBA_OBSERVER_PROTOCOL_ERROR'; events_truncated = $true }; break
            }
        }
        Start-Sleep -Milliseconds 250
    }
} catch {
    # Never serialize exceptions, window text, source, tokens or paths to stderr/stdout.
    try { Send-Event 'degraded' @{ code = 'VBA_OBSERVER_PROTOCOL_ERROR' } } catch { }
} finally {
    $active = $false
    if ($null -ne $watchdog) { $watchdog.Dispose() }
    if ($null -ne $journal) { $journal.Dispose() }
    try { Send-Event 'stopped' } catch { }
}
