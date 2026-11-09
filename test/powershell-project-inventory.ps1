$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../powershell/CorelAutomation.psm1') -Force
& (Get-Module CorelAutomation) {
    function Collection($items) {
        $c = [pscustomobject]@{ Count = @($items).Count; Values = @($items) }
        $c | Add-Member ScriptMethod Item { param($i) return $this.Values[$i - 1] }
        return $c
    }
    $user = [pscustomobject]@{ Name = 'UserGMS_keep'; FullFileName = 'C:\user.gms' }
    $temp = [pscustomobject]@{ Name = 'CorelMcp_a1b2c3d4e5f6'; FullFileName = 'C:\runs\CorelMcp_a1b2c3d4e5f6.gms' }
    $renamed = [pscustomobject]@{ Name = 'Renamed'; FullFileName = 'C:\runs\CorelMcp_111111111111.gms' }
    $user | Add-Member ScriptMethod Unload { throw 'Must never unload user projects' }
    $temp | Add-Member ScriptMethod Unload { throw 'Inventory must not mutate projects' }
    $vbUser = [pscustomobject]@{ Name = 'UserGMS_keep'; Mode = 2 }
    $vbTemp = [pscustomobject]@{ Name = 'CorelMcp_a1b2c3d4e5f6'; Mode = 2 }
    $script:Corel = [pscustomobject]@{ GMSManager = [pscustomobject]@{ Projects = (Collection @($user, $temp, $renamed)) }; VBE = [pscustomobject]@{ VBProjects = (Collection @($vbUser, $vbTemp)) } }
    $before = $script:Corel.GMSManager.Projects.Count
    $r = Get-TemporaryProjectInventory
    if (-not $r.complete -or -not $r.idle -or $r.gms.Count -ne 2 -or $r.vbe_names.Count -ne 1) { throw 'Incomplete inventory' }
    if ($r.gms[1].name -ne 'Renamed' -or $script:Corel.GMSManager.Projects.Count -ne $before) { throw 'Renamed project missing or inventory mutated' }
    if (($r | ConvertTo-Json -Depth 10) -match 'UserGMS_keep') { throw 'Unrelated project names must not enter inventory result' }
    $vbTemp.Mode = 1
    $r = Get-TemporaryProjectInventory
    if (-not $r.complete -or $r.idle) { throw 'Running VBA must prevent confirmation' }
    $vbTemp.Mode = 2
    $script:Corel.GMSManager.Projects = Collection @($user)
    $script:Corel.VBE.VBProjects = Collection @($vbUser)
    $r = Get-TemporaryProjectInventory
    if (-not $r.complete -or $r.gms.Count -ne 0 -or $r.vbe_names.Count -ne 0) { throw 'Empty inventory must be explicit' }
    $script:Corel.GMSManager.Projects | Add-Member ScriptMethod Item { param($i) throw 'Synthetic enumeration failure' } -Force
    $r = Get-TemporaryProjectInventory
    if ($r.complete) { throw 'Enumeration failure must not prove absence' }
    $script:Corel.GMSManager.Projects = Collection @($user)
    $script:Corel.GMSManager.Projects | Add-Member ScriptMethod Item { param($i) $this.Count = 0; return $this.Values[$i - 1] } -Force
    $r = Get-TemporaryProjectInventory
    if ($r.complete) { throw 'Changing inventory must not prove absence' }
}
