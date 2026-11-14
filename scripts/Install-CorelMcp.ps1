#requires -Version 5.1
[CmdletBinding()]
param([switch] $Uninstall, [switch] $SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    $major = & node -p "process.versions.node.split('.')[0]"
    if ($LASTEXITCODE -ne 0 -or $major -ne '24') { throw 'Сначала установите Node.js 24.' }
    & node (Join-Path $PSScriptRoot 'check-preupgrade.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Проверка прежних заданий/владельцев не пройдена; установка не начата.' }
    if (-not $Uninstall) {
        & node (Join-Path $PSScriptRoot 'worker-package.mjs')
        if ($LASTEXITCODE -ne 0) { throw 'Готовый комплект C#-исполнителя не подтверждён; установка не начата.' }
    }
    if (-not $Uninstall -and -not $SkipBuild) {
        & npm.cmd ci --ignore-scripts
        if ($LASTEXITCODE -ne 0) { throw 'Ошибка установки фиксированных зависимостей.' }
        & npm.cmd run build
        if ($LASTEXITCODE -ne 0) { throw 'Ошибка сборки TypeScript.' }
    }
    $installArgs = @((Join-Path $PSScriptRoot 'install-codex.mjs'))
    if ($Uninstall) { $installArgs += '--uninstall' }
    & node @installArgs
    if ($LASTEXITCODE -ne 0) { throw 'Ошибка регистрации Codex; исходные данные сохранены.' }
}
finally { Pop-Location }
