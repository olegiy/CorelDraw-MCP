[CmdletBinding(SupportsShouldProcess)]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$runtime = Join-Path $env:LOCALAPPDATA 'CorelDrawAgent\runtime.json'
$mcpOwner = Join-Path $env:LOCALAPPDATA 'CorelDrawAgent\mcp-owner.json'
if ((Test-Path -LiteralPath $runtime) -or (Test-Path -LiteralPath $mcpOwner)) {
    throw 'Сначала штатно остановите и проверьте владельцев host/MCP. Удаление работающего runtime запрещено.'
}
foreach ($relative in @('webhost\.venv', 'webui\dist', 'webui\node_modules')) {
    $target = Join-Path $root $relative
    if (Test-Path -LiteralPath $target) {
        $item = Get-Item -LiteralPath $target -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Удаление reparse/junction запрещено.' }
        if ($PSCmdlet.ShouldProcess($target, 'Удалить только runtime/build панели')) {
            Remove-Item -LiteralPath $target -Recurse -Force
        }
    }
}
# История, credentials, исходники, Corel runs и регистрация Codex сохраняются.
