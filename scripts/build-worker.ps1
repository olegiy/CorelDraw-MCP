#Requires -Version 5.1
[CmdletBinding()]
param(
    [string] $DotnetPath = 'dotnet',
    [switch] $GenerateInteropOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
& $windowsPowerShell -NoProfile -NonInteractive -STA -File (Join-Path $PSScriptRoot 'generate-worker-interop.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Не удалось получить проверенную типизированную interop-сборку.' }
if ($GenerateInteropOnly) { return }
Push-Location $repository
try {
    & $DotnetPath build 'worker/CorelMcp.Worker/CorelMcp.Worker.csproj' -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Сборка C#-исполнителя завершилась ошибкой.' }
} finally { Pop-Location }
