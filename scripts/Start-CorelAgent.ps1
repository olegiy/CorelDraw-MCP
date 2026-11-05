param([switch]$NoBrowser)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$python = Join-Path $root 'webhost\.venv\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $python)) { throw 'Сначала выполните отдельную установку CorelAgent.' }
Push-Location (Join-Path $root 'webhost')
try {
    if ($NoBrowser) { & $python -m corel_agent --no-browser }
    else { & $python -m corel_agent }
    if ($LASTEXITCODE -ne 0) { throw 'CorelAgent отказал в запуске; проверьте диагностический код.' }
} finally { Pop-Location }
