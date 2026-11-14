[CmdletBinding()]
param([string] $DotnetPath = 'dotnet', [switch] $SourceOnly)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$python = Join-Path $root 'webhost\.venv\Scripts\python.exe'
function Assert-Exit { if ($LASTEXITCODE -ne 0) { throw 'Проверка CorelAgent завершилась ошибкой.' } }
Push-Location $root
$savedEnvironment = @{}
foreach ($name in @('DOTNET_HOST_PATH', 'DOTNET_ROOT_X64', 'CORELDRAW_MCP_CSHARP_WORKER')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
try {
    if (-not $SourceOnly) {
        & node (Join-Path $PSScriptRoot 'worker-package.mjs'); Assert-Exit
    } else {
        Write-Host 'Режим исходников: готовность распространяемого пакета не проверяется.'
    }
    $dotnet = (Get-Command $DotnetPath -CommandType Application -ErrorAction Stop).Source
    $env:DOTNET_HOST_PATH = $dotnet
    & $dotnet test 'worker/CorelMcp.Worker.Tests/CorelMcp.Worker.Tests.csproj' -c Release; Assert-Exit
    if ($SourceOnly) {
        $env:DOTNET_ROOT_X64 = Split-Path -Parent $dotnet
        $env:CORELDRAW_MCP_CSHARP_WORKER = Join-Path $root 'worker/CorelMcp.Worker/bin/Release/net10.0-windows/CorelMcp.Worker.exe'
    }
    & npm test; Assert-Exit
    & npm run check:contracts; Assert-Exit
    Push-Location (Join-Path $root 'webhost')
    try {
        & $python -m pytest -q -m 'not live'; Assert-Exit
        & $python -m ruff check corel_agent tests; Assert-Exit
        & $python -m corel_agent.export_openapi --check; Assert-Exit
    } finally { Pop-Location }
    Push-Location (Join-Path $root 'webui')
    try {
        & npm run check:api; Assert-Exit
        & npm run build; Assert-Exit
        & npm test; Assert-Exit
    } finally { Pop-Location }
} finally {
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
    Pop-Location
}
