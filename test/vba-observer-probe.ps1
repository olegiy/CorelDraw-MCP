#requires -Version 5.1
param([string] $Hwnd)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
Add-Type -Path (Join-Path $PSScriptRoot '../powershell/VbaDialogObserver.cs')
[Console]::WriteLine(([CorelMcp.Observer.VbaWindows]::Snapshot($Hwnd) | ConvertTo-Json -Depth 12 -Compress))
