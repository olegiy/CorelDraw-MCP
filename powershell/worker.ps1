#requires -Version 5.1
[CmdletBinding()]
param([string] $ProgId = 'CorelDRAW.Application.27')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8 = New-Object Text.UTF8Encoding($false)
[Console]::InputEncoding = $utf8
[Console]::OutputEncoding = $utf8
if ($PSVersionTable.PSEdition -ne 'Desktop' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw 'Запустите с помощью Windows PowerShell 5.1 с параметром -STA.'
}
Import-Module (Join-Path $PSScriptRoot 'CorelAutomation.psm1') -Force
Add-Type -Path (Join-Path $PSScriptRoot 'JsonLineChannel.cs')
# One reader owns stdin, including after a service timeout. The background
# thread reads bytes only; all callbacks and COM remain on this STA thread.
$channel = New-Object CorelMcp.Observer.JsonLineChannel(16777216, 32)
function Read-WorkerRecord([int] $TimeoutMs = -1) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        if ($channel.Fault) { throw 'Ошибка канала исполнителя.' }
        $remaining = if ($TimeoutMs -lt 0) { 1000 } else { [Math]::Max(0, $TimeoutMs - [int]$watch.ElapsedMilliseconds) }
        $record = $channel.Take([Math]::Min(1000, $remaining))
        if ($null -ne $record) { return ($record | ConvertFrom-Json) }
        if ($channel.Ended -or ($TimeoutMs -ge 0 -and $watch.ElapsedMilliseconds -ge $TimeoutMs)) { return $null }
    }
}
function Wait-WorkerReply([string] $Type, [string] $JobId, [string] $HandshakeId = '', [int] $TimeoutMs = -1) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        $remaining = if ($TimeoutMs -lt 0) { -1 } else { [Math]::Max(0, $TimeoutMs - [int]$watch.ElapsedMilliseconds) }
        $reply = Read-WorkerRecord $remaining
        if ($null -eq $reply) { return $null }
        if ($reply.type -eq $Type -and $reply.job_id -eq $JobId) {
            if ($HandshakeId -eq '') { return $reply }
            if ($null -ne $reply.PSObject.Properties['handshake_id'] -and $reply.handshake_id -ceq $HandshakeId -and
                $null -ne $reply.PSObject.Properties['schema_version'] -and $reply.schema_version -eq 1) { return $reply }
        }
        if ($TimeoutMs -ge 0 -and $watch.ElapsedMilliseconds -ge $TimeoutMs) { return $null }
        # Ignore old service frames; never launch a job from inside a callback.
        if ($reply.type -notmatch '^(observer_|json_validation)') { throw 'Неожиданная команда во время служебного ожидания.' }
    }
}
while ($null -ne ($command = Read-WorkerRecord)) {
    try {
        if ($null -ne $command.PSObject.Properties['type'] -and $command.type -match '^(observer_|json_validation)') { continue }
        $parameters = @{}
        foreach ($property in $command.args.PSObject.Properties) { $parameters[$property.Name] = $property.Value }
        $jobId = [string]$command.job_id
        $observerWindow = ''
        $observerEnabled = $null -ne $command.PSObject.Properties['observer_mode'] -and $command.observer_mode -eq 'observe'
        $progress = {
            param([string] $Stage)
            [Console]::WriteLine((@{ type = 'progress'; job_id = $jobId; stage = $Stage } | ConvertTo-Json -Compress))
        }
        $validateJson = {
            param([string] $Json)
            [Console]::WriteLine((@{ type = 'validate_json'; job_id = $jobId; json = $Json } | ConvertTo-Json -Compress))
            $reply = Wait-WorkerReply 'json_validation' $jobId
            if ($null -eq $reply) { throw 'Канал проверки JSON закрылся до получения подтверждения.' }
            if ($reply.type -ne 'json_validation' -or $reply.job_id -ne $jobId) { throw 'Получено неожиданное подтверждение проверки JSON.' }
            if (-not $reply.ok) {
                $failure = New-Object InvalidOperationException('Функция Main должна возвращать корректный JSON. Проверьте снимок исходного кода и сохранённый GMS.')
                $failure.Data['code'] = 'INVALID_VBA_JSON'
                throw $failure
            }
        }
        $observer = $null
        if ($observerEnabled) {
            $observer = {
                param([string] $Action, [string] $Phase, [string] $ProjectName, [string] $Window)
                if ($Window -ne '') { $script:observerWindow = $Window }
                $handshakeId = [guid]::NewGuid().ToString('N')
                $message = @{ schema_version = 1; type = ('observer_' + $Action); job_id = $jobId; handshake_id = $handshakeId }
                if ($Action -eq 'prepare') {
                    $message.phase = $Phase; $message.project_name = $ProjectName; $message.app_hwnd = $script:observerWindow
                }
                [Console]::WriteLine(($message | ConvertTo-Json -Compress))
                $replyType = if ($Action -eq 'prepare') { 'observer_ready' } else { 'observer_finished' }
                $reply = Wait-WorkerReply $replyType $jobId $handshakeId 2000
                if ($null -eq $reply -or $reply.available -isnot [bool]) {
                    return @{ available = $false; compile_error_confirmed = $false }
                }
                # First release is observation only, irrespective of extra reply fields.
                return @{ available = $reply.available; compile_error_confirmed = $false }
            }
        }
        $result = Invoke-CorelOperation -Tool $command.tool -Parameters $parameters -RunDirectory $command.run_directory -ProgId $ProgId -Progress $progress -ValidateJson $validateJson -Observer $observer
        [Console]::WriteLine((@{ type = 'result'; job_id = $jobId; data = $result } | ConvertTo-Json -Depth 30 -Compress))
    }
    catch {
        [Console]::Error.WriteLine($_.Exception.ToString())
        exit 1
    }
}
# Release this process's COM references, never quit the user's application.
Remove-Module CorelAutomation
