#Requires -Version 5.1
[CmdletBinding()]
param([string] $DotnetPath='dotnet')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$package=Join-Path $PSScriptRoot 'worker-package.mjs'
Push-Location -LiteralPath $repository
try {
    # This gate is first: no restore, generator, deletion or publish without reviewed rights.
    & node $package --check-rights
    if($LASTEXITCODE -ne 0){throw 'INTEROP_RIGHTS_UNCONFIRMED: публикация не начата.'}
    $windowsPowerShell=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -NonInteractive -STA -File (Join-Path $PSScriptRoot 'generate-worker-interop.ps1')
    if($LASTEXITCODE -ne 0){throw 'Не подтверждена VGCore 27 type library.'}
    & node $package --check-rights
    if($LASTEXITCODE -ne 0){throw 'Сгенерированная interop не соответствует подтверждённым правам.'}
    $output=[IO.Path]::GetFullPath((Join-Path $repository 'worker\publish\win-x64'))
    $expected=[IO.Path]::GetFullPath((Join-Path $repository 'worker\publish'))
    if((Split-Path -Parent $output) -ne $expected -or (Split-Path -Leaf $output) -ne 'win-x64'){
        throw 'Каталог публикации не подтверждён; удаление запрещено.'
    }
    for($ancestor=$output; $ancestor; $ancestor=Split-Path -Parent $ancestor){
        if((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){
            throw 'Ссылка в пути каталога публикации запрещена; удаление не начато.'
        }
    }
    if(Test-Path -LiteralPath $output){
        if((Get-Item -LiteralPath $output -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Ссылка вместо каталога публикации запрещена.'}
        Remove-Item -LiteralPath $output -Recurse -Force
    }
    & $DotnetPath publish 'worker/CorelMcp.Worker/CorelMcp.Worker.csproj' -c Release -r win-x64 --self-contained true -o $output '-p:PublishTrimmed=false' '-p:PublishAot=false' '-p:PublishSingleFile=false' '-p:DebugType=None' '-p:DebugSymbols=false'
    if($LASTEXITCODE -ne 0){throw 'Self-contained публикация завершилась ошибкой; комплект не активирован.'}
    & node $package --seal
    if($LASTEXITCODE -ne 0){throw 'Manifest/лицензии комплекта не подтверждены.'}
    & node $package
    if($LASTEXITCODE -ne 0){throw 'Версия и комплектность self-contained worker не подтверждены.'}
} finally { Pop-Location }
