param([switch]$InvalidName, [string]$Kind='types', [string]$Name='', [string]$Filter='')
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot '..\powershell\ApiMetadata.psm1') -Force
$app = New-Object -ComObject Scripting.Dictionary
try {
  if ($InvalidName) {
    try { Get-CorelApiMetadata $app @{ kind = 'types'; name = 'Foo.Bar()' } | Out-Null }
    catch { Write-Output $_.Exception.Data['code']; exit 0 }
    throw 'expected INVALID_NAME'
  }
  $r = Get-CorelApiMetadata $app @{ kind = $Kind; name = $Name; filter = $Filter; offset = 0; limit = 2 }
  $r | ConvertTo-Json -Depth 20 -Compress
}
finally {
  [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($app)
}
