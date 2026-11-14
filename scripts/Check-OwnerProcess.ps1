#Requires -Version 5.1
param([Parameter(Mandatory=$true)][ValidateRange(1,2147483647)][int] $ProcessId)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
try {
    $null=Get-Process -Id $ProcessId -ErrorAction Stop
    '{"exists":true}'
} catch {
    if ($_.FullyQualifiedErrorId -like 'NoProcessFoundForGivenId*') { '{"exists":false}' }
    else { '{"exists":null}'; exit 1 }
}
