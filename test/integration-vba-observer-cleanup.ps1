#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string] $Ledger)
$ErrorActionPreference = 'Stop'
$items = @([IO.File]::ReadAllText($Ledger, [Text.Encoding]::UTF8) | ConvertFrom-Json)
$mutex = New-Object Threading.Mutex($false, 'Local\CorelDrawMcp_v27')
$owned = $false
try {
    $owned = $mutex.WaitOne(0)
    if (-not $owned) { throw 'Исполнитель ещё занят: очистка запрещена.' }
    $app = [Runtime.InteropServices.Marshal]::GetActiveObject('CorelDRAW.Application.27')
    if ([string]$app.Version -notmatch '^27\.') { throw 'Неверная версия CorelDRAW.' }
    foreach ($item in $items) {
        if ($item.name -cnotmatch '^CorelMcp_[a-f0-9]{12}$' -or [IO.Path]::GetFileName($item.path) -cne ($item.name + '.gms')) { throw 'Неверный ledger GMS.' }
        for ($i = $app.GMSManager.Projects.Count; $i -ge 1; $i--) {
            $project = $app.GMSManager.Projects.Item($i)
            if ($project.Name -ceq $item.name) {
                if (-not [string]::Equals([IO.Path]::GetFullPath($project.FullFileName), [IO.Path]::GetFullPath($item.path), [StringComparison]::OrdinalIgnoreCase)) { throw 'Identity GMS не совпала.' }
                $project.Unload()
            }
        }
    }
} finally {
    if ($owned) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
