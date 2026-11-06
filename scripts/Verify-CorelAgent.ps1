$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$python = Join-Path $root 'webhost\.venv\Scripts\python.exe'
function Assert-Exit { if ($LASTEXITCODE -ne 0) { throw 'Проверка CorelAgent завершилась ошибкой.' } }
Push-Location $root
try {
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
} finally { Pop-Location }
