#Requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop' -or -not [Environment]::Is64BitProcess) {
    throw 'Генерация interop требует Windows PowerShell 5.1 x64; это только сборочный инструмент.'
}
$repository = Split-Path -Parent $PSScriptRoot
$outputDirectory = Join-Path $repository 'worker\generated'
$registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey('ClassesRoot', 'Registry64')
$typeLibrary = $registry.OpenSubKey('TypeLib\{95E23C91-BC5A-49F3-8CD1-1FC515597048}')
try {
    if ($null -eq $typeLibrary) { throw 'VGCore 27 не зарегистрирован в 64-разрядном представлении реестра.' }
    $versions = @($typeLibrary.GetSubKeyNames() | Where-Object { $_ -match '^1[bB]\.[0-9a-fA-F]+$' } |
        Sort-Object { [Convert]::ToInt32(($_ -split '\.')[1], 16) } -Descending)
    if ($versions.Count -eq 0) { throw 'Не найдена регистрация type library VGCore 27.' }
    $source = $null
    foreach ($platform in @('win64', 'win32')) {
        $key = $typeLibrary.OpenSubKey($versions[0] + '\0\' + $platform)
        if ($null -ne $key) {
            try { $source = [string]$key.GetValue('') } finally { $key.Dispose() }
            if ($source) { break }
        }
    }
    if (-not $source -or -not (Test-Path -LiteralPath $source -PathType Leaf)) { throw 'Зарегистрированный файл VGCore отсутствует.' }
    Add-Type -Path (Join-Path $PSScriptRoot 'WorkerInteropGenerator.cs')
    $details = [WorkerInteropGenerator]::Generate($source, $outputDirectory)
    $registeredMinor = [Convert]::ToInt32(($versions[0] -split '\.')[1], 16)
    if ($details[2] -ne $registeredMinor) { throw 'Версия загруженной type library не совпала с регистрацией.' }
    $manifest = [ordered]@{
        typelib_guid = $details[0]; major = $details[1]; minor = $details[2]; syskind = $details[3]
        source_path = $source; source_sha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
        assembly_sha256 = (Get-FileHash -LiteralPath (Join-Path $outputDirectory 'VGCore.Interop.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
        generator = 'System.Runtime.InteropServices.TypeLibConverter (.NET Framework, build-time only)'
        warnings = @($details[4]); redistribution_verified = $false
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputDirectory 'fingerprint.json') -Encoding UTF8
    Write-Host ('Создана типизированная interop-сборка VGCore 27.' + $details[2] + ' (x64).')
} finally {
    if ($null -ne $typeLibrary) { $typeLibrary.Dispose() }
    $registry.Dispose()
}
