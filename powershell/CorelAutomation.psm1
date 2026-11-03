Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:Corel = $null
$script:Documents = @{}
$script:SessionId = [guid]::NewGuid().ToString('N')
$script:Stage = 'starting'
$script:FailureStage = $null
$script:Progress = $null
$script:ValidateJson = $null
$script:Diagnostics = $null
$script:Warnings = @()
$script:Artifacts = @()
$script:Cleanup = @{}

function Set-Stage([string] $Name) {
    $script:Stage = $Name
    if ($null -ne $script:Progress) { & $script:Progress $Name }
}
function Throw-CorelError([string] $Code, [string] $Message) {
    $failure = New-Object InvalidOperationException($Message)
    $failure.Data['code'] = $Code
    throw $failure
}
function Get-ComIdentity($Value) {
    $pointer = [Runtime.InteropServices.Marshal]::GetIUnknownForObject($Value)
    try { return $pointer.ToInt64().ToString() }
    finally { [Runtime.InteropServices.Marshal]::Release($pointer) | Out-Null }
}
function Connect-Corel([string] $ProgId) {
    Set-Stage 'connecting'
    if ($null -ne $script:Corel) {
        try { $null = $script:Corel.Version; return }
        catch { $script:Corel = $null; $script:Documents = @{}; $script:SessionId = [guid]::NewGuid().ToString('N') }
    }
    $script:Corel = New-Object -ComObject $ProgId
    $version = [string]$script:Corel.Version
    if ($version -notmatch '\b27\.') { $script:Corel = $null; Throw-CorelError 'UNSUPPORTED_VERSION' ('Expected CorelDRAW 27, got ' + $version) }
}
function Sync-Documents {
    $current = @{}
    for ($i = 1; $i -le $script:Corel.Documents.Count; $i++) {
        $drawing = $script:Corel.Documents.Item($i)
        $identity = Get-ComIdentity $drawing
        $existingId = $null
        foreach ($id in @($script:Documents.Keys)) {
            if ($script:Documents[$id].identity -eq $identity) { $existingId = $id; break }
        }
        if ($null -eq $existingId) { $existingId = $script:SessionId + ':' + [guid]::NewGuid().ToString('N') }
        $current[$existingId] = @{ identity = $identity; document = $drawing }
    }
    $script:Documents = $current
}
function Get-Document([string] $Id) {
    Sync-Documents
    if (-not $script:Documents.ContainsKey($Id)) { Throw-CorelError 'STALE_DOCUMENT' 'The document was closed or belongs to another executor session. Call corel_status to obtain current IDs.' }
    return ,$script:Documents[$Id].document
}
function Get-DocumentInfo($Document) {
    $identity = Get-ComIdentity $Document
    $id = $null
    foreach ($key in $script:Documents.Keys) { if ($script:Documents[$key].identity -eq $identity) { $id = $key; break } }
    return [ordered]@{
        document_id = $id; name = [string]$Document.Name; path = [string]$Document.FullFileName
        dirty = [bool]$Document.Dirty; pages = [int]$Document.Pages.Count
        active_page = [int]$Document.ActivePage.Index; unit = [int]$Document.Unit; preserve_selection = [bool]$Document.PreserveSelection
    }
}
function Assert-VbaIdle {
    $editor = $script:Corel.VBE
    if ($null -eq $editor) { Throw-CorelError 'VBA_UNAVAILABLE' 'Install VBA and enable access to its object model in CorelDRAW.' }
    for ($i = 1; $i -le $editor.VBProjects.Count; $i++) {
        if ($editor.VBProjects.Item($i).Mode -ne 2) { Throw-CorelError 'VBA_BUSY' 'VBA is executing or paused in the debugger. Finish or reset it in CorelDRAW before submitting another operation.' }
    }
}
function Get-Page($Document, [hashtable] $Parameters) {
    if ($Parameters.ContainsKey('page_index')) {
        $index = [int]$Parameters.page_index
        if ($index -lt 1 -or $index -gt $Document.Pages.Count) { Throw-CorelError 'PAGE_NOT_FOUND' ('Invalid page index: ' + $index) }
        return ,$Document.Pages.Item($index)
    }
    return ,$Document.ActivePage
}
function Get-ShapeInfo($Shape, $Selected) {
    $staticId = [int]$Shape.StaticID
    return [ordered]@{
        static_id = $staticId; name = [string]$Shape.Name; type = [int]$Shape.Type
        layer = [string]$Shape.Layer.Name; selected = $Selected.ContainsKey([string]$staticId)
        bounds = @{ left = [double]$Shape.LeftX; bottom = [double]$Shape.BottomY; width = [double]$Shape.SizeWidth; height = [double]$Shape.SizeHeight }
    }
}
function Get-CorelStatus {
    Sync-Documents
    $drawings = @()
    foreach ($entry in $script:Documents.Values) { $drawings += Get-DocumentInfo $entry.document }
    $vba = @{ available = $false; mode = $null; compile_available = $false }
    try {
        $editor = $script:Corel.VBE
        $vba.available = $null -ne $editor
        if ($null -ne $editor.ActiveVBProject) { $vba.mode = [int]$editor.ActiveVBProject.Mode }
        $vba.compile_available = $null -ne $editor.CommandBars.FindControl(1, 578)
    }
    catch { $script:Warnings += 'The VBA editor could not be accessed: ' + $_.Exception.Message }
    $activeId = $null
    if ($script:Corel.Documents.Count -gt 0) { $activeId = (Get-DocumentInfo $script:Corel.ActiveDocument).document_id }
    return @{
        version = [string]$script:Corel.Version; session_id = $script:SessionId; active_document_id = $activeId; documents = $drawings; vba = $vba
        application_state = @{ optimization = [bool]$script:Corel.Optimization; events_enabled = [bool]$script:Corel.EventsEnabled }
        executor = @{ state = 'ready'; pid = $PID; apartment = [Threading.Thread]::CurrentThread.ApartmentState.ToString(); powershell_version = $PSVersionTable.PSVersion.ToString() }
    }
}
function Inspect-Document($Document, [hashtable] $Parameters) {
    Set-Stage 'inspecting'
    $page = Get-Page $Document $Parameters
    $limit = [int]$Parameters.limit
    $selected = @{}
    $selectionIds = @()
    $selection = $Document.SelectionRange
    for ($i = 1; $i -le $selection.Count; $i++) {
        $shapeId = [int]$selection.Item($i).StaticID; $selected[[string]$shapeId] = $true
        if ($i -le $limit) { $selectionIds += $shapeId }
    }
    $pages = @()
    for ($i = 1; $i -le [Math]::Min($Document.Pages.Count, $limit); $i++) { $p = $Document.Pages.Item($i); $pages += @{ index = [int]$p.Index; name = [string]$p.Name; width = [double]$p.SizeWidth; height = [double]$p.SizeHeight } }
    $layers = @()
    for ($i = 1; $i -le [Math]::Min($page.Layers.Count, $limit); $i++) {
        $layer = $page.Layers.Item($i)
        $layers += @{ name = [string]$layer.Name; visible = [bool]$layer.Visible; editable = [bool]$layer.Editable; printable = [bool]$layer.Printable; shapes = [int]$layer.Shapes.Count }
    }
    $shapes = @()
    $offset = [int]$Parameters.offset
    $end = [Math]::Min($page.Shapes.Count, $offset + [int]$Parameters.limit)
    for ($i = $offset + 1; $i -le $end; $i++) { $shapes += Get-ShapeInfo $page.Shapes.Item($i) $selected }
    return @{
        document = (Get-DocumentInfo $Document); pages = $pages; page_index = [int]$page.Index; layers = $layers; selection_ids = $selectionIds; shapes = $shapes
        total_shapes = [int]$page.Shapes.Count; next_offset = $(if ($end -lt $page.Shapes.Count) { $end } else { $null })
        total_pages = [int]$Document.Pages.Count; total_layers = [int]$page.Layers.Count; total_selected = [int]$selection.Count
        pages_truncated = $Document.Pages.Count -gt $limit; layers_truncated = $page.Layers.Count -gt $limit; selection_truncated = $selection.Count -gt $limit
    }
}
function Get-OutputPath([string] $Path, [string] $Extension) {
    $destination = [IO.Path]::GetFullPath($Path)
    if (-not [string]::Equals([IO.Path]::GetExtension($destination), '.' + $Extension, [StringComparison]::OrdinalIgnoreCase)) { Throw-CorelError 'INVALID_EXTENSION' ('Expected .' + $Extension + ' file.') }
    if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($destination))) { Throw-CorelError 'DIRECTORY_NOT_FOUND' 'The destination parent directory must exist.' }
    if ([IO.Directory]::Exists($destination)) { Throw-CorelError 'PATH_IS_DIRECTORY' 'The destination is a directory.' }
    return $destination
}
function Save-Document($Document, [hashtable] $Parameters) {
    Set-Stage 'saving'
    $current = [string]$Document.FullFileName
    $destination = $current
    if ($Parameters.ContainsKey('path')) { $destination = [string]$Parameters.path }
    if ([string]::IsNullOrWhiteSpace($destination) -or -not [IO.Path]::IsPathRooted($destination)) { Throw-CorelError 'SAVE_PATH_REQUIRED' 'An unsaved document requires an absolute .cdr path.' }
    $destination = Get-OutputPath $destination 'cdr'
    $samePath = [string]::Equals($current, $destination, [StringComparison]::OrdinalIgnoreCase)
    if ([IO.File]::Exists($destination) -and -not $samePath -and -not $Parameters.overwrite) { Throw-CorelError 'FILE_EXISTS' 'The destination exists. Set overwrite=true to replace it.' }
    Sync-Documents
    foreach ($entry in $script:Documents.Values) {
        if ((Get-ComIdentity $entry.document) -ne (Get-ComIdentity $Document) -and [string]::Equals([string]$entry.document.FullFileName, $destination, [StringComparison]::OrdinalIgnoreCase)) { Throw-CorelError 'DOCUMENT_ALREADY_OPEN' 'The destination is open in another document.' }
    }
    if ($samePath) { $Document.Save() }
    else {
        $options = $script:Corel.CreateStructSaveAsOptions()
        $options.Overwrite = [bool]$Parameters.overwrite
        $options.Filter = 1795 # cdrCDR, API 27
        $Document.SaveAs($destination, $options)
    }
    if (-not [IO.File]::Exists($destination) -or (Get-Item -LiteralPath $destination).Length -eq 0) { Throw-CorelError 'SAVE_NOT_CREATED' 'CorelDRAW returned without creating a nonempty CDR file.' }
    $script:Artifacts += $destination
    return @{ document = (Get-DocumentInfo $Document); path = $destination; bytes = (Get-Item -LiteralPath $destination).Length }
}
function Export-Document($Document, [hashtable] $Parameters, [string] $RunDirectory, [bool] $Preview) {
    Set-Stage 'exporting'
    $format = $(if ($Preview) { 'png' } else { [string]$Parameters.format })
    $range = $(if ($Preview) { 'page' } else { [string]$Parameters.range })
    if (($format -eq 'pdf' -and $range -eq 'selection') -or ($format -ne 'pdf' -and $range -eq 'document')) { Throw-CorelError 'UNSUPPORTED_EXPORT_RANGE' 'PNG/SVG support page/selection; PDF supports page/document.' }
    $destination = $(if ($Preview) { Join-Path $RunDirectory 'preview.png' } else { Get-OutputPath $Parameters.path $format })
    $overwrite = -not $Preview -and [bool]$Parameters.overwrite
    if ([IO.File]::Exists($destination) -and -not $overwrite) { Throw-CorelError 'FILE_EXISTS' 'The export file exists. Set overwrite=true to replace it.' }
    $temporary = Join-Path ([IO.Path]::GetDirectoryName($destination)) ('.corelmcp-' + [guid]::NewGuid().ToString('N') + '.' + $format)
    $previousDocument = $script:Corel.ActiveDocument
    $previousPage = $Document.ActivePage
    $pdfRange = $null
    $pdfPages = $null
    try {
        $Document.Activate()
        $page = Get-Page $Document $Parameters
        if ($range -eq 'selection' -and $page.Index -ne $Document.ActivePage.Index) { Throw-CorelError 'SELECTION_PAGE_MISMATCH' 'Selection export requires the document current page.' }
        $page.Activate()
        $exportRange = 1 # cdrCurrentPage
        if ($range -eq 'selection') {
            if ($Document.SelectionRange.Count -eq 0) { Throw-CorelError 'EMPTY_SELECTION' 'No objects are selected in the target document.' }
            $exportRange = 2 # cdrSelection
        }
        if ($format -eq 'pdf') {
            $pdfRange = $Document.PDFSettings.PublishRange
            $pdfPages = $Document.PDFSettings.PageRange
            if ($range -eq 'document') { $Document.PDFSettings.PublishRange = 0 }
            else { $Document.PDFSettings.PublishRange = 3; $Document.PDFSettings.PageRange = [string]$page.Index }
            $Document.PublishToPDF($temporary)
        }
        elseif ($format -eq 'svg') {
            # Corel's ExportEx/ExportBitmap optional interface parameters do not
            # marshal reliably through Windows PowerShell's late-bound COM adapter.
            # Compile a typed native VBA call using the same isolated GMS engine.
            $literalPath = $temporary.Replace('"', '""')
            $source = "Option Explicit`nPublic Function Main() As String`nDim f As ExportFilter`nSet f = ActiveDocument.ExportEx(""$literalPath"", cdrSVG, $exportRange)`nf.Finish`nMain = ""{}""`nEnd Function"
            Invoke-Vba @{ code = $source } $RunDirectory $true $Document | Out-Null
        }
        else {
            $width = [double]$page.SizeWidth; $height = [double]$page.SizeHeight
            if ($range -eq 'selection') { $width = [double]$Document.SelectionRange.SizeWidth; $height = [double]$Document.SelectionRange.SizeHeight }
            if ($width -le 0 -or $height -le 0) { Throw-CorelError 'EMPTY_EXPORT_AREA' 'Export width and height must be positive.' }
            $dpi = $(if ($Preview) { 96 } else { [int]$Parameters.dpi })
            if ($Preview) {
                $scale = [double]$Parameters.max_size / [Math]::Max($width, $height)
                $pixelWidth = [int][Math]::Max(1, [Math]::Round($width * $scale))
                $pixelHeight = [int][Math]::Max(1, [Math]::Round($height * $scale))
            }
            else {
                $pixelWidth = [Math]::Max(1, [Math]::Ceiling($script:Corel.ConvertUnits($width, $Document.Unit, 1) * $dpi))
                $pixelHeight = [Math]::Max(1, [Math]::Ceiling($script:Corel.ConvertUnits($height, $Document.Unit, 1) * $dpi))
                if ($pixelWidth * $pixelHeight -gt 40000000) { Throw-CorelError 'EXPORT_TOO_LARGE' 'PNG exceeds 40 megapixels; reduce dpi or export a smaller area.' }
            }
            $literalPath = $temporary.Replace('"', '""')
            # cdrCurrentPage chooses objects, but bitmap export otherwise crops
            # to their bounds. An explicit area preserves the actual page frame.
            $exportArea = $(if ($range -eq 'page') { ', ExportArea:=ActivePage.BoundingBox' } else { '' })
            $source = "Option Explicit`nPublic Function Main() As String`nDim f As ExportFilter`nSet f = ActiveDocument.ExportBitmap(""$literalPath"", cdrPNG, $exportRange, cdrRGBColorImage, $pixelWidth, $pixelHeight, $dpi, $dpi, cdrNormalAntiAliasing, False, False$exportArea)`nf.Finish`nMain = ""{}""`nEnd Function"
            Invoke-Vba @{ code = $source } $RunDirectory $true $Document | Out-Null
        }
        if (-not [IO.File]::Exists($temporary) -or (Get-Item -LiteralPath $temporary).Length -eq 0) { Throw-CorelError 'EXPORT_NOT_CREATED' 'CorelDRAW returned without creating a nonempty export.' }
        Set-Stage 'committing_export'
        if ([IO.File]::Exists($destination)) {
            if (-not $overwrite) { Throw-CorelError 'FILE_EXISTS' 'The destination was created while exporting; no file was replaced.' }
            [IO.File]::Replace($temporary, $destination, [System.Management.Automation.Language.NullString]::Value)
        }
        else { [IO.File]::Move($temporary, $destination) }
        $script:Artifacts += $destination
        return @{ path = $destination; bytes = (Get-Item -LiteralPath $destination).Length; format = $format; range = $range }
    }
    finally {
        try {
            if ($null -ne $pdfRange) { $Document.PDFSettings.PublishRange = $pdfRange; $Document.PDFSettings.PageRange = $pdfPages }
            $previousPage.Activate()
            if ($null -ne $previousDocument) { $previousDocument.Activate() }
        }
        catch { $script:Cleanup.completed = $false; $script:Warnings += 'Could not restore export context: ' + $_.Exception.Message }
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}
function Get-VbaProject($Editor, [string] $Name) {
    for ($i = 1; $i -le $Editor.VBProjects.Count; $i++) {
        $candidate = $Editor.VBProjects.Item($i)
        if ([string]::Equals([string]$candidate.Name, $Name, [StringComparison]::Ordinal)) { return ,$candidate }
    }
    Throw-CorelError 'VBA_PROJECT_NOT_FOUND' ('The generated project was not found: ' + $Name)
}
function Add-VbaModule($Project, [string] $Name, [string] $Code) {
    $component = $Project.VBComponents.Add(1)
    $component.Name = $Name
    if ($component.CodeModule.CountOfLines -gt 0) { $component.CodeModule.DeleteLines(1, $component.CodeModule.CountOfLines) }
    $component.CodeModule.AddFromString($Code)
}
function Get-VbaSnapshot([hashtable] $Parameters, [string] $RunDirectory, [bool] $Execute) {
    Set-Stage 'snapshotting_sources'
    $snapshots = @()
    $utf8 = New-Object Text.UTF8Encoding($false, $true)
    if ($Execute) {
        if ($utf8.GetByteCount([string]$Parameters.code) -gt 262144) { Throw-CorelError 'SOURCE_TOO_LARGE' 'The UTF-8 scenario exceeds 256 KiB.' }
        $snapshots += @{ name = 'M_UserCode'; code = [string]$Parameters.code }
    }
    else {
        $root = (Resolve-Path -LiteralPath $Parameters.source_root).ProviderPath
        if (-not [IO.Directory]::Exists($root)) { Throw-CorelError 'SOURCE_DIRECTORY_REQUIRED' 'source_root must be a directory.' }
        $rootPrefix = $root.TrimEnd('\') + '\'
        $files = @()
        if ($Parameters.ContainsKey('module_paths')) {
            foreach ($relativePath in $Parameters.module_paths) {
                if ([IO.Path]::IsPathRooted($relativePath)) { Throw-CorelError 'INVALID_MODULE_PATH' 'Module paths must be relative to source_root.' }
                $file = Get-Item -LiteralPath (Join-Path $root $relativePath)
                if (-not $file.FullName.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) { Throw-CorelError 'INVALID_MODULE_PATH' 'Module paths must remain within source_root.' }
                $files += $file
            }
        }
        else { $files = @(Get-ChildItem -LiteralPath $root -Filter '*.bas' -File | Sort-Object Name) }
        if ($files.Count -eq 0 -or $files.Count -gt 100) { Throw-CorelError 'INVALID_MODULE_COUNT' 'Supply between 1 and 100 standard .bas modules.' }
        $names = @{}
        $totalBytes = 0
        foreach ($file in $files) {
            if ($file.PSIsContainer -or $file.Extension -ine '.bas') { Throw-CorelError 'INVALID_MODULE_TYPE' 'Only standard .bas files are supported.' }
            $ancestor = $file
            while ($null -ne $ancestor -and $ancestor.FullName.Length -gt $root.Length) {
                if (($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Throw-CorelError 'MODULE_REPARSE_POINT' 'Module paths may not traverse symlinks or junctions below source_root.' }
                $ancestor = $(if ($ancestor -is [IO.FileInfo]) { $ancestor.Directory } else { $ancestor.Parent })
            }
            $name = $file.BaseName
            if ($name -notmatch '^[A-Za-z][A-Za-z0-9_]{0,30}$' -or $names.ContainsKey($name)) { Throw-CorelError 'INVALID_MODULE_NAME' ('Invalid or duplicate VBA module name: ' + $name) }
            $names[$name] = $true
            $totalBytes += $file.Length
            if ($totalBytes -gt 2097152) { Throw-CorelError 'SOURCES_TOO_LARGE' 'The total source size exceeds 2 MiB.' }
            $snapshots += @{ name = $name; code = [IO.File]::ReadAllText($file.FullName, $utf8) }
        }
    }
    foreach ($snapshot in $snapshots) {
        $snapshot.code = [regex]::Replace($snapshot.code, '(?m)^Attribute VB_.*\r?\n?', '')
        $snapshot.path = Join-Path $RunDirectory ($snapshot.name + '.bas')
        [IO.File]::WriteAllText($snapshot.path, $snapshot.code, $utf8)
        $script:Artifacts += $snapshot.path
    }
    return ,$snapshots
}
function Invoke-Vba([hashtable] $Parameters, [string] $RunDirectory, [bool] $Execute, $Document) {
    $snapshots = Get-VbaSnapshot $Parameters $RunDirectory $Execute
    $editor = $script:Corel.VBE
    $previousProject = $editor.ActiveVBProject
    $previousDocument = $null
    if ($script:Corel.Documents.Count -gt 0) { $previousDocument = $script:Corel.ActiveDocument }
    $projectName = 'CorelMcp_' + [guid]::NewGuid().ToString('N').Substring(0, 12)
    $gmsPath = Join-Path $RunDirectory ($projectName + '.gms')
    $gms = $null
    $succeeded = $false
    $groupStarted = $false
    $settingsSaved = $false
    $previousOptimization = $null
    $previousEvents = $null
    $previousSelectionMode = $null
    try {
        Set-Stage 'creating_gms'
        $gms = $script:Corel.GMSManager.Projects.Create($projectName, $false, $gmsPath)
        $script:Artifacts += $gmsPath
        $script:Cleanup.project_retained = $true
        $script:Cleanup.project_name = $projectName
        $project = Get-VbaProject $editor $projectName
        foreach ($snapshot in $snapshots) { Add-VbaModule $project $snapshot.name $snapshot.code }
        if ($Execute) {
            $harness = @'
Option Explicit
Public Function RunMain() As String
    On Error GoTo Failed
    RunMain = "OK" & vbLf & M_UserCode.Main()
    Exit Function
Failed:
    RunMain = "ERROR" & vbLf & CStr(Err.Number) & vbLf & Err.Source & vbLf & Err.Description
End Function
'@
            Add-VbaModule $project 'M_CorelMcpHarness' $harness
            $harnessPath = Join-Path $RunDirectory 'M_CorelMcpHarness.bas'
            [IO.File]::WriteAllText($harnessPath, $harness, (New-Object Text.UTF8Encoding($false)))
            $script:Artifacts += $harnessPath
        }
        # Corel's VBE can keep ActiveCodePane pointing at an older failed GMS,
        # even while the current project's own pane receives the compiler span.
        # Reset only our new panes, then inspect only those panes on failure.
        for ($i = 1; $i -le $project.VBComponents.Count; $i++) {
            $module = $project.VBComponents.Item($i).CodeModule
            if ($module.CountOfLines -gt 0) { $module.CodePane.SetSelection(1, 1, 1, 1) }
        }
        $editor.ActiveVBProject = $project
        $project.VBComponents.Item($snapshots[0].name).Activate()
        if ($editor.ActiveVBProject.Name -ne $projectName) { Throw-CorelError 'COMPILER_PROJECT_MISMATCH' 'VBE did not activate the generated VBA project.' }
        Set-Stage 'compiling'
        $compile = $editor.CommandBars.FindControl(1, 578)
        if ($null -eq $compile) { Throw-CorelError 'COMPILER_UNAVAILABLE' 'VBE Compile command 578 is unavailable.' }
        if ($compile.Enabled) { $compile.Execute() }
        if ($compile.Enabled) {
            for ($i = 1; $i -le $project.VBComponents.Count; $i++) {
                $module = $project.VBComponents.Item($i).CodeModule
                if ($module.CountOfLines -eq 0) { continue }
                $pane = $module.CodePane
                $line1 = 0; $column1 = 0; $line2 = 0; $column2 = 0
                $pane.GetSelection([ref]$line1, [ref]$column1, [ref]$line2, [ref]$column2)
                if ($line1 -gt 0 -and ($line2 -gt $line1 -or $column2 -gt $column1) -and $pane.CodeModule.Parent.Collection.Parent.Name -eq $projectName) {
                    $script:Diagnostics = @{ module = [string]$pane.CodeModule.Name; line = $line1; column = $column1; text = [string]$pane.CodeModule.Lines($line1, 1) }
                    break
                }
            }
            Throw-CorelError 'COMPILATION_FAILED' 'Compilation did not finish. Inspect compiler_location and the retained GMS in the VBA editor; a modal compiler dialog may need dismissal.'
        }
        if (-not $Execute) {
            $succeeded = $true
            return @{ compiled = $true; project = $projectName; modules = @($snapshots | ForEach-Object { $_.name }); gms_path = $gmsPath }
        }
        Set-Stage 'executing_vba'
        $Document.Activate()
        $previousOptimization = [bool]$script:Corel.Optimization
        $previousEvents = [bool]$script:Corel.EventsEnabled
        $previousSelectionMode = [bool]$Document.PreserveSelection
        $Document.SaveSettings(); $settingsSaved = $true
        $Document.BeginCommandGroup('Corel MCP VBA'); $groupStarted = $true
        $script:Corel.Optimization = $true
        $script:Corel.EventsEnabled = $false
        $Document.PreserveSelection = $false
        $raw = [string]$script:Corel.GMSManager.RunMacro($projectName, 'M_CorelMcpHarness.RunMain')
        if ($raw.StartsWith("ERROR`n")) {
            $parts = $raw -split "`n", 4
            $failure = New-Object InvalidOperationException($parts[3])
            $failure.Data['code'] = 'VBA_RUNTIME_ERROR'; $failure.Data['number'] = [int]$parts[1]; $failure.Data['source'] = $parts[2]
            throw $failure
        }
        if (-not $raw.StartsWith("OK`n")) { Throw-CorelError 'INVALID_VBA_RESULT' 'The VBA harness returned an unexpected result.' }
        $json = $raw.Substring(3)
        if ([Text.Encoding]::UTF8.GetByteCount($json) -gt 1048576) { Throw-CorelError 'VBA_RESULT_TOO_LARGE' 'The JSON result exceeds 1 MiB.' }
        # Validate with the host's strict JSON parser before unloading the GMS.
        # Keep the raw JSON in IPC: PowerShell 5.1 otherwise unwraps singleton
        # arrays and loses the distinction between [] and null.
        Set-Stage 'validating_result'
        if ($null -eq $script:ValidateJson) { Throw-CorelError 'VALIDATION_UNAVAILABLE' 'The executor JSON validation channel is unavailable.' }
        & $script:ValidateJson $json | Out-Null
        $succeeded = $true
        return @{ compiled = $true; project = $projectName; value_json = $json; gms_path = $gmsPath }
    }
    catch { $script:FailureStage = $script:Stage; throw }
    finally {
        Set-Stage 'cleanup'
        if ($Execute -and $null -ne $Document) {
            # Attempt each restoration independently, including EndCommandGroup,
            # even if an earlier step fails (for example, the script closed doc).
            $cleanupSteps = @(
                @{ name = 'document settings'; action = { if ($settingsSaved) { $Document.RestoreSettings() } } }
                @{ name = 'selection mode'; action = { if ($null -ne $previousSelectionMode) { $Document.PreserveSelection = $previousSelectionMode } } }
                @{ name = 'Undo group'; action = { if ($groupStarted) { $Document.EndCommandGroup() } } }
                @{ name = 'optimization'; action = { if ($null -ne $previousOptimization) { $script:Corel.Optimization = $previousOptimization } } }
                @{ name = 'events'; action = { if ($null -ne $previousEvents) { $script:Corel.EventsEnabled = $previousEvents } } }
                @{ name = 'refresh'; action = { $script:Corel.Refresh() } }
            )
            foreach ($step in $cleanupSteps) {
                try { & $step.action | Out-Null }
                catch { $script:Cleanup.completed = $false; $script:Warnings += ('Could not restore ' + $step.name + ': ' + $_.Exception.Message) }
            }
        }
        if ($null -ne $previousDocument) { try { $previousDocument.Activate() } catch { $script:Cleanup.completed = $false; $script:Warnings += 'Previous document is no longer available.' } }
        if ($succeeded -and $null -ne $gms) {
            try {
                if ($null -ne $previousProject) { $editor.ActiveVBProject = $previousProject }
                $gms.Unload(); $script:Cleanup.project_retained = $false
            }
            catch { $script:Cleanup.completed = $false; $script:Warnings += 'Could not unload GMS: ' + $_.Exception.Message }
        }
    }
}
function Invoke-CorelOperation {
    [CmdletBinding()]
    param([string] $Tool, [hashtable] $Parameters, [string] $RunDirectory, [string] $ProgId, [scriptblock] $Progress, [scriptblock] $ValidateJson)
    $script:Progress = $Progress
    $script:ValidateJson = $ValidateJson
    $script:Stage = 'waiting_for_mutex'
    $script:FailureStage = $null
    $script:Diagnostics = $null
    $script:Warnings = @()
    $script:Artifacts = @()
    $script:Cleanup = @{ completed = $true }
    $mutex = New-Object Threading.Mutex($false, 'Local\CorelDrawMcp_v27')
    $owned = $false
    $outcome = @{ ok = $false; result = $null; error = $null }
    try {
        try { $owned = $mutex.WaitOne(0) }
        catch [Threading.AbandonedMutexException] { $owned = $true; $script:Warnings += 'A previous executor abandoned the mutex; inspect earlier unknown jobs before repeating them.' }
        if (-not $owned) { Throw-CorelError 'BUSY' 'Another Corel MCP executor holds the application mutex.' }
        Connect-Corel $ProgId
        if ($Tool -ne 'corel_status') { Assert-VbaIdle }
        $drawing = $null
        if ($Parameters.ContainsKey('document_id')) { $drawing = Get-Document $Parameters.document_id }
        switch ($Tool) {
            'corel_status' { $outcome.result = Get-CorelStatus }
            'corel_inspect_document' { $outcome.result = Inspect-Document $drawing $Parameters }
            'corel_create_document' {
                Set-Stage 'creating_document'
                $drawing = $script:Corel.CreateDocument()
                $drawing.Unit = 3
                $drawing.ActivePage.SetSize([double]$Parameters.width_mm, [double]$Parameters.height_mm)
                Sync-Documents
                $outcome.result = Get-DocumentInfo $drawing
            }
            'corel_open_document' {
                Set-Stage 'opening_document'
                $path = [IO.Path]::GetFullPath([string]$Parameters.path)
                if ([IO.Path]::GetExtension($path) -ine '.cdr' -or -not [IO.File]::Exists($path)) { Throw-CorelError 'CDR_NOT_FOUND' 'Supply an existing .cdr file.' }
                Sync-Documents
                $existing = $null
                foreach ($entry in $script:Documents.Values) { if ([string]::Equals([string]$entry.document.FullFileName, $path, [StringComparison]::OrdinalIgnoreCase)) { $existing = $entry.document; break } }
                if ($null -ne $existing) { $drawing = $existing; $drawing.Activate() } else { $drawing = $script:Corel.OpenDocument($path) }
                Sync-Documents
                $outcome.result = Get-DocumentInfo $drawing
            }
            'corel_save_document' { $outcome.result = Save-Document $drawing $Parameters }
            'corel_close_document' {
                Set-Stage 'closing_document'
                if ($drawing.Dirty -and -not $Parameters.discard_changes) { Throw-CorelError 'UNSAVED_CHANGES' 'Save the document or explicitly set discard_changes=true.' }
                if ($Parameters.discard_changes) { $drawing.Dirty = $false }
                $drawing.Close(); Sync-Documents
                $outcome.result = @{ closed = $true; document_id = $Parameters.document_id }
            }
            'corel_export' { $outcome.result = Export-Document $drawing $Parameters $RunDirectory $false }
            'corel_preview' { $outcome.result = Export-Document $drawing $Parameters $RunDirectory $true }
            'corel_build_vba' { $outcome.result = Invoke-Vba $Parameters $RunDirectory $false $null }
            'corel_execute_vba' { $outcome.result = Invoke-Vba $Parameters $RunDirectory $true $drawing }
            default { Throw-CorelError 'UNKNOWN_TOOL' ('Unsupported tool: ' + $Tool) }
        }
        $outcome.ok = $true
    }
    catch {
        $exception = $_.Exception
        $originalStage = $(if ($null -ne $script:FailureStage) { $script:FailureStage } else { $script:Stage })
        $code = 'COM_ERROR'
        $number = [int]$exception.HResult
        $source = [string]$exception.Source
        for ($currentException = $exception; $null -ne $currentException; $currentException = $currentException.InnerException) {
            if ($currentException.Data.Contains('code')) {
                $code = [string]$currentException.Data['code']
                if ($currentException.Data.Contains('number')) { $number = [int]$currentException.Data['number'] }
                if ($currentException.Data.Contains('source')) { $source = [string]$currentException.Data['source'] }
                break
            }
        }
        $outcome.error = @{ code = $code; number = $number; source = $source; message = $exception.Message; stage = $originalStage }
    }
    finally {
        if ($owned) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
        $script:Progress = $null
        $script:ValidateJson = $null
    }
    $outcome.cleanup = $script:Cleanup
    $outcome.compiler_location = $script:Diagnostics
    $outcome.artifacts = @($script:Artifacts)
    $outcome.warnings = @($script:Warnings)
    return $outcome
}
Export-ModuleMember -Function Invoke-CorelOperation
