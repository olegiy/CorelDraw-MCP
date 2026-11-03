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
while ($null -ne ($line = [Console]::ReadLine())) {
    try {
        $command = $line | ConvertFrom-Json
        $parameters = @{}
        foreach ($property in $command.args.PSObject.Properties) { $parameters[$property.Name] = $property.Value }
        $jobId = [string]$command.job_id
        $progress = {
            param([string] $Stage)
            [Console]::WriteLine((@{ type = 'progress'; job_id = $jobId; stage = $Stage } | ConvertTo-Json -Compress))
        }
        $validateJson = {
            param([string] $Json)
            [Console]::WriteLine((@{ type = 'validate_json'; job_id = $jobId; json = $Json } | ConvertTo-Json -Compress))
            $replyLine = [Console]::ReadLine()
            if ($null -eq $replyLine) { throw 'Канал проверки JSON закрылся до получения подтверждения.' }
            $reply = $replyLine | ConvertFrom-Json
            if ($reply.type -ne 'json_validation' -or $reply.job_id -ne $jobId) { throw 'Получено неожиданное подтверждение проверки JSON.' }
            if (-not $reply.ok) {
                $failure = New-Object InvalidOperationException('Функция Main должна возвращать корректный JSON. Проверьте снимок исходного кода и сохранённый GMS.')
                $failure.Data['code'] = 'INVALID_VBA_JSON'
                throw $failure
            }
        }
        $result = Invoke-CorelOperation -Tool $command.tool -Parameters $parameters -RunDirectory $command.run_directory -ProgId $ProgId -Progress $progress -ValidateJson $validateJson
        [Console]::WriteLine((@{ type = 'result'; job_id = $jobId; data = $result } | ConvertTo-Json -Depth 30 -Compress))
    }
    catch {
        [Console]::Error.WriteLine($_.Exception.ToString())
        exit 1
    }
}
# Release this process's COM references, never quit the user's application.
Remove-Module CorelAutomation
