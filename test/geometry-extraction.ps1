$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '../powershell/GeometryExtraction.psm1') -Force
Add-Type @'
using System;
using System.Collections.Generic;
public class GeoCollection {
 public List<GeoShape> Items=new List<GeoShape>();
 public int Count {get{return Items.Count;}}
 public GeoShape Item(int i){return Items[i-1];}
}
public class GeoRange {
 public List<GeoShape> Items=new List<GeoShape>();public GeoApp App;
 public void Add(GeoShape s){Items.Add(s);}
 public GeoDoc CreateDocumentFrom(bool temporary){var d=App.CreateDocument();foreach(var s in Items){var c=s.CopyToLayer(d.ActiveLayer);c.StaticID=s.StaticID;}return d;}
}
public class GeoEffects {public int Count=0;}
public class GeoTransparency {public int Type=0;}
public class GeoOutline {public int Type=0;public double Width=0;}
public class GeoFill {public int Type=1;}
public class GeoShape {
 public bool Visible=true; public GeoLayer Layer=new GeoLayer();
 public int StaticID=1, Type=1; public double LeftX=0,BottomY=0,SizeWidth=10,SizeHeight=5,RotationAngle=0;
 public GeoCollection Shapes=new GeoCollection();public GeoEffects Effects=new GeoEffects();
 public GeoTransparency Transparency=new GeoTransparency();public GeoOutline Outline=new GeoOutline();
 public GeoFill Fill=new GeoFill();
 public object PowerClip=null; public GeoPage Page; public bool Mismatch=false;public static int Copies=0;
 public GeoShape CopyToLayer(GeoLayer layer){Copies++;var c=new GeoShape();c.StaticID=100+StaticID;c.LeftX=Mismatch?1:LeftX;c.SizeWidth=SizeWidth;c.SizeHeight=SizeHeight;c.Page=layer.Page;layer.Shapes.Items.Add(c);return c;}
}
public class GeoLayers {public GeoPage Page;public GeoLayer Add(string name){return Page.ActiveLayer;}}
public class GeoLayer {public bool Visible=true;public GeoPage Page;public GeoCollection Shapes=new GeoCollection();}
public class GeoPage {
 public int Index=1;public double SizeWidth=100,SizeHeight=100;public GeoDoc Document;
 public GeoLayer ActiveLayer;public GeoLayers Layers;public GeoCollection Shapes {get{return ActiveLayer.Shapes;}}
 public GeoPage(){ActiveLayer=new GeoLayer();ActiveLayer.Page=this;Layers=new GeoLayers();Layers.Page=this;}
 public void Activate(){Document.ActivePage=this;}
}
public class GeoPages {public GeoPage Page;public GeoPage Item(int i){if(i!=1)throw new Exception("page");return Page;}}
public class GeoDoc {
 public static bool FailNextClose=false;
 public int Unit=3;public bool Dirty=true,Closed=false;public GeoPage ActivePage;public GeoPages Pages=new GeoPages();
 public GeoCollection SelectionRange=new GeoCollection();public GeoApp App;
 public GeoDoc(){ActivePage=new GeoPage();ActivePage.Document=this;Pages.Page=ActivePage;}
 public GeoLayer ActiveLayer {get{return ActivePage.ActiveLayer;}}
 public void Activate(){App.ActiveDocument=this;}
 public void Close(){Closed=true;if(FailNextClose){FailNextClose=false;throw new Exception("temporary-close-failed");}}
}
public class GeoApp {
 public GeoDoc ActiveDocument;public List<GeoDoc> Created=new List<GeoDoc>();
 public GeoDoc CreateDocument(){var d=new GeoDoc();d.App=this;ActiveDocument=d;Created.Add(d);return d;}
 public double ConvertUnits(double value,int from,int to){return value;}
 public GeoRange CreateShapeRange(){var r=new GeoRange();r.App=this;return r;}
}
'@
$app=New-Object GeoApp;$source=New-Object GeoDoc;$source.App=$app;$app.ActiveDocument=$source
$shape=New-Object GeoShape;$shape.Page=$source.ActivePage;$source.ActivePage.Shapes.Items.Add($shape);$source.SelectionRange.Items.Add($shape)
$dir=Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N'));[IO.Directory]::CreateDirectory($dir)|Out-Null
$script:callback=0
$runner={param($code,$document,$readOnly)
 $script:callback++
 if(-not $readOnly -or [object]::ReferenceEquals($document,$source)){throw 'VBA must target owned temp with ReadOnly wrapper'}
 if($code -notmatch 'StartingControlPointX' -or $code -notmatch 'ConvertToObject' -or $code -notmatch 'Fill.Type = cdrNoFill'){throw 'Missing typed extraction'}
 $m=[regex]::Match($code,'Const OutputPath As String = "([^"]+)"')
 if(-not $m.Success){throw 'Missing output path'}
 [IO.File]::WriteAllText($m.Groups[1].Value,'{"objects":[{"static_id":1,"segments":[{"kind":"line","p0":{"x":0,"y":0},"p1":{"x":1,"y":1}}]}]}',(New-Object Text.UTF8Encoding($false)))
 return @{value_json='{"written":true}'}
}
try {
 $shape.Type=5
 try {Invoke-GeometrySnapshot $app $source @{} $dir $runner;throw 'Expected unsupported'}catch{if($_.Exception.Data['code'] -ne 'UNSUPPORTED_GEOMETRY'){throw}}
 if([GeoShape]::Copies -ne 0 -or $app.Created.Count -ne 0){throw 'Unsupported object copied'}
 $shape.Type=1;$shape.Mismatch=$true
 try {Invoke-GeometrySnapshot $app $source @{} $dir $runner;throw 'Expected invariant failure'}catch{if($_.Exception.Data['code'] -ne 'COPY_GEOMETRY_MISMATCH'){throw}}
 if($script:callback -ne 0 -or -not $app.Created[0].Closed){throw 'Mismatched geometry converted or temporary document leaked'}
 $shape.Mismatch=$false
 $result=Invoke-GeometrySnapshot $app $source @{} $dir $runner
 if(-not $result.original_state_preserved -or -not [IO.File]::Exists($result.snapshot_path)){throw 'Missing result'}
 if(-not $source.Dirty -or $source.Closed -or $source.SelectionRange.Count -ne 1 -or $app.ActiveDocument -ne $source){throw 'Source changed'}
 if(-not $app.Created[1].Closed){throw 'Temp document leaked'}
 $shape.Effects.Count=1
 try {Invoke-GeometrySnapshot $app $source @{} $dir $runner;throw 'Expected effect failure'}catch{if($_.Exception.Data['code'] -ne 'UNSUPPORTED_GEOMETRY'){throw}}
 $shape.Effects.Count=0;$shape.Transparency.Type=1
 try {Invoke-GeometrySnapshot $app $source @{} $dir $runner;throw 'Expected transparency failure'}catch{if($_.Exception.Data['code'] -ne 'UNSUPPORTED_GEOMETRY'){throw}}
 $shape.Transparency.Type=0
 try {Invoke-GeometrySnapshot $app $source @{static_ids=@(99)} $dir $runner;throw 'Expected missing shape'}catch{if($_.Exception.Data['code'] -ne 'SHAPE_NOT_FOUND'){throw}}
 $shape.PowerClip=New-Object object
 try {Invoke-GeometrySnapshot $app $source @{} $dir $runner;throw 'Expected PowerClip failure'}catch{if($_.Exception.Data['code'] -ne 'UNSUPPORTED_GEOMETRY'){throw}}
 $shape.PowerClip=$null
 $selectionBefore=$source.SelectionRange.Item(1).StaticID
 $exact=Invoke-GeometrySnapshot $app $source @{static_ids=@(1)} $dir $runner
 if(-not $exact.original_state_preserved -or $source.SelectionRange.Item(1).StaticID -ne $selectionBefore){throw 'Exact IDs changed selection'}
 $script:cleanups=New-Object 'System.Collections.Generic.List[string]'
 $cleanup={param($step,$error)$script:cleanups.Add($step)}
 [GeoDoc]::FailNextClose=$true
 try {Invoke-GeometrySnapshot $app $source @{} $dir $runner $cleanup;throw 'Expected cleanup failure'}catch{if($_.Exception.Data['code'] -ne 'GEOMETRY_CLEANUP_FAILED'){throw}}
 if($script:cleanups -notcontains 'geometry.temporary_document' -or $app.ActiveDocument -ne $source){throw 'Later cleanup steps skipped'}
 $primaryRunner={param($code,$document,$readOnly)throw 'PRIMARY_GEOMETRY_FAILURE'}
 [GeoDoc]::FailNextClose=$true
 try {Invoke-GeometrySnapshot $app $source @{} $dir $primaryRunner $cleanup;throw 'Expected primary failure'}catch{if($_.Exception.Message -notmatch 'PRIMARY_GEOMETRY_FAILURE' -or -not $_.Exception.Data.Contains('cleanup_errors')){throw}}
 if($app.ActiveDocument -ne $source){throw 'Primary error skipped restore'}
 $corruptRunner={param($code,$document,$readOnly)$source.Dirty=$false; & $runner $code $document $readOnly}
 try {Invoke-GeometrySnapshot $app $source @{} $dir $corruptRunner $cleanup;throw 'Expected source invariant failure'}catch{if($_.Exception.Data['code'] -ne 'GEOMETRY_CLEANUP_FAILED'){throw}}
 $source.Dirty=$true
 $shape.Visible=$false
 try {Invoke-GeometrySnapshot $app $source @{} $dir $runner;throw 'Expected hidden shape failure'}catch{if($_.Exception.Data['code'] -ne 'UNSUPPORTED_GEOMETRY'){throw}}
 $shape.Visible=$true;$shape.Layer.Visible=$false
 try {Invoke-GeometrySnapshot $app $source @{} $dir $runner;throw 'Expected hidden layer failure'}catch{if($_.Exception.Data['code'] -ne 'UNSUPPORTED_GEOMETRY'){throw}}
 $shape.Layer.Visible=$true
 $shape.Fill.Type=0;$shape.Outline.Type=0
 try {Invoke-GeometrySnapshot $app $source @{} $dir $runner;throw 'Expected invisible shape rejection'}catch{if($_.Exception.Data['code'] -ne 'UNSUPPORTED_GEOMETRY' -or $_.Exception.Message -notmatch '1'){throw}}
 $shape.Fill.Type=1
 $shape.Type=5;$second=New-Object GeoShape;$second.StaticID=2;$second.Type=5;$second.Page=$source.ActivePage;$source.ActivePage.Shapes.Items.Add($second);$source.SelectionRange.Items.Add($second)
 try {Invoke-GeometrySnapshot $app $source @{} $dir $runner;throw 'Expected unsupported list'}catch{if($_.Exception.Data['code'] -ne 'UNSUPPORTED_GEOMETRY' -or $_.Exception.Message -notmatch '1' -or $_.Exception.Message -notmatch '2'){throw}}
 $source.ActivePage.Shapes.Items.Remove($second)|Out-Null;$source.SelectionRange.Items.Remove($second)|Out-Null;$shape.Type=1
} finally {[IO.Directory]::Delete($dir,$true)}
'geometry extraction fixtures passed'
