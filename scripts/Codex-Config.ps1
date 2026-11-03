[CmdletBinding()]
param(
    [ValidateSet('List', 'Add', 'Remove')] [string] $Action,
    [string] $NodePath,
    [string] $ServerPath
)
$ErrorActionPreference = 'Stop'
if (-not (Get-Command codex -ErrorAction SilentlyContinue)) { throw 'Codex CLI is not on PATH.' }
switch ($Action) {
    'List' { & codex mcp list --json }
    'Add' { & codex mcp add coreldraw -- $NodePath $ServerPath }
    'Remove' { & codex mcp remove coreldraw }
}
exit $LASTEXITCODE
