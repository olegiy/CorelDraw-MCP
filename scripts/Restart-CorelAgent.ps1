param(
    [switch]$NoBrowser,
    [ValidateRange(1, 120)][int]$TimeoutSeconds = 15
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$python = Join-Path $root 'webhost\.venv\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $python)) { throw 'Python runtime панели отсутствует.' }
Push-Location (Join-Path $root 'webhost')
try {
    $arguments = @('-m', 'corel_agent', '--restart', '--restart-timeout', [string]$TimeoutSeconds)
    if ($NoBrowser) { $arguments += '--no-browser' }
    & $python @arguments
    if ($LASTEXITCODE -ne 0) { throw 'CorelAgent отказал в перезапуске; проверьте диагностический код.' }
} finally { Pop-Location }
