#requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$Directory)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathRooted($Directory) -or [IO.Path]::GetFileName($Directory) -notmatch '^m3-http-') { throw 'Exact M3 HTTP fixture root required.' }
$proof = Get-Content -LiteralPath (Join-Path $Directory 'verification.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $proof.native_exited -or -not $proof.owners_released -or @($proof.baseline_documents).Count) { throw 'Review or restore ownership before cleanup.' }
$created = @()
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $Directory 'runs') -Filter report.json -Recurse -File) {
    $report = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($report.tool -eq 'corel_create_document' -and $report.status -eq 'succeeded' -and $proof.created_documents -contains $report.result.document_id) { $created += $report.result }
}
if ($created.Count -ne 2) { throw 'Incomplete fixture creation evidence.' }
Import-Module (Join-Path $PSScriptRoot '../powershell/CorelAutomation.psm1') -Force
$mutex = [Threading.Mutex]::new($false, 'Local\CorelDrawMcp_v27')
$owned = $false
try {
    $owned = $mutex.WaitOne(0)
    if (-not $owned) { throw 'Native executor busy; no cleanup.' }
    & (Get-Module CorelAutomation) {
        param($created)
        Connect-Corel 'CorelDRAW.Application.27'
        Assert-VbaIdle
        $targets = @()
        foreach ($item in $created) {
            $matches = @()
            for ($i=1; $i -le $script:Corel.Documents.Count; $i++) {
                $doc = $script:Corel.Documents.Item($i)
                if ($doc.Name -eq $item.name -and $doc.FullFileName -eq $item.path) { $matches += $doc }
            }
            if ($matches.Count -ne 1) { throw 'Fixture identity ambiguous; drawing retained.' }
            $doc = $matches[0]
            if ($doc.Dirty -or $doc.Pages.Count -ne 1 -or $doc.ActivePage.Shapes.Count -ne 0 -or $doc.Unit -ne $item.unit) { throw 'Fixture changed; drawing retained.' }
            $targets += $doc
        }
        foreach ($doc in $targets) { $doc.Close() }
    } $created
    $proof.created_documents_closed = $true
    $proof.cleanup_blocked = $false
    [IO.File]::WriteAllText((Join-Path $Directory 'verification.json'), ($proof | ConvertTo-Json -Depth 50), [Text.UTF8Encoding]::new($false))
    'M3_EXACT_EMPTY_FIXTURES_CLOSED'
} finally { if ($owned) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
