$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$python = Join-Path $root 'webhost\.venv\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $python)) { throw 'Python runtime панели отсутствует.' }
Push-Location (Join-Path $root 'webhost')
try {
    & $python -m corel_agent --stop
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось подтвердить владельца панели; принудительное завершение запрещено.' }
} finally { Pop-Location }
