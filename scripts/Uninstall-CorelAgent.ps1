[CmdletBinding(SupportsShouldProcess)]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Assert-NoReparseAncestors([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $current = $full
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse/junction в цепочке удаления запрещён.' }
        }
        $parent = Split-Path -Parent $current
        if ($parent -eq $current) { break }
        $current = $parent
    }
}
Assert-NoReparseAncestors $root
$runtime = Join-Path $env:LOCALAPPDATA 'CorelDrawAgent\runtime.json'
$mcpOwner = Join-Path $env:LOCALAPPDATA 'CorelDrawAgent\mcp-owner.json'
if ((Test-Path -LiteralPath $runtime) -or (Test-Path -LiteralPath $mcpOwner)) {
    throw 'Сначала штатно остановите и проверьте владельцев host/MCP. Удаление работающего runtime запрещено.'
}
$targets = @()
foreach ($relative in @('webhost\.venv', 'webui\dist', 'webui\node_modules')) {
    $target = Join-Path $root $relative
    Assert-NoReparseAncestors $target
    if (-not ([IO.Path]::GetFullPath($target)).StartsWith(([IO.Path]::GetFullPath($root) + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) { throw 'Назначение удаления вне проекта.' }
    if (Test-Path -LiteralPath $target) {
        $queue = [Collections.Generic.Queue[string]]::new()
        $queue.Enqueue($target)
        while ($queue.Count) {
            $directory = $queue.Dequeue()
            foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
                if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Вложенный reparse point: удаление запрещено.' }
                if ($entry.PSIsContainer) { $queue.Enqueue($entry.FullName) }
            }
        }
        $targets += $target
    }
}
foreach ($target in $targets) {
    Assert-NoReparseAncestors $target
    if ($PSCmdlet.ShouldProcess($target, 'Удалить только runtime/build панели')) { Remove-Item -LiteralPath $target -Recurse -Force }
}
# История, credentials, исходники, Corel runs и регистрация Codex сохраняются.
