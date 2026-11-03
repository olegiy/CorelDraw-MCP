$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../powershell/CorelAutomation.psm1') -Force
Add-Type @'
using System;
using System.Collections.Generic;
public class CleanupLog { public static List<string> Steps = new List<string>(); public static System.IO.FileStream Held; public static string Path; public static bool LockFile; public static bool Succeed; }
public class CleanupPdf {
 public int PublishRange { get { return 7; } set { CleanupLog.Steps.Add("publish"); if(value == 7) throw new Exception("restore publish failed"); } }
 public string PageRange { get { return "before"; } set { CleanupLog.Steps.Add("pages"); if(value == "before") throw new Exception("restore pages failed"); } }
}
public class CleanupPage { public int Index = 1; public void Activate() { CleanupLog.Steps.Add("page"); } }
public class CleanupDoc {
 public CleanupPdf PDFSettings = new CleanupPdf(); public CleanupPage ActivePage = new CleanupPage();
 public void Activate() { CleanupLog.Steps.Add("document"); }
 public void PublishToPDF(string path) {
  System.IO.File.WriteAllText(path,"partial export"); CleanupLog.Path=path;
  if(CleanupLog.LockFile) CleanupLog.Held = new System.IO.FileStream(path,System.IO.FileMode.Open,System.IO.FileAccess.Read,System.IO.FileShare.None);
  if(!CleanupLog.Succeed) throw new Exception("PRIMARY_EXPORT_FAILURE");
 }
}
public class CleanupCorel { public CleanupDoc ActiveDocument = new CleanupDoc(); }
'@
$module = Get-Module CorelAutomation
& $module {
    $script:Corel = New-Object CleanupCorel
    $script:Cleanup = @{ completed = $true; errors = @() }; $script:Warnings = @(); $script:Artifacts = @()
    $script:Progress = { throw 'progress failed' }
    Set-Stage 'fixture'
    if ($script:Stage -ne 'fixture') { throw 'Progress prevented stage update' }
    $directory = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    try {
        $doc = New-Object CleanupDoc
        try { Export-Document $doc @{ format='pdf'; range='page'; path=(Join-Path $directory 'out.pdf'); overwrite=$false } $directory $false; throw 'Expected primary failure' }
        catch { if ($_.Exception.ToString() -notmatch 'PRIMARY_EXPORT_FAILURE') { throw } }
        if ($script:Cleanup.completed) { throw 'Expected incomplete cleanup' }
        $steps = @($script:Cleanup.errors | ForEach-Object { $_.step })
        if ($steps -notcontains 'export.publish_range' -or $steps -notcontains 'export.page_range') { throw ('Missing structured cleanup errors: ' + ($steps -join ',')) }
        if ([CleanupLog]::Steps -notcontains 'page' -or ([CleanupLog]::Steps | Where-Object { $_ -eq 'document' }).Count -lt 2) { throw 'Later restoration steps were skipped' }
        foreach ($failure in $script:Cleanup.errors) { if (-not $failure.code -or -not $failure.message) { throw 'Incomplete cleanup error contract' } }
        $script:Cleanup=@{completed=$true;errors=@()};[CleanupLog]::LockFile=$true
        try { Export-Document $doc @{format='pdf';range='page';path=(Join-Path $directory 'locked.pdf');overwrite=$false} $directory $false; throw 'Expected primary failure' }
        catch { if ($_.Exception.ToString() -notmatch 'PRIMARY_EXPORT_FAILURE') { throw } }
        finally { if($null -ne [CleanupLog]::Held){[CleanupLog]::Held.Dispose()} }
        if(@($script:Cleanup.errors|ForEach-Object{$_.step}) -notcontains 'export.temporary_file'){throw 'Locked temporary file was not reported'}
        [IO.File]::Delete([CleanupLog]::Path)
        $script:Cleanup=@{completed=$true;errors=@()};[CleanupLog]::LockFile=$false;[CleanupLog]::Succeed=$true
        $success=Export-Document $doc @{format='pdf';range='page';path=(Join-Path $directory 'success.pdf');overwrite=$false} $directory $false
        if($success.bytes -le 0 -or $script:Cleanup.completed -or -not [IO.File]::Exists($success.path)){throw 'Successful export result must survive cleanup failure'}
    }
    finally { [IO.Directory]::Delete($directory, $true) }
}
'cleanup fixtures passed'
