Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not ('CorelMcp.ApiMetadataSession' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'ApiMetadata.cs')
}
$script:ApiMetadataSession = New-Object CorelMcp.ApiMetadataSession
$ExecutionContext.SessionState.Module.OnRemove = { $script:ApiMetadataSession.Clear() }

function Get-CorelApiMetadata($Application, [hashtable] $Parameters) {
    $kind = 'types'; $name = ''; $filter = ''; $offset = 0; $limit = 100
    if ($Parameters.ContainsKey('kind')) { $kind = [string]$Parameters.kind }
    if ($Parameters.ContainsKey('name')) { $name = [string]$Parameters.name }
    if ($Parameters.ContainsKey('filter')) { $filter = [string]$Parameters.filter }
    if ($Parameters.ContainsKey('offset')) { $offset = [int]$Parameters.offset }
    if ($Parameters.ContainsKey('limit')) { $limit = [int]$Parameters.limit }
    foreach ($identifier in @($name, $filter)) {
        if ($identifier -ne '' -and $identifier -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
            $failure = New-Object InvalidOperationException('Допускаются только идентификаторы API, без выражений и путей.')
            $failure.Data['code'] = 'INVALID_NAME'; throw $failure
        }
    }
    if ($kind -notin @('types', 'members', 'enum') -or $offset -lt 0 -or $limit -lt 1 -or $limit -gt 200 -or ($kind -ne 'types' -and $name -eq '')) {
        $failure = New-Object InvalidOperationException('Некорректные параметры запроса метаданных API.')
        $failure.Data['code'] = 'INVALID_API_QUERY'; throw $failure
    }
    try { return $script:ApiMetadataSession.Describe($Application, $kind, $name, $filter, $offset, $limit) }
    catch {
        $cause = $_.Exception
        while ($null -ne $cause.InnerException) { $cause = $cause.InnerException }
        if (-not $cause.Data.Contains('code')) { $cause.Data['code'] = 'API_METADATA_UNAVAILABLE' }
        throw $cause
    }
}
Export-ModuleMember -Function Get-CorelApiMetadata
