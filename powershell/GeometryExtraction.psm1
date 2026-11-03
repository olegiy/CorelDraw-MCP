#requires -Version 5.1
Set-StrictMode -Version Latest
function Throw-GeometryError([string]$Code,[string]$Message) {
 $e=New-Object InvalidOperationException($Message);$e.Data['code']=$Code;throw $e
}
function Get-GeometrySelection($Document) {
 $ids=New-Object 'System.Collections.Generic.List[long]'
 for($i=1;$i -le $Document.SelectionRange.Count;$i++){$ids.Add([long]$Document.SelectionRange.Item($i).StaticID)}
 return ,@($ids.ToArray())
}
function Find-GeometryShape($Shapes,[long]$Id,[ref]$Visited,[int]$Depth=0) {
 if($Depth -gt 32){Throw-GeometryError 'GEOMETRY_LIMIT_EXCEEDED' 'Слишком большая глубина групп.'}
 for($i=1;$i -le $Shapes.Count;$i++){
  if(++$Visited.Value -gt 50000){Throw-GeometryError 'GEOMETRY_LIMIT_EXCEEDED' 'Превышен лимит обхода объектов.'}
  $s=$Shapes.Item($i)
  if([long]$s.StaticID -eq $Id){return $s}
  if([int]$s.Type -eq 7){$found=Find-GeometryShape $s.Shapes $Id $Visited ($Depth+1);if($null -ne $found){return $found}}
 }
 return $null
}
function Assert-GeometrySupported($Shape,[long]$SourceId,[ref]$Visited,[int]$Depth=0) {
 if($Depth -gt 32 -or ++$Visited.Value -gt 50000){Throw-GeometryError 'GEOMETRY_LIMIT_EXCEEDED' 'Превышен лимит проверки геометрии.'}
 $type=[int]$Shape.Type
 if(-not [bool]$Shape.Visible -or -not [bool]$Shape.Layer.Visible){Throw-GeometryError 'UNSUPPORTED_GEOMETRY' ('Объект StaticID='+$SourceId+' или его слой скрыт.')}
 if($type -notin @(1,2,3,4,6,7)){Throw-GeometryError 'UNSUPPORTED_GEOMETRY' ('Неподдерживаемый тип объекта StaticID='+$SourceId+': '+$type)}
 if([int]$Shape.Effects.Count -gt 0){Throw-GeometryError 'UNSUPPORTED_GEOMETRY' ('Эффекты объекта StaticID='+$SourceId+' не поддерживаются.')}
 if($null -ne $Shape.PowerClip){Throw-GeometryError 'UNSUPPORTED_GEOMETRY' ('PowerClip объекта StaticID='+$SourceId+' не поддерживается.')}
 if([int]$Shape.Transparency.Type -ne 0){Throw-GeometryError 'UNSUPPORTED_GEOMETRY' ('Прозрачность объекта StaticID='+$SourceId+' не поддерживается.')}
 if($type -ne 7 -and [int]$Shape.Fill.Type -eq 0 -and [int]$Shape.Outline.Type -eq 0){Throw-GeometryError 'UNSUPPORTED_GEOMETRY' ('Объект StaticID='+$SourceId+' не имеет видимой заливки или обводки.')}
 if($type -eq 7){for($i=1;$i -le $Shape.Shapes.Count;$i++){Assert-GeometrySupported $Shape.Shapes.Item($i) ([long]$Shape.Shapes.Item($i).StaticID) $Visited ($Depth+1)}}
}
function Collect-GeometryUnsupported($Shape,[long]$SourceId,[ref]$Visited,[System.Collections.Generic.List[string]]$Items,[int]$Depth=0) {
 if($Depth -gt 32 -or ++$Visited.Value -gt 50000){Throw-GeometryError 'GEOMETRY_LIMIT_EXCEEDED' 'Превышен лимит проверки геометрии.'}
 $type=[int]$Shape.Type
 if(-not [bool]$Shape.Visible -or -not [bool]$Shape.Layer.Visible){$Items.Add(('StaticID='+$SourceId+': объект или слой скрыт'))}
 if($type -notin @(1,2,3,4,6,7)){$Items.Add(('StaticID='+$SourceId+': неподдерживаемый тип '+$type))}
 if([int]$Shape.Effects.Count -gt 0){$Items.Add(('StaticID='+$SourceId+': эффекты'))}
 if($null -ne $Shape.PowerClip){$Items.Add(('StaticID='+$SourceId+': PowerClip'))}
 if([int]$Shape.Transparency.Type -ne 0){$Items.Add(('StaticID='+$SourceId+': прозрачность'))}
 if($type -ne 7 -and [int]$Shape.Fill.Type -eq 0 -and [int]$Shape.Outline.Type -eq 0){$Items.Add(('StaticID='+$SourceId+': нет видимой заливки или обводки'))}
 if($type -eq 7){for($i=1;$i -le $Shape.Shapes.Count;$i++){ $child=$Shape.Shapes.Item($i); Collect-GeometryUnsupported $child ([long]$child.StaticID) $Visited $Items ($Depth+1)}}
}
function Assert-GeometryCopy($Source,$Copy,[double]$MmPerUnit,[int]$Depth=0) {
 if($null -eq $Copy -or $Depth -gt 32 -or [int]$Source.Type -ne [int]$Copy.Type){Throw-GeometryError 'COPY_GEOMETRY_MISMATCH' 'Копирование изменило тип или структуру объекта.'}
 foreach($name in @('LeftX','BottomY','SizeWidth','SizeHeight')){
  $a=[double]$Source.$name;$b=[double]$Copy.$name
  if([double]::IsNaN($a) -or [double]::IsNaN($b) -or [double]::IsInfinity($a) -or [double]::IsInfinity($b) -or [Math]::Abs(($a-$b)*$MmPerUnit) -gt 0.0000001){Throw-GeometryError 'COPY_GEOMETRY_MISMATCH' ('Копирование изменило физическую геометрию: '+$name)}
 }
 if([Math]::Abs([double]$Source.RotationAngle-[double]$Copy.RotationAngle) -gt 0.0000001){Throw-GeometryError 'COPY_GEOMETRY_MISMATCH' 'Копирование изменило поворот объекта.'}
 if([int]$Source.Type -eq 7){
  if($Source.Shapes.Count -ne $Copy.Shapes.Count){Throw-GeometryError 'COPY_GEOMETRY_MISMATCH' 'Копирование изменило состав группы.'}
  for($i=1;$i -le $Source.Shapes.Count;$i++){Assert-GeometryCopy $Source.Shapes.Item($i) $Copy.Shapes.Item($i) $MmPerUnit ($Depth+1)}
 }
 if([int]$Source.Type -eq 3){
  if($Source.Curve.Segments.Count -ne $Copy.Curve.Segments.Count){Throw-GeometryError 'COPY_GEOMETRY_MISMATCH' 'Копирование изменило сегменты кривой.'}
  for($i=1;$i -le $Source.Curve.Segments.Count;$i++){
   $a=$Source.Curve.Segments.Item($i);$b=$Copy.Curve.Segments.Item($i)
   if([int]$a.Type -ne [int]$b.Type){Throw-GeometryError 'COPY_GEOMETRY_MISMATCH' 'Копирование изменило тип сегмента.'}
   foreach($node in @('StartNode','EndNode')){foreach($coord in @('PositionX','PositionY')){if([Math]::Abs(([double]$a.$node.$coord-[double]$b.$node.$coord)*$MmPerUnit) -gt 0.0000001){Throw-GeometryError 'COPY_GEOMETRY_MISMATCH' 'Копирование изменило узлы кривой.'}}}
   if([int]$a.Type -eq 1){foreach($coord in @('StartingControlPointX','StartingControlPointY','EndingControlPointX','EndingControlPointY')){if([Math]::Abs(([double]$a.$coord-[double]$b.$coord)*$MmPerUnit) -gt 0.0000001){Throw-GeometryError 'COPY_GEOMETRY_MISMATCH' 'Копирование изменило контрольные точки.'}}}
  }
 }
}
function Invoke-GeometrySnapshot {
 [CmdletBinding()]
 param($Application,$Document,[hashtable]$Parameters,[string]$RunDirectory,[scriptblock]$RunVba,[scriptblock]$CleanupError=$null)
 $ErrorActionPreference='Stop'
 if($null -eq $RunVba){Throw-GeometryError 'GEOMETRY_EXECUTOR_UNAVAILABLE' 'Недоступен исполнитель VBA для извлечения геометрии.'}
 $previousDocument=$Application.ActiveDocument;$previousPage=$Document.ActivePage
 $sourceDirty=[bool]$Document.Dirty;$selection=Get-GeometrySelection $Document
 $temp=$null;$result=$null;$primary=$null;$cleanupFailures=New-Object 'System.Collections.Generic.List[object]'
 $part=Join-Path ([IO.Path]::GetFullPath($RunDirectory)) ('geometry-'+[guid]::NewGuid().ToString('N')+'.part')
 $path=[IO.Path]::ChangeExtension($part,'.json')
 try {
  $page=$Document.ActivePage
  if($Parameters.ContainsKey('page_index') -and [string]$Parameters.page_index -ne 'current'){$page=$Document.Pages.Item([int]$Parameters.page_index)}
  $targets=New-Object 'System.Collections.Generic.List[object]'
  if($Parameters.ContainsKey('static_ids')){
   $ids=@($Parameters.static_ids);if($ids.Count -lt 1 -or $ids.Count -gt 100 -or @($ids | Select-Object -Unique).Count -ne $ids.Count){Throw-GeometryError 'GEOMETRY_INVALID_INPUT' 'Требуется от 1 до 100 уникальных StaticID.'}
   foreach($id in $ids){$visited=0;$s=Find-GeometryShape $page.Shapes ([long]$id) ([ref]$visited);if($null -eq $s){Throw-GeometryError 'SHAPE_NOT_FOUND' ('Не найден StaticID='+$id+' на выбранной странице.')};$targets.Add($s)}
  } else {
   if([int]$page.Index -ne [int]$Document.ActivePage.Index){Throw-GeometryError 'SELECTION_PAGE_MISMATCH' 'Выделение доступно только на текущей странице.'}
   if($Document.SelectionRange.Count -eq 0){Throw-GeometryError 'EMPTY_SELECTION' 'В документе не выделены объекты.'}
   if($Document.SelectionRange.Count -gt 100){Throw-GeometryError 'GEOMETRY_LIMIT_EXCEEDED' 'Допускается не более 100 объектов.'}
   for($i=1;$i -le $Document.SelectionRange.Count;$i++){$s=$Document.SelectionRange.Item($i);if([int]$s.Page.Index -ne [int]$page.Index){Throw-GeometryError 'SELECTION_PAGE_MISMATCH' 'Выделение содержит объекты другой страницы.'};$targets.Add($s)}
  }
  $visited=0;$unsupported=New-Object 'System.Collections.Generic.List[string]'
  foreach($s in $targets){Collect-GeometryUnsupported $s ([long]$s.StaticID) ([ref]$visited) $unsupported}
  if($unsupported.Count -gt 0){Throw-GeometryError 'UNSUPPORTED_GEOMETRY' ('Неподдерживаемые объекты: '+($unsupported -join '; '))}
  $mm=[double]$Application.ConvertUnits(1,[int]$Document.Unit,3)
  if($mm -le 0 -or [double]::IsNaN($mm) -or [double]::IsInfinity($mm)){Throw-GeometryError 'GEOMETRY_INVALID_UNITS' 'Не удалось определить физические единицы документа.'}
  # CopyToLayer cannot cross document boundaries. CreateDocumentFrom clones a
  # range into its own document without selecting or modifying the source.
  $range=$Application.CreateShapeRange()
  foreach($s in $targets){$range.Add($s)}
  $temp=$range.CreateDocumentFrom($false)
  if($null -eq $temp){Throw-GeometryError 'COPY_GEOMETRY_MISMATCH' 'Не удалось создать изолированную копию документа.'}
  # The cloned document starts with Corel's default display unit. Align it
  # with the source before checking physical coordinates.
  $temp.Unit=$Document.Unit
  if([int]$temp.Unit -ne [int]$Document.Unit){Throw-GeometryError 'COPY_GEOMETRY_MISMATCH' 'Изолированная копия изменила единицы измерения.'}
  $mapping=New-Object Text.StringBuilder
  foreach($s in $targets){
   $visited=0;$copy=Find-GeometryShape $temp.ActivePage.Shapes ([long]$s.StaticID) ([ref]$visited)
   if($null -eq $copy){Throw-GeometryError 'COPY_GEOMETRY_MISMATCH' ('Изолированная копия не сохранила StaticID='+$s.StaticID)}
   Assert-GeometryCopy $s $copy $mm
   [void]$mapping.AppendLine(('    WriteObject FindRoot({0}), {1}' -f [long]$copy.StaticID,[long]$s.StaticID))
  }
  $template=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'M_GeometrySnapshot.bas'))
  $code=$template.Replace('@@OUTPUT@@',$part.Replace('"','""')).Replace('@@OBJECTS@@',$mapping.ToString())
  $vbaResult=& $RunVba $code $temp $true
  if($null -eq $vbaResult -or $vbaResult.value_json -notmatch '"written"\s*:\s*true'){Throw-GeometryError 'GEOMETRY_INVALID_RESULT' 'VBA не подтвердил запись геометрии.'}
  if(-not [IO.File]::Exists($part) -or (Get-Item -LiteralPath $part).Length -eq 0){Throw-GeometryError 'GEOMETRY_SNAPSHOT_MISSING' 'Не создан файл геометрии.'}
  if((Get-Item -LiteralPath $part).Length -gt 67108864){Throw-GeometryError 'GEOMETRY_LIMIT_EXCEEDED' 'Файл геометрии превышает 64 МиБ.'}
  [IO.File]::Move($part,$path)
  $result=@{snapshot_path=$path;original_state_preserved=$true}
 } catch {
  $primary=$_.Exception
  if([string]$primary.Message -match '(GEOMETRY_LIMIT_EXCEEDED|GEOMETRY_SNAPSHOT_MISSING|UNSUPPORTED_GEOMETRY):'){$primary.Data['code']=$Matches[1]}
 }
 finally {
  $steps=@(
   @{name='geometry.temporary_document';action={if($null -ne $temp){$temp.Dirty=$false;$temp.Close()}}},
   @{name='geometry.page';action={if([int]$Document.ActivePage.Index -ne [int]$previousPage.Index){$previousPage.Activate()}}},
   @{name='geometry.document';action={if($null -ne $previousDocument){$previousDocument.Activate()}}},
   @{name='geometry.source_state';action={if([bool]$Document.Dirty -ne $sourceDirty -or ((Get-GeometrySelection $Document) -join ',') -ne ($selection -join ',')){Throw-GeometryError 'GEOMETRY_SOURCE_CHANGED' 'Состояние исходного документа изменилось во время измерения.'}}},
   @{name='geometry.partial_file';action={if([IO.File]::Exists($part)){[IO.File]::Delete($part)}}}
  )
  foreach($step in $steps){try{& $step.action | Out-Null}catch{$cleanupFailures.Add(@{step=$step.name;exception=$_.Exception});if($null -ne $CleanupError){try{& $CleanupError $step.name $_.Exception | Out-Null}catch{}}}}
 }
 if($cleanupFailures.Count -gt 0 -and $null -ne $primary){$primary.Data['cleanup_errors']=@($cleanupFailures | ForEach-Object {@{step=$_.step;code=[string]$_.exception.Data['code'];message=[string]$_.exception.Message}})}
 if($null -ne $primary){throw $primary}
 if($cleanupFailures.Count -gt 0){Throw-GeometryError 'GEOMETRY_CLEANUP_FAILED' 'Не удалось подтвердить сохранность состояния после измерения.'}
 return $result
}
Export-ModuleMember -Function Invoke-GeometrySnapshot
