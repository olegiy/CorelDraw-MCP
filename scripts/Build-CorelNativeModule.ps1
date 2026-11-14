#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][switch]$DeveloperCorel,[ValidateSet('Prepare','Seal')][string]$Phase='Prepare',[string]$DotnetPath='dotnet')
$ErrorActionPreference='Stop'
if(-not $DeveloperCorel){throw 'Требуется явный DeveloperCorel на машине разработчика.'}
$repository=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
Push-Location -LiteralPath $repository
try {
 & $DotnetPath run --project 'tools/CorelMcp.NativeBuilder' -c Release -- ('--developer-'+$Phase.ToLowerInvariant()) $repository
 if($LASTEXITCODE -ne 0){throw 'Подготовка/фиксация собственного GMS не завершена.'}
 if($Phase -eq 'Prepare'){Write-Output 'Сборка разработчика: скомпилируйте и сохраните только подготовленный собственный проект через agent-desktop, затем выполните Phase=Seal. Runtime пользователя этого не требует.'}
}finally{Pop-Location}
