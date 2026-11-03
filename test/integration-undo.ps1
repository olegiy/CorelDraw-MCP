#requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$TestDocumentPath)
$ErrorActionPreference='Stop'
if(-not [IO.Path]::IsPathRooted($TestDocumentPath) -or [IO.Path]::GetFileName($TestDocumentPath) -ne 'geometry-source.cdr' -or [IO.Path]::GetDirectoryName($TestDocumentPath) -notmatch 'CorelDrawMcp-extended-'){throw 'Требуется точный путь собственного интеграционного документа.'}
Import-Module (Join-Path $PSScriptRoot '../powershell/CorelAutomation.psm1') -Force
$mutex=New-Object Threading.Mutex($false,'Local\CorelDrawMcp_v27');$owned=$false
try {
 $owned=$mutex.WaitOne(0);if(-not $owned){throw 'Исполнитель занят.'}
 & (Get-Module CorelAutomation) {
  param($path)
  Connect-Corel 'CorelDRAW.Application.27'
  $previous=$script:Corel.ActiveDocument;$target=$null
  for($i=1;$i -le $script:Corel.Documents.Count;$i++){
   $d=$script:Corel.Documents.Item($i)
   if([string]::Equals([string]$d.FullFileName,$path,[StringComparison]::OrdinalIgnoreCase)){$target=$d;break}
  }
  if($null -eq $target){throw 'Тестовый документ не найден.'}
  try{$target.Activate();$target.Undo(1)}finally{if($null -ne $previous){$previous.Activate()}}
 } $TestDocumentPath
} finally {if($owned){$mutex.ReleaseMutex()};$mutex.Dispose()}
