#requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$ReportDirectory, [switch]$CleanupOnly)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathRooted($ReportDirectory) -or -not $ReportDirectory.StartsWith($env:LOCALAPPDATA + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'M3 reports require a private LOCALAPPDATA child.' }
Import-Module (Join-Path $PSScriptRoot '../powershell/CorelAutomation.psm1') -Force
$module = Get-Module CorelAutomation
$mutex = [Threading.Mutex]::new($false, 'Local\CorelDrawMcp_v27')
$before = $null
$corelProcess = @(Get-Process -Name CorelDRW -ErrorAction SilentlyContinue)
if ($corelProcess.Count -ne 1 -or $corelProcess[0].FileVersion -notmatch '^27\.') { throw 'Exactly one already-open CorelDRAW 27 is required.' }
$corelPid = $corelProcess[0].Id
$proof = @{ schema_version=1; authority='live'; ok=$false; native_checks_passed=$false; reports=$ReportDirectory; suites=@(); preservation=$false; cleanup_completed=$false }
function Write-Proof {
    [IO.File]::WriteAllText((Join-Path $ReportDirectory 'verification.json'), ($proof | ConvertTo-Json -Depth 50), [Text.UTF8Encoding]::new($false))
}
function Native-Snapshot {
    $owned = $false
    try {
        $owned = $mutex.WaitOne(0)
        if (-not $owned) { throw 'Native executor busy; no further COM call allowed.' }
        return & $module {
            Connect-Corel 'CorelDRAW.Application.27'
            Sync-Documents
            $status = Get-CorelStatus
            Assert-VbaIdle
            $documents = @{}
            foreach ($entry in $script:Documents.GetEnumerator()) {
                $doc = $entry.Value.document
                $pages = @()
                for ($i=1; $i -le $doc.Pages.Count; $i++) {
                    $item = Inspect-Document $doc @{ page_index=$i; limit=5000; recursive=$true; max_depth=32; max_visited=50000; include_text=$true; text_limit=10000 }
                    if ($item.shapes_truncated -or $item.depth_truncated -or $item.visited_truncated -or $item.selection_truncated -or $item.pages_truncated -or $item.layers_truncated) { throw 'Incomplete preservation baseline.' }
                    $pages += $item
                }
                $hash = $null
                if ($doc.FullFileName -and (Test-Path -LiteralPath $doc.FullFileName)) { $hash = (Get-FileHash -LiteralPath $doc.FullFileName -Algorithm SHA256).Hash }
                $documents[$entry.Key] = @{ pages=$pages; file_sha256=$hash }
            }
            $projects = @()
            for ($i=1; $i -le $script:Corel.GMSManager.Projects.Count; $i++) { $projects += [string]$script:Corel.GMSManager.Projects.Item($i).Name }
            return @{status=$status; documents=$documents; projects=@($projects | Sort-Object)}
        }
    } finally { if ($owned) { $mutex.ReleaseMutex() } }
}
try {
    if ($CleanupOnly) {
        $saved = Get-Content -LiteralPath (Join-Path $ReportDirectory 'verification.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not $saved.preservation) { throw 'Review preservation failure before cleanup.' }
        $baseline = Get-Content -LiteralPath (Join-Path $ReportDirectory 'before.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $names = @()
        foreach ($file in Get-ChildItem -LiteralPath $ReportDirectory -Filter report.json -Recurse -File) {
            $report = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($report.cleanup.project_retained -and $report.cleanup.project_name) {
                $name = [string]$report.cleanup.project_name
                if ($name -notmatch '^CorelMcp_' -or $baseline.projects -contains $name) { throw 'Unowned GMS cleanup candidate.' }
                $names += $name
            }
        }
        $owned = $mutex.WaitOne(0)
        if (-not $owned) { throw 'Native executor busy; cleanup forbidden.' }
        try {
            & $module {
                param($names)
                Connect-Corel 'CorelDRAW.Application.27'
                Assert-VbaIdle
                foreach ($name in @($names | Sort-Object -Unique)) {
                    for ($i=$script:Corel.GMSManager.Projects.Count; $i -ge 1; $i--) {
                        $project = $script:Corel.GMSManager.Projects.Item($i)
                        if ($project.Name -eq $name) { $project.Unload() }
                    }
                }
            } $names
        } finally { $mutex.ReleaseMutex() }
        $after = Native-Snapshot
        $actual = @($after.projects | Where-Object { $baseline.projects -notcontains $_ })
        if ($actual.Count -or @($baseline.projects | Where-Object { $after.projects -notcontains $_ }).Count) { throw 'GMS baseline not restored.' }
        $saved.cleanup_completed = $true
        $saved.ok = $saved.native_checks_passed -and $saved.suites.Count -eq 3
        [IO.File]::WriteAllText((Join-Path $ReportDirectory 'verification.json'), ($saved | ConvertTo-Json -Depth 50), [Text.UTF8Encoding]::new($false))
        'M3_GMS_CLEANUP_PASS'
        return
    }
    [IO.Directory]::CreateDirectory($ReportDirectory) > $null
    $before = Native-Snapshot
    if (@(Get-Process -Name CorelDRW).Count -ne 1 -or (Get-Process -Name CorelDRW).Id -ne $corelPid) { throw 'COM attached to an unexpected CorelDRAW process.' }
    $proof.corel_pid = $corelPid
    [IO.File]::WriteAllText((Join-Path $ReportDirectory 'before.json'), ($before | ConvertTo-Json -Depth 50), [Text.UTF8Encoding]::new($false))
    $previous = & $module { if ($script:Corel.Documents.Count) { return ,$script:Corel.ActiveDocument } }
    $env:CORELDRAW_MCP_INTEGRATION_PARENT = $ReportDirectory
    foreach ($suite in @('integration.mjs', 'integration-recipes.mjs', 'integration-extended.mjs')) {
        & node (Join-Path $PSScriptRoot $suite)
        if ($LASTEXITCODE -ne 0) { throw "Live suite failed: $suite. No automatic mutation replay." }
        $proof.suites += $suite
    }
} finally {
    if ($before) {
        try {
            $owned = $mutex.WaitOne(0)
            if (-not $owned) { throw 'Native job remains busy; preservation check blocked.' }
            try { if ($null -ne $previous) { $previous.Activate() } }
            finally { $mutex.ReleaseMutex() }
            $after = Native-Snapshot
            [IO.File]::WriteAllText((Join-Path $ReportDirectory 'after.json'), ($after | ConvertTo-Json -Depth 50), [Text.UTF8Encoding]::new($false))
            Write-Proof
            & node (Join-Path $PSScriptRoot 'm3-compare.mjs') $ReportDirectory
            if ($LASTEXITCODE -ne 0) { throw 'M3 preservation comparison failed.' }
            $reviewed = Get-Content -LiteralPath (Join-Path $ReportDirectory 'verification.json') -Raw -Encoding UTF8 | ConvertFrom-Json
            $proof.preservation = $reviewed.preservation
            $proof.native_checks_passed = $reviewed.native_checks_passed
            if (-not $proof.preservation) { Write-Error 'M3 preservation mismatch; reports retained.' -ErrorAction Continue }
        } catch { $proof.failure = 'PRESERVATION_CHECK_FAILED' }
        Write-Proof
        "M3_NATIVE_REPORT=$ReportDirectory"
    }
    $mutex.Dispose()
}
if (-not $proof.native_checks_passed) { exit 1 }
