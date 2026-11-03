$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '../powershell/CorelAutomation.psm1') -Force
& (Get-Module CorelAutomation) {
 function Collection($items) { $c=[pscustomobject]@{ Count=@($items).Count; Values=@($items) }; $c | Add-Member ScriptMethod Item { param($i) return $this.Values[$i-1] }; return $c }
 function Shape($id,$type,$name,[object[]]$children=@()) { return [pscustomobject]@{StaticID=$id;Type=$type;Name=$name;Layer=[pscustomobject]@{Name='L'};LeftX=1.0;BottomY=2.0;SizeWidth=3.0;SizeHeight=4.0;RotationAngle=30.0;Fill=[pscustomobject]@{Type=1};Outline=[pscustomobject]@{Type=1;Width=.1};Shapes=(Collection $children);Text=[pscustomobject]@{Story=[pscustomobject]@{Text='abcdefgh'}}} }
 $leaf=Shape 3 6 'Match child'
 $nested=Shape 2 7 'nested' @($leaf)
 $group=Shape 1 7 'Match group' @($nested)
 $other=Shape 4 1 'other'
 $layer=[pscustomobject]@{Name='L';Visible=$true;Editable=$true;Printable=$true;Shapes=(Collection @($group,$other))}
 $page=[pscustomobject]@{Index=1;Name='Page';SizeWidth=10.0;SizeHeight=20.0;Shapes=(Collection @($group,$other));Layers=(Collection @($layer))}
 $selection=Collection @($leaf); $selection | Add-Member NoteProperty LeftX 1.0; $selection | Add-Member NoteProperty BottomY 2.0; $selection | Add-Member NoteProperty SizeWidth 3.0; $selection | Add-Member NoteProperty SizeHeight 4.0
 $doc=[pscustomobject]@{Name='fixture';FullFileName='';Dirty=$false;Pages=(Collection @($page));ActivePage=$page;Unit=1;ReferencePoint=9;PreserveSelection=$true;SelectionRange=$selection}
 $script:Corel=[pscustomobject]@{}; $script:Corel | Add-Member ScriptMethod ConvertUnits {param($n,$from,$to) return $n*25.4}
 function Get-ComIdentity($value) { return 'fixture' }
 $script:Documents=@{ fixture=@{identity='fixture';document=$doc} }
 $base=@{limit=10;offset=0;recursive=$true;max_depth=8;max_visited=100;include_text=$true;text_limit=3}
 $r=Inspect-Document $doc $base
 if (($r.shapes.static_id -join ',') -ne '1,2,3,4') { throw 'Expected DFS traversal' }
 if ($r.shapes[2].parent_static_id -ne 2 -or $r.shapes[2].text -ne 'abc' -or -not $r.shapes[2].text_truncated) { throw 'Missing hierarchy or text limits' }
 if ([Math]::Abs($r.shapes[2].bounds_mm.width-76.2) -gt .0001) { throw 'Incorrect mm conversion' }
 $base.name_filter='MATCH'; $base.shape_types=@(6); $base.static_ids=@(3)
 $r=Inspect-Document $doc $base
 if ($r.shapes.Count -ne 1 -or $r.shapes[0].static_id -ne 3) { throw 'Filters did not combine' }
 $base.Remove('name_filter');$base.Remove('shape_types');$base.Remove('static_ids');$base.max_depth=1
 $r=Inspect-Document $doc $base
 if (-not $r.depth_truncated -or ($r.shapes.static_id -join ',') -ne '1,2,4') { throw 'Depth limit broken' }
 $base.max_depth=8;$base.max_visited=100
 $many=@();for($i=1;$i -le 101;$i++){$many+=Shape ($i+10) 1 'visit'}; $page.Shapes=Collection $many
 $r=Inspect-Document $doc $base
 if (-not $r.visited_truncated -or $r.visited_shapes -ne 100) { throw 'Visit cap broken' }
 $page.Shapes=Collection @($group,$other)
 $r=Inspect-Document $doc @{limit=1;offset=1}
 if ($r.shapes[0].static_id -ne 4 -or $r.total_shapes -ne 2 -or $null -ne $r.next_offset) { throw 'Legacy pagination broken' }
}
