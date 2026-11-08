$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '../powershell/CorelAutomation.psm1') -Force
Add-Type @'
using System.Collections;
public class ExportCollection : IEnumerable {
 public object[] Values; public int Count { get { return Values.Length; } }
 public ExportCollection(object[] values) { Values=values; }
 public object Item(int i) { return Values[i-1]; }
 public IEnumerator GetEnumerator() { return Values.GetEnumerator(); }
}
'@
& (Get-Module CorelAutomation) {
 $script:Cleanup=@{completed=$true;errors=@()};$script:Warnings=@();$script:Artifacts=@()
 $script:FailExport=$false;$script:RaceDestination=$null
 $page=[pscustomobject]@{Index=1;SizeWidth=25.4;SizeHeight=12.7};$page|Add-Member ScriptMethod Activate {}
 $doc=[pscustomobject]@{ActivePage=$page;Unit=3};$doc|Add-Member ScriptMethod Activate {}
 $script:Corel=[pscustomobject]@{ActiveDocument=$doc};$script:Corel|Add-Member ScriptMethod ConvertUnits {param($n,$from,$to) if($to -eq 1){return $n/25.4};return $n}
  function Invoke-Vba($Parameters,$RunDirectory,$Execute,$Document,$ReadOnly) {
  if(-not $ReadOnly){throw 'Export must bypass source Undo/settings mutations'}
  $code=[string]$Parameters.code
  $syntaxErrors=$null;$tokens=$null
  if($code -match 'ExportBitmap\("([^"]+)"'){[IO.File]::WriteAllText($matches[1],'fixture');$script:LastExport=$code}
  elseif($code -match 'ExportEx\("([^"]+)"'){[IO.File]::WriteAllText($matches[1],'fixture');$script:LastExport=$code}
   else{throw 'Missing native export invocation'}
   if ($script:FailExport) { throw 'Synthetic filter failure' }
   if ($script:RaceDestination) { [IO.File]::WriteAllText($script:RaceDestination, 'appeared during export') }
 }
 $dir=Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N'));[IO.Directory]::CreateDirectory($dir)|Out-Null
  try {
   $destination = Join-Path $dir "sentinel ' file.png"
   [IO.File]::WriteAllText($destination, 'original sentinel')
   $null = Export-Document $doc @{format='png';path=$destination;range='page'} $dir $false
   if ([IO.File]::ReadAllText($destination) -ne 'fixture') { throw 'Automatic export replacement failed' }
   [IO.File]::Delete($destination)
   $script:RaceDestination = $destination
   $null = Export-Document $doc @{format='png';path=$destination;range='page'} $dir $false
   if ([IO.File]::ReadAllText($destination) -ne 'fixture') { throw 'Destination race was not replaced' }
   $script:RaceDestination = $null
   [IO.File]::WriteAllText($destination, 'preserve sentinel')
   $script:FailExport = $true
   try { $null = Export-Document $doc @{format='png';path=$destination;range='page'} $dir $false; throw 'Expected filter failure' }
   catch { if ($_.Exception.Message -notmatch 'Synthetic filter failure') { throw } }
   $script:FailExport = $false
   if ([IO.File]::ReadAllText($destination) -ne 'preserve sentinel') { throw 'Filter failure replaced destination' }
   $savePath = Join-Path $dir 'sentinel.cdr'
   [IO.File]::WriteAllText($savePath, 'original CDR')
   function Sync-Documents {}
   function Get-ComIdentity($Value) { return $Value.identity }
   function Get-DocumentInfo($Value) { return @{path=$Value.FullFileName} }
   $script:Documents = @{}
   $doc | Add-Member NoteProperty FullFileName ''
   $doc | Add-Member NoteProperty identity 1
   $doc | Add-Member ScriptMethod SaveAs {param($path,$options) if (-not $options.Overwrite) { throw 'SaveAs Overwrite must be true' }; [IO.File]::WriteAllText($path,'saved CDR'); $this.FullFileName=$path }
   $script:Corel | Add-Member ScriptMethod CreateStructSaveAsOptions { return [pscustomobject]@{Overwrite=$false;Filter=0} }
   $null = Save-Document $doc @{path=$savePath}
   if ([IO.File]::ReadAllText($savePath) -ne 'saved CDR') { throw 'Automatic SaveAs replacement failed' }
   $other = [pscustomobject]@{identity=2;FullFileName=$savePath}
   $script:Documents.other = @{document=$other}
   try { $null = Save-Document $doc @{path=$savePath}; throw 'Expected DOCUMENT_ALREADY_OPEN' }
   catch { if ($_.Exception.Data['code'] -ne 'DOCUMENT_ALREADY_OPEN') { throw } }
   $script:Documents = @{}
  foreach($spec in @(@('jpeg','jpeg','cdrJPEG'),@('tif','tif','cdrTIFF'),@('ai','ai','cdrAI'),@('eps','eps','cdrEPS'),@('svg','svg','cdrSVG'))){
    $p=@{format=$spec[0];range='page';path=(Join-Path $dir ('out.'+$spec[1]));dpi=150;color_mode='cmyk';jpeg_compression=67;tiff_compression='none';use_document_profile=$true;text_mode='preserve'}
   $r=Export-Document $doc $p $dir $false
   if($script:LastExport -notmatch $spec[2]){throw ('Wrong filter '+$spec[0])}
   if(-not [IO.File]::Exists($r.path)){throw 'Missing committed export'}
   if($spec[0] -eq 'jpeg' -and $script:LastExport -notmatch 'f.Compression = 67'){throw 'JPEG compression ignored'}
   if($spec[0] -eq 'tif' -and $script:LastExport -notmatch 'Compression:=cdrCompressionNone'){throw 'TIFF compression ignored'}
   if($spec[0] -in @('ai','eps') -and $script:LastExport -notmatch 'f.TextAsCurves = False'){throw 'Text preservation must override native filter preference'}
  }
  $copy=[pscustomobject]@{StaticID=42;LeftX=1.;BottomY=2.;SizeWidth=3.;SizeHeight=4.;RotationAngle=10.;Type=1}
  $original=$copy.PSObject.Copy();$original|Add-Member NoteProperty Target $copy;$original|Add-Member ScriptMethod CopyToLayer {param($layer) throw 'Cross-document CopyToLayer is forbidden'}
  $null=Assert-ExportGeometry $original $copy 1
  $copy.LeftX=5
  try{$null=Assert-ExportGeometry $original $copy 1;throw 'Expected geometry failure'}catch{if($_.Exception.Data['code'] -ne 'COPY_GEOMETRY_MISMATCH'){throw}}
  function List($items){return ,(New-Object ExportCollection -ArgumentList (,@($items)))}
  $copy.LeftX=1;$copy.Type=6;$copy|Add-Member NoteProperty Converted $false;$copy|Add-Member ScriptMethod ConvertToCurves {$this.Converted=$true}
  $original.Type=6;$original|Add-Member NoteProperty Converted $false;$original|Add-Member ScriptMethod ConvertToCurves {throw 'Source must never be converted'}
  $page|Add-Member NoteProperty Shapes (List @($original))
  $doc|Add-Member NoteProperty SelectionRange (List @($original));$doc|Add-Member NoteProperty ReferencePoint 9;$doc|Add-Member NoteProperty DrawingOriginX 0.0;$doc|Add-Member NoteProperty DrawingOriginY 0.0
  $tempPage=[pscustomobject]@{Index=1;SizeWidth=25.4;SizeHeight=12.7;Shapes=(List @($copy))};$tempPage|Add-Member ScriptMethod SetSize {param($w,$h)$this.SizeWidth=$w;$this.SizeHeight=$h}
  $temporary=[pscustomobject]@{ActivePage=$tempPage;ActiveLayer=[pscustomobject]@{};Unit=3;ReferencePoint=0;DrawingOriginX=0.0;DrawingOriginY=0.0;Dirty=$true;Closed=$false}
  $temporary|Add-Member ScriptMethod Activate {};$temporary|Add-Member ScriptMethod Close {$this.Closed=$true}
  $rangeObject=[pscustomobject]@{Temporary=$temporary;Added=(New-Object 'Collections.Generic.List[object]')};$rangeObject|Add-Member ScriptMethod Add {param($shape)$this.Added.Add($shape)};$rangeObject|Add-Member ScriptMethod CreateDocumentFrom {param($flag)if($flag){throw 'Must not modify source'};return $this.Temporary}
  $script:Corel|Add-Member NoteProperty RangeObject $rangeObject;$script:Corel|Add-Member ScriptMethod CreateShapeRange {return $this.RangeObject}
  foreach($format in @('dxf','plt','ai','eps')){
   $temporary.Closed=$false;$copy.Converted=$false
   $r=Export-Document $doc @{format=$format;range='selection';path=(Join-Path $dir ('curves.'+$format));overwrite=$false;text_mode='curves'} $dir $false
   if($format -in @('ai','eps') -and $script:LastExport -notmatch 'f.TextAsCurves = True'){throw 'Vector curves mode was ignored'}
   if(-not $copy.Converted -or -not $temporary.Closed -or $original.Converted){throw 'Temporary conversion/cleanup invariant failed'}
   if($tempPage.SizeWidth -ne $page.SizeWidth -or $tempPage.SizeHeight -ne $page.SizeHeight){throw 'Page size not copied'}
   if($format -eq 'plt' -and $script:LastExport -notmatch 'f.PlotterUnits = 1000'){throw 'PLT plotter units were not fixed'}
  }
  $temporary.Closed=$false;$copy.Converted=$false;$copy.SizeWidth=123
  try{$r=Export-Document $doc @{format='dxf';range='selection';path=(Join-Path $dir 'wrong.dxf');overwrite=$false} $dir $false;throw 'Expected copy invariant failure'}catch{if($_.Exception.Data['code'] -ne 'COPY_GEOMETRY_MISMATCH'){throw}}
  if($copy.Converted -or -not $temporary.Closed -or [IO.File]::Exists((Join-Path $dir 'wrong.dxf'))){throw 'Failed copy must not convert or commit'}
 }
 finally{[IO.Directory]::Delete($dir,$true)}
}
