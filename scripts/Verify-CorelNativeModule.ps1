#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ModulePath,[Parameter(Mandatory=$true)][string]$RunDirectory,[string]$DotnetPath='dotnet')
$ErrorActionPreference='Stop'
if(-not [IO.Path]::IsPathRooted($ModulePath) -or -not [IO.Path]::IsPathRooted($RunDirectory)){throw 'Нужны абсолютные локальные пути.'}
$repository=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
Push-Location -LiteralPath $repository
try {
 & $DotnetPath run --project 'tools/CorelMcp.NativeVerifier' -c Release -- $ModulePath $RunDirectory
 if($LASTEXITCODE -ne 0){throw 'Проверка native runtime не пройдена; диагностический JSON сохранён.'}
}finally{Pop-Location}
