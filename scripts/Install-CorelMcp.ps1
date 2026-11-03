#requires -Version 5.1
[CmdletBinding()]
param([switch] $Uninstall, [switch] $SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    $major = & node -p "process.versions.node.split('.')[0]"
    if ($LASTEXITCODE -ne 0 -or $major -ne '24') { throw 'Install Node.js 24 first.' }
    if (-not $Uninstall -and -not $SkipBuild) {
        & npm.cmd ci --ignore-scripts
        if ($LASTEXITCODE -ne 0) { throw 'Dependency installation failed.' }
        & npm.cmd run build
        if ($LASTEXITCODE -ne 0) { throw 'TypeScript build failed.' }
    }
    $installArgs = @((Join-Path $PSScriptRoot 'install-codex.mjs'))
    if ($Uninstall) { $installArgs += '--uninstall' }
    & node @installArgs
    if ($LASTEXITCODE -ne 0) { throw 'Codex registration failed.' }
}
finally { Pop-Location }
