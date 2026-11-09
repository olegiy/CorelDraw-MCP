#requires -Version 5.1
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '../powershell/CorelAutomation.psm1') -Force
& (Get-Module CorelAutomation) {
    $script:propertyReads = 0
    $script:metadataValid = $true
    $script:handleValue = [int]42
    $script:Corel = New-Object PSObject
    $script:Corel | Add-Member ScriptProperty AppWindow { $script:propertyReads++; return @{ Handle = $script:handleValue } }
    function Get-CorelApiMetadata($Application, $Parameters) {
        if (-not $script:metadataValid) { return @{ items = @() } }
        return @{ items = @(@{ name = $Parameters.filter; kind = 'INVOKE_PROPERTYGET' }) }
    }
    if ((Get-ObserverWindow) -cne '42' -or $script:propertyReads -ne 1) { throw 'Не проверен Int32 getter.' }
    $script:metadataValid = $false
    if ((Get-ObserverWindow) -cne '' -or $script:propertyReads -ne 1) { throw 'Getter вызван без metadata.' }
    $script:metadataValid = $true; $script:handleValue = [long]42
    if ((Get-ObserverWindow) -cne '') { throw 'Неизвестный тип HWND не отклонён.' }
    $script:handleValue = [int]-2147483648
    if ((Get-ObserverWindow) -cne '-2147483648') { throw 'Потеряны знаковые биты Int32.' }
}
[Console]::WriteLine('observer metadata fixtures passed')
