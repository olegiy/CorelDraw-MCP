$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$hostRoot = Join-Path $root 'webhost'
$venv = Join-Path $hostRoot '.venv'
if (-not (Test-Path -LiteralPath $hostRoot)) { throw 'Не найден webhost.' }
$nodeVersion = & node --version
if ($LASTEXITCODE -ne 0 -or $nodeVersion -notmatch '^v24\.\d+\.\d+$') { throw 'Для установки нужен Node.js 24.' }
if (-not (Test-Path -LiteralPath $venv)) {
    & py -3.12 -m venv $venv
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось создать Python 3.12 venv.' }
}
$python = Join-Path $venv 'Scripts\python.exe'
& $python -c 'import sys; sys.exit(0 if sys.version_info[:2] == (3, 12) else 1)'
if ($LASTEXITCODE -ne 0) { throw 'Нужен Python 3.12; существующий venv не изменён.' }
& $python -m pip install --require-hashes -r (Join-Path $hostRoot 'requirements.lock.txt')
if ($LASTEXITCODE -ne 0) { throw 'Не удалось установить фиксированные Python зависимости.' }
Push-Location $root
try {
    & npm ci
    if ($LASTEXITCODE -ne 0) { throw 'Ошибка npm ci MCP.' }
    & npm run build
    if ($LASTEXITCODE -ne 0) { throw 'Ошибка сборки MCP.' }
    Push-Location (Join-Path $root 'webui')
    try {
        & npm ci
        if ($LASTEXITCODE -ne 0) { throw 'Ошибка npm ci UI.' }
        & npm --prefix codegen ci
        if ($LASTEXITCODE -ne 0) { throw 'Ошибка установки codegen.' }
        & npm run build
        if ($LASTEXITCODE -ne 0) { throw 'Ошибка сборки UI.' }
    } finally { Pop-Location }
} finally { Pop-Location }
# Установка не запускает host, CorelDRAW, Credential Manager или регистрацию Codex.
