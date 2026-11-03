Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ApiMetadata.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'GeometryExtraction.psm1') -Force
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
    if ($null -ne $script:Progress) { try { & $script:Progress $Name } catch { $script:Warnings += ('Не удалось сообщить этап: ' + $_.Exception.Message) } }
}
function Add-CleanupError([string] $Step, $Exception) {
    $script:Cleanup.completed = $false
    if (-not $script:Cleanup.ContainsKey('errors') -or $null -eq $script:Cleanup.errors) { $script:Cleanup.errors = @() }
    $code = 'CLEANUP_ERROR'
    if ($null -ne $Exception -and $null -ne $Exception.Data -and $Exception.Data.Contains('code')) { $code = [string]$Exception.Data['code'] }
    $script:Cleanup.errors += @{ step = $Step; code = $code; message = [string]$Exception.Message }
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
    if ($version -notmatch '\b27\.') { $script:Corel = $null; Throw-CorelError 'UNSUPPORTED_VERSION' ('Ожидалась версия CorelDRAW 27, получена ' + $version) }
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
    if (-not $script:Documents.ContainsKey($Id)) { Throw-CorelError 'STALE_DOCUMENT' 'Документ закрыт или принадлежит другому сеансу исполнителя. Вызовите corel_status, чтобы получить актуальные идентификаторы.' }
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
    if ($null -eq $editor) { Throw-CorelError 'VBA_UNAVAILABLE' 'Установите VBA и разрешите доступ к его объектной модели в CorelDRAW.' }
    for ($i = 1; $i -le $editor.VBProjects.Count; $i++) {
        if ($editor.VBProjects.Item($i).Mode -ne 2) { Throw-CorelError 'VBA_BUSY' 'VBA выполняется или приостановлен в отладчике. Завершите или сбросьте выполнение в CorelDRAW перед следующей операцией.' }
    }
}
function Get-Page($Document, [hashtable] $Parameters) {
    if ($Parameters.ContainsKey('page_index')) {
        $index = [int]$Parameters.page_index
        if ($index -lt 1 -or $index -gt $Document.Pages.Count) { Throw-CorelError 'PAGE_NOT_FOUND' ('Недопустимый номер страницы: ' + $index) }
        return ,$Document.Pages.Item($index)
    }
    return ,$Document.ActivePage
}
function Get-ShapeInfo($Shape, $Selected, $ParentStaticId = $null, [int]$Depth = 0, $Document = $null, [bool]$IncludeText = $false, [int]$TextLimit = 1000) {
    $staticId = [int]$Shape.StaticID
    $unitToMm = 1.0
    if ($null -ne $Document -and $null -ne $script:Corel) { $unitToMm = [double]$script:Corel.ConvertUnits(1, $Document.Unit, 3) }
    $text = $null; $textTruncated = $false
    if ($IncludeText) { try { $text = [string]$Shape.Text.Story.Text } catch { try { $text = [string]$Shape.Text.Text } catch { $text = $null } }; if ($null -ne $text -and $text.Length -gt $TextLimit) { $text = $text.Substring(0,$TextLimit); $textTruncated = $true } }
    $appearance = $null
    try { $appearance = @{ fill_type = [int]$Shape.Fill.Type; outline_type = [int]$Shape.Outline.Type; outline_width_mm = [double]$Shape.Outline.Width * $unitToMm } } catch { }
    $result = [ordered]@{ static_id=$staticId; name=[string]$Shape.Name; type=[int]$Shape.Type; layer=[string]$Shape.Layer.Name; selected=$Selected.ContainsKey([string]$staticId); depth=$Depth; parent_static_id=$ParentStaticId; bounds=@{left=[double]$Shape.LeftX;bottom=[double]$Shape.BottomY;width=[double]$Shape.SizeWidth;height=[double]$Shape.SizeHeight}; bounds_mm=@{left=[double]$Shape.LeftX*$unitToMm;bottom=[double]$Shape.BottomY*$unitToMm;width=[double]$Shape.SizeWidth*$unitToMm;height=[double]$Shape.SizeHeight*$unitToMm}; rotation=0.0; appearance=$appearance }
    try { $result.rotation = [double]$Shape.RotationAngle } catch { }
    if ($IncludeText) { $result.text=$text; $result.text_truncated=$textTruncated }
    return $result
}
function Add-InspectedShape($Shape, $ParentId, [int]$Depth, $Context) {
    if ($Context.visited -ge $Context.max_visited) { $Context.visited_truncated = $true; return }
    $Context.visited++
    $matches = $true
    if ($null -ne $Context.name_filter -and ([string]$Shape.Name).IndexOf($Context.name_filter, [StringComparison]::OrdinalIgnoreCase) -lt 0) { $matches = $false }
    if ($Context.types.Count -gt 0 -and $Context.types -notcontains [int]$Shape.Type) { $matches = $false }
    if ($Context.ids.Count -gt 0 -and $Context.ids -notcontains [int]$Shape.StaticID) { $matches = $false }
    if ($matches) {
        if ($Context.matched -ge $Context.offset -and $Context.items.Count -lt $Context.limit) { $Context.items.Add((Get-ShapeInfo $Shape $Context.selected $ParentId $Depth $Context.document $Context.include_text $Context.text_limit)) }
        $Context.matched++
    }
    if (-not $Context.recursive -or [int]$Shape.Type -ne 7) { return } # cdrGroupShape
    $children = $Shape.Shapes
    if ($Depth -ge $Context.max_depth) { if ($children.Count -gt 0) { $Context.depth_truncated = $true }; return }
    for ($childIndex = 1; $childIndex -le $children.Count; $childIndex++) {
        if ($Context.visited -ge $Context.max_visited) { $Context.visited_truncated = $true; break }
        Add-InspectedShape $children.Item($childIndex) ([int]$Shape.StaticID) ($Depth + 1) $Context
    }
}
function Inspect-Document($Document, [hashtable] $Parameters) {
    Set-Stage 'inspecting'
    $page = Get-Page $Document $Parameters
    $limit = 100; if ($Parameters.ContainsKey('limit')) { $limit = [int]$Parameters.limit }
    $offset = 0; if ($Parameters.ContainsKey('offset')) { $offset = [int]$Parameters.offset }
    $context = @{ document=$Document; recursive=$false; max_depth=8; max_visited=10000; include_text=$false; text_limit=1000; name_filter=$null; types=@(); ids=@(); selected=@{}; items=(New-Object 'Collections.Generic.List[object]'); visited=0; matched=0; offset=$offset; limit=$limit; depth_truncated=$false; visited_truncated=$false }
    foreach ($option in @('recursive','max_depth','max_visited','include_text','text_limit','name_filter')) { if ($Parameters.ContainsKey($option)) { $context[$option] = $Parameters[$option] } }
    $context.max_depth = [Math]::Max(1, [Math]::Min(32, [int]$context.max_depth))
    $context.max_visited = [Math]::Max(100, [Math]::Min(50000, [int]$context.max_visited))
    $context.text_limit = [Math]::Max(1, [Math]::Min(10000, [int]$context.text_limit))
    if ($Parameters.ContainsKey('shape_types')) { $context.types = @($Parameters.shape_types | ForEach-Object { [int]$_ }) }
    if ($Parameters.ContainsKey('static_ids')) { $context.ids = @($Parameters.static_ids | ForEach-Object { [int]$_ }) }
    $selectionIds = @(); $selection = $Document.SelectionRange
    for ($i=1; $i -le $selection.Count; $i++) { $id=[int]$selection.Item($i).StaticID; $context.selected[[string]$id]=$true; if ($i -le $limit) { $selectionIds += $id } }
    $pages=@(); for ($i=1; $i -le [Math]::Min($Document.Pages.Count,$limit); $i++) { $p=$Document.Pages.Item($i); $pages += @{ index=[int]$p.Index; name=[string]$p.Name; width=[double]$p.SizeWidth; height=[double]$p.SizeHeight } }
    $layers=@(); for ($i=1; $i -le [Math]::Min($page.Layers.Count,$limit); $i++) { $layer=$page.Layers.Item($i); $layers += @{name=[string]$layer.Name; visible=[bool]$layer.Visible; editable=[bool]$layer.Editable; printable=[bool]$layer.Printable; shapes=[int]$layer.Shapes.Count} }
    for ($i=1; $i -le $page.Shapes.Count; $i++) {
        if ($context.visited -ge $context.max_visited) { $context.visited_truncated=$true; break }
        Add-InspectedShape $page.Shapes.Item($i) $null 0 $context
    }
    $end = [Math]::Min($context.matched, $offset+$limit); $shapes=@($context.items.ToArray())
    $selectionBounds=$null; $selectionBoundsMm=$null
    if ($selection.Count -gt 0) {
        $selectionBounds=@{ left=[double]$selection.LeftX; bottom=[double]$selection.BottomY; width=[double]$selection.SizeWidth; height=[double]$selection.SizeHeight }
        $factor=[double]$script:Corel.ConvertUnits(1,$Document.Unit,3)
        $selectionBoundsMm=@{}; foreach($key in $selectionBounds.Keys) { $selectionBoundsMm[$key]=$selectionBounds[$key]*$factor }
    }
    $reasons=@(); if($context.depth_truncated){$reasons+='max_depth'}; if($context.visited_truncated){$reasons+='max_visited'}; if($end -lt $context.matched){$reasons+='limit'}
    return @{ document=(Get-DocumentInfo $Document); pages=$pages; page_index=[int]$page.Index; layers=$layers; selection_ids=$selectionIds; shapes=$shapes
        total_shapes=[int]$page.Shapes.Count; next_offset=$(if($end -lt $context.matched){$end}else{$null}); total_pages=[int]$Document.Pages.Count; total_layers=[int]$page.Layers.Count; total_selected=[int]$selection.Count
        pages_truncated=$Document.Pages.Count -gt $limit; layers_truncated=$page.Layers.Count -gt $limit; selection_truncated=$selection.Count -gt $limit
        visited_shapes=$context.visited; matched_shapes=$context.matched; depth_truncated=$context.depth_truncated; visited_truncated=$context.visited_truncated; shapes_truncated=$end -lt $context.matched; truncation_reasons=$reasons
        selection_bounds=$selectionBounds; selection_bounds_mm=$selectionBoundsMm; reference_point=[int]$Document.ReferencePoint }
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
    catch { $script:Warnings += 'Не удалось получить доступ к редактору VBA: ' + $_.Exception.Message }
    $activeId = $null
    if ($script:Corel.Documents.Count -gt 0) { $activeId = (Get-DocumentInfo $script:Corel.ActiveDocument).document_id }
    return @{
        version = [string]$script:Corel.Version; session_id = $script:SessionId; active_document_id = $activeId; documents = $drawings; vba = $vba
        application_state = @{ optimization = [bool]$script:Corel.Optimization; events_enabled = [bool]$script:Corel.EventsEnabled }
        executor = @{ state = 'ready'; pid = $PID; apartment = [Threading.Thread]::CurrentThread.ApartmentState.ToString(); powershell_version = $PSVersionTable.PSVersion.ToString() }
    }
}
function Get-OutputPath([string] $Path, [string] $Extension) {
    $destination = [IO.Path]::GetFullPath($Path)
    $extensionActual = [IO.Path]::GetExtension($destination).ToLowerInvariant()
    $extensions = @('.' + $Extension)
    if ($Extension -eq 'jpg') { $extensions += '.jpeg' }; if ($Extension -eq 'tiff') { $extensions += '.tif' }
    if ($extensions -notcontains $extensionActual) { Throw-CorelError 'INVALID_EXTENSION' ('Ожидался файл с расширением .' + $Extension + '.') }
    if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($destination))) { Throw-CorelError 'DIRECTORY_NOT_FOUND' 'Родительская папка файла назначения должна существовать.' }
    if ([IO.Directory]::Exists($destination)) { Throw-CorelError 'PATH_IS_DIRECTORY' 'Путь назначения указывает на папку.' }
    return $destination
}
function Save-Document($Document, [hashtable] $Parameters) {
    Set-Stage 'saving'
    $current = [string]$Document.FullFileName
    $destination = $current
    if ($Parameters.ContainsKey('path')) { $destination = [string]$Parameters.path }
    if ([string]::IsNullOrWhiteSpace($destination) -or -not [IO.Path]::IsPathRooted($destination)) { Throw-CorelError 'SAVE_PATH_REQUIRED' 'Для несохранённого документа требуется абсолютный путь к файлу .cdr.' }
    $destination = Get-OutputPath $destination 'cdr'
    $samePath = [string]::Equals($current, $destination, [StringComparison]::OrdinalIgnoreCase)
    if ([IO.File]::Exists($destination) -and -not $samePath -and -not $Parameters.overwrite) { Throw-CorelError 'FILE_EXISTS' 'Файл назначения уже существует. Для замены укажите overwrite=true.' }
    Sync-Documents
    foreach ($entry in $script:Documents.Values) {
        if ((Get-ComIdentity $entry.document) -ne (Get-ComIdentity $Document) -and [string]::Equals([string]$entry.document.FullFileName, $destination, [StringComparison]::OrdinalIgnoreCase)) { Throw-CorelError 'DOCUMENT_ALREADY_OPEN' 'Файл назначения открыт в другом документе.' }
    }
    if ($samePath) { $Document.Save() }
    else {
        $options = $script:Corel.CreateStructSaveAsOptions()
        $options.Overwrite = [bool]$Parameters.overwrite
        $options.Filter = 1795 # cdrCDR, API 27
        $Document.SaveAs($destination, $options)
    }
    if (-not [IO.File]::Exists($destination) -or (Get-Item -LiteralPath $destination).Length -eq 0) { Throw-CorelError 'SAVE_NOT_CREATED' 'CorelDRAW не создал непустой файл CDR.' }
    $script:Artifacts += $destination
    return @{ document = (Get-DocumentInfo $Document); path = $destination; bytes = (Get-Item -LiteralPath $destination).Length }
}
function Convert-TemporaryTextToCurves($Shape) {
    if ([int]$Shape.Type -eq 6) { $Shape.ConvertToCurves(); return }
    if ([int]$Shape.Type -eq 7) { for ($child=1; $child -le $Shape.Shapes.Count; $child++) { Convert-TemporaryTextToCurves $Shape.Shapes.Item($child) } }
}
function Find-ExportShape($Shapes, [int]$StaticId) {
    for ($i = 1; $i -le $Shapes.Count; $i++) {
        $shape = $Shapes.Item($i)
        if ([int]$shape.StaticID -eq $StaticId) { return ,$shape }
        if ([int]$shape.Type -eq 7) {
            $nested = Find-ExportShape $shape.Shapes $StaticId
            if ($null -ne $nested) { return ,$nested }
        }
    }
    return $null
}
function Assert-ExportGeometry($Source, $Copy, [double]$MmPerUnit) {
    if ($null -eq $Copy -or [int]$Source.StaticID -ne [int]$Copy.StaticID -or [int]$Source.Type -ne [int]$Copy.Type) {
        Throw-CorelError 'COPY_GEOMETRY_MISMATCH' 'Изолированная копия изменила StaticID или тип объекта.'
    }
    foreach ($property in @('LeftX','BottomY','SizeWidth','SizeHeight','RotationAngle')) {
        try { $delta = ([double]$Source.$property - [double]$Copy.$property) * $MmPerUnit } catch { Throw-CorelError 'COPY_GEOMETRY_MISMATCH' ('Не удалось проверить геометрию: ' + $property) }
        if ([double]::IsNaN($delta) -or [double]::IsInfinity($delta) -or [Math]::Abs($delta) -gt 0.0000001) { Throw-CorelError 'COPY_GEOMETRY_MISMATCH' ('Копирование изменило геометрию: ' + $property) }
    }
    if ([int]$Source.Type -eq 7) {
        if ($Source.Shapes.Count -ne $Copy.Shapes.Count) { Throw-CorelError 'COPY_GEOMETRY_MISMATCH' 'Копирование изменило состав группы.' }
        for ($i = 1; $i -le $Source.Shapes.Count; $i++) { Assert-ExportGeometry $Source.Shapes.Item($i) $Copy.Shapes.Item($i) $MmPerUnit }
    }
}
function Export-Document($Document, [hashtable] $Parameters, [string] $RunDirectory, [bool] $Preview) {
    Set-Stage 'exporting'
    $format = $(if ($Preview) { 'png' } else { ([string]$Parameters.format).ToLowerInvariant() })
    if ($format -eq 'jpeg') { $format = 'jpg' }; if ($format -eq 'tif') { $format = 'tiff' }
    if ($format -notin @('png','svg','pdf','jpg','tiff','ai','eps','dxf','plt')) { Throw-CorelError 'UNSUPPORTED_FORMAT' ('Неподдерживаемый формат: ' + $format) }
    $range = $(if ($Parameters.ContainsKey('range')) { ([string]$Parameters.range).ToLowerInvariant() } else { 'page' })
    if (($format -eq 'pdf' -and $range -eq 'selection') -or ($format -ne 'pdf' -and $range -eq 'document')) { Throw-CorelError 'UNSUPPORTED_EXPORT_RANGE' 'Для этого формата доступны только страница или выделение; PDF поддерживает страницу или документ.' }
    $destination = $(if ($Preview) { Join-Path $RunDirectory 'preview.png' } else { Get-OutputPath $Parameters.path $format })
    $overwrite = -not $Preview -and [bool]$Parameters.overwrite
    if ([IO.File]::Exists($destination) -and -not $overwrite) { Throw-CorelError 'FILE_EXISTS' 'Файл экспорта уже существует; для замены укажите overwrite=true.' }
    $temporary = Join-Path ([IO.Path]::GetDirectoryName($destination)) ('.corelmcp-' + [guid]::NewGuid().ToString('N') + '.' + $format)
    $previousDocument = $script:Corel.ActiveDocument
    $previousPage = $Document.ActivePage
    $pdfRange = $null
    $pdfPages = $null
    $pdfPagesCaptured = $false
    $temporaryDocument = $null
    $exportDocument = $Document
    try {
        $Document.Activate()
        $page = Get-Page $Document $Parameters
        if ($range -eq 'selection' -and $page.Index -ne $Document.ActivePage.Index) { Throw-CorelError 'SELECTION_PAGE_MISMATCH' 'Экспорт выделения требует текущую страницу документа.' }
        $page.Activate()
        $exportRange = 1 # cdrCurrentPage
        if ($range -eq 'selection') {
            if ($Document.SelectionRange.Count -eq 0) { Throw-CorelError 'EMPTY_SELECTION' 'В целевом документе нет выделенных объектов.' }
            $exportRange = 2 # cdrSelection
        }
        $convertText = $format -in @('dxf','plt') -or ($format -in @('ai','eps') -and $Parameters.ContainsKey('text_mode') -and $Parameters.text_mode -eq 'curves')
        if ($convertText) {
            Set-Stage 'copying_export_geometry'
            if ($range -eq 'selection') { $sourceShapes = $Document.SelectionRange } else { $sourceShapes = $page.Shapes }
            $rangeObject = $script:Corel.CreateShapeRange()
            for ($index=1; $index -le $sourceShapes.Count; $index++) { $rangeObject.Add($sourceShapes.Item($index)) }
            $temporaryDocument = $rangeObject.CreateDocumentFrom($false)
            if ($null -eq $temporaryDocument) { Throw-CorelError 'COPY_GEOMETRY_MISMATCH' 'Не удалось создать изолированный документ экспорта.' }
            # CreateDocumentFrom uses Corel's default display unit for the new
            # document. Set that display unit explicitly before comparing
            # coordinates; this only changes the owned temporary document.
            $temporaryDocument.Unit = $Document.Unit
            $temporaryDocument.ActivePage.SetSize([double]$page.SizeWidth, [double]$page.SizeHeight)
            if ([int]$temporaryDocument.Unit -ne [int]$Document.Unit) { Throw-CorelError 'COPY_GEOMETRY_MISMATCH' 'Изолированная копия изменила единицы документа.' }
            $mmPerUnit = [double]$script:Corel.ConvertUnits(1, $Document.Unit, 3)
            for ($index=1; $index -le $sourceShapes.Count; $index++) {
                $source = $sourceShapes.Item($index)
                $copy = Find-ExportShape $temporaryDocument.ActivePage.Shapes ([int]$source.StaticID)
                Assert-ExportGeometry $source $copy $mmPerUnit
                Convert-TemporaryTextToCurves $copy
            }
            $exportDocument = $temporaryDocument
            $temporaryDocument.Activate()
            $exportRange = 1
        }
        if ($format -eq 'pdf') {
            $pdfRange = $Document.PDFSettings.PublishRange
            $pdfPages = $Document.PDFSettings.PageRange
            $pdfPagesCaptured = $true
            if ($range -eq 'document') { $Document.PDFSettings.PublishRange = 0 }
            else { $Document.PDFSettings.PublishRange = 3; $Document.PDFSettings.PageRange = [string]$page.Index }
            $Document.PublishToPDF($temporary)
        }
        elseif ($format -in @('svg','ai','eps','dxf','plt')) {
            # Corel's ExportEx/ExportBitmap optional interface parameters do not
            # marshal reliably through Windows PowerShell's late-bound COM adapter.
            # Compile a typed native VBA call using the same isolated GMS engine.
            $literalPath = $temporary.Replace('"', '""')
            $filter = switch ($format) { 'svg' { 'cdrSVG' }; 'ai' { 'cdrAI' }; 'eps' { 'cdrEPS' }; 'dxf' { 'cdrDXF' }; 'plt' { 'cdrPLT' } }
            $textOption = ''
            if ($format -in @('ai','eps')) {
                $asCurves = $Parameters.ContainsKey('text_mode') -and $Parameters.text_mode -eq 'curves'
                $textOption = "f.TextAsCurves = " + $(if ($asCurves) { 'True' } else { 'False' }) + "`n"
            }
            if ($format -eq 'plt') { $textOption += "f.PlotterUnits = 1000`n" }
            $source = "Option Explicit`nPublic Function Main() As String`nDim f As ExportFilter`nSet f = ActiveDocument.ExportEx(""$literalPath"", $filter, $exportRange)`n${textOption}f.Finish`nMain = ""{}""`nEnd Function"
            Invoke-Vba @{ code = $source } $RunDirectory $true $exportDocument $true | Out-Null
        }
        else {
            $width = [double]$page.SizeWidth; $height = [double]$page.SizeHeight
            if ($range -eq 'selection') { $width = [double]$Document.SelectionRange.SizeWidth; $height = [double]$Document.SelectionRange.SizeHeight }
            if ($width -le 0 -or $height -le 0) { Throw-CorelError 'EMPTY_EXPORT_AREA' 'Ширина и высота области экспорта должны быть положительными.' }
            $dpi = $(if ($Preview) { 96 } elseif ($Parameters.ContainsKey('dpi')) { [int]$Parameters.dpi } else { 150 })
            if ($dpi -lt 1 -or $dpi -gt 1200) { Throw-CorelError 'INVALID_DPI' 'Значение dpi должно быть от 1 до 1200.' }
            if ($Preview) {
                $scale = [double]$Parameters.max_size / [Math]::Max($width, $height)
                $pixelWidth = [int][Math]::Max(1, [Math]::Round($width * $scale))
                $pixelHeight = [int][Math]::Max(1, [Math]::Round($height * $scale))
            }
            else {
                $pixelWidth = [Math]::Max(1, [Math]::Ceiling($script:Corel.ConvertUnits($width, $Document.Unit, 1) * $dpi))
                $pixelHeight = [Math]::Max(1, [Math]::Ceiling($script:Corel.ConvertUnits($height, $Document.Unit, 1) * $dpi))
                if ($pixelWidth * $pixelHeight -gt 40000000) { Throw-CorelError 'EXPORT_TOO_LARGE' 'Экспорт превышает 40 мегапикселей; уменьшите dpi или область.' }
            }
            $literalPath = $temporary.Replace('"', '""')
            # cdrCurrentPage chooses objects, but bitmap export otherwise crops
            # to their bounds. An explicit area preserves the actual page frame.
            $exportArea = $(if ($range -eq 'page') { ', ExportArea:=ActivePage.BoundingBox' } else { '' })
            $filter = 'cdrPNG'; $imageType = 'cdrRGBColorImage'; $extraOptions = ''; $filterOptions = ''
            if ($format -in @('jpg','tiff')) {
                $filter = $(if ($format -eq 'jpg') { 'cdrJPEG' } else { 'cdrTIFF' })
                if ($Parameters.ContainsKey('color_mode') -and $Parameters.color_mode -eq 'cmyk') { $imageType = 'cdrCMYKColorImage' }
                $profile = 'True'; if ($Parameters.ContainsKey('use_document_profile') -and -not $Parameters.use_document_profile) { $profile = 'False' }
                $transparent = 'False'; if ($format -eq 'tiff' -and $Parameters.ContainsKey('transparent') -and $Parameters.transparent) { $transparent = 'True' }
                $extraOptions = ', UseColorProfile:=' + $profile
                if ($format -eq 'tiff') { $compression = 'cdrCompressionLZW'; if ($Parameters.ContainsKey('tiff_compression') -and $Parameters.tiff_compression -eq 'none') { $compression = 'cdrCompressionNone' }; $extraOptions += ', Compression:=' + $compression }
                if ($format -eq 'jpg') { $quality = 80; if ($Parameters.ContainsKey('jpeg_compression')) { $quality = [int]$Parameters.jpeg_compression }; $filterOptions = "f.Compression = $quality`n" }
            }
            else { $transparent = 'False' }
            $source = "Option Explicit`nPublic Function Main() As String`nDim f As ExportFilter`nSet f = ActiveDocument.ExportBitmap(""$literalPath"", $filter, $exportRange, $imageType, $pixelWidth, $pixelHeight, $dpi, $dpi, cdrNormalAntiAliasing, False, $transparent$extraOptions$exportArea)`n${filterOptions}f.Finish`nMain = ""{}""`nEnd Function"
            Invoke-Vba @{ code = $source } $RunDirectory $true $Document $true | Out-Null
        }
        if (-not [IO.File]::Exists($temporary) -or (Get-Item -LiteralPath $temporary).Length -eq 0) { Throw-CorelError 'EXPORT_NOT_CREATED' 'CorelDRAW не создал непустой файл экспорта.' }
        Set-Stage 'committing_export'
        if ([IO.File]::Exists($destination)) {
            if (-not $overwrite) { Throw-CorelError 'FILE_EXISTS' 'Файл назначения появился во время экспорта; замена не выполнена.' }
            [IO.File]::Replace($temporary, $destination, [System.Management.Automation.Language.NullString]::Value)
        }
        else { [IO.File]::Move($temporary, $destination) }
        $script:Artifacts += $destination
        return @{ path = $destination; bytes = (Get-Item -LiteralPath $destination).Length; format = $format; range = $range }
    }
    finally {
        # Cleanup is deliberately independent: a failed PublishRange restore
        # must not prevent PageRange, page, document, or temporary-file cleanup.
        if ($null -ne $temporaryDocument) {
            try { $temporaryDocument.Dirty = $false; $temporaryDocument.Close() } catch { Add-CleanupError 'export.temporary_document' $_.Exception }
        }
        if ($null -ne $pdfRange) {
            try { $Document.PDFSettings.PublishRange = $pdfRange } catch { Add-CleanupError 'export.publish_range' $_.Exception }
            if ($pdfPagesCaptured) { try { $Document.PDFSettings.PageRange = $pdfPages } catch { Add-CleanupError 'export.page_range' $_.Exception } }
        }
        try { $previousPage.Activate() } catch { Add-CleanupError 'export.page' $_.Exception }
        if ($null -ne $previousDocument) { try { $previousDocument.Activate() } catch { Add-CleanupError 'export.document' $_.Exception } }
        if ([IO.File]::Exists($temporary)) { try { [IO.File]::Delete($temporary) } catch { Add-CleanupError 'export.temporary_file' $_.Exception } }
    }
}
function Get-VbaProject($Editor, [string] $Name) {
    for ($i = 1; $i -le $Editor.VBProjects.Count; $i++) {
        $candidate = $Editor.VBProjects.Item($i)
        if ([string]::Equals([string]$candidate.Name, $Name, [StringComparison]::Ordinal)) { return ,$candidate }
    }
    Throw-CorelError 'VBA_PROJECT_NOT_FOUND' ('Не найден созданный проект: ' + $Name)
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
        if ($utf8.GetByteCount([string]$Parameters.code) -gt 262144) { Throw-CorelError 'SOURCE_TOO_LARGE' 'Размер сценария в UTF-8 превышает 256 КиБ.' }
        $snapshots += @{ name = 'M_UserCode'; code = [string]$Parameters.code }
    }
    else {
        $root = (Resolve-Path -LiteralPath $Parameters.source_root).ProviderPath
        if (-not [IO.Directory]::Exists($root)) { Throw-CorelError 'SOURCE_DIRECTORY_REQUIRED' 'Параметр source_root должен указывать на папку.' }
        $rootPrefix = $root.TrimEnd('\') + '\'
        $files = @()
        if ($Parameters.ContainsKey('module_paths')) {
            foreach ($relativePath in $Parameters.module_paths) {
                if ([IO.Path]::IsPathRooted($relativePath)) { Throw-CorelError 'INVALID_MODULE_PATH' 'Пути модулей должны быть относительно source_root.' }
                $file = Get-Item -LiteralPath (Join-Path $root $relativePath)
                if (-not $file.FullName.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) { Throw-CorelError 'INVALID_MODULE_PATH' 'Пути модулей должны находиться внутри source_root.' }
                $files += $file
            }
        }
        else { $files = @(Get-ChildItem -LiteralPath $root -Filter '*.bas' -File | Sort-Object Name) }
        if ($files.Count -eq 0 -or $files.Count -gt 100) { Throw-CorelError 'INVALID_MODULE_COUNT' 'Укажите от 1 до 100 стандартных модулей .bas.' }
        $names = @{}
        $totalBytes = 0
        foreach ($file in $files) {
            if ($file.PSIsContainer -or $file.Extension -ine '.bas') { Throw-CorelError 'INVALID_MODULE_TYPE' 'Поддерживаются только стандартные файлы .bas.' }
            $ancestor = $file
            while ($null -ne $ancestor -and $ancestor.FullName.Length -gt $root.Length) {
                if (($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Throw-CorelError 'MODULE_REPARSE_POINT' 'Пути модулей внутри source_root не должны проходить через символические ссылки или точки соединения.' }
                $ancestor = $(if ($ancestor -is [IO.FileInfo]) { $ancestor.Directory } else { $ancestor.Parent })
            }
            $name = $file.BaseName
            if ($name -notmatch '^[A-Za-z][A-Za-z0-9_]{0,30}$' -or $names.ContainsKey($name)) { Throw-CorelError 'INVALID_MODULE_NAME' ('Недопустимое или повторяющееся имя модуля VBA: ' + $name) }
            $names[$name] = $true
            $totalBytes += $file.Length
            if ($totalBytes -gt 2097152) { Throw-CorelError 'SOURCES_TOO_LARGE' 'Общий размер исходных файлов превышает 2 МиБ.' }
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
function Invoke-Vba([hashtable] $Parameters, [string] $RunDirectory, [bool] $Execute, $Document, [bool] $ReadOnly = $false) {
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
        if ($editor.ActiveVBProject.Name -ne $projectName) { Throw-CorelError 'COMPILER_PROJECT_MISMATCH' 'Редактор VBE не активировал созданный проект VBA.' }
        Set-Stage 'compiling'
        $compile = $editor.CommandBars.FindControl(1, 578)
        if ($null -eq $compile) { Throw-CorelError 'COMPILER_UNAVAILABLE' 'Команда компиляции VBE 578 недоступна.' }
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
            Throw-CorelError 'COMPILATION_FAILED' 'Компиляция не завершена. Проверьте compiler_location и сохранённый GMS в редакторе VBA; возможно, требуется закрыть модальное окно компилятора.'
        }
        if (-not $Execute) {
            $succeeded = $true
            return @{ compiled = $true; project = $projectName; modules = @($snapshots | ForEach-Object { $_.name }); gms_path = $gmsPath }
        }
        Set-Stage 'executing_vba'
        $Document.Activate()
        $previousOptimization = [bool]$script:Corel.Optimization
        $previousEvents = [bool]$script:Corel.EventsEnabled
        if (-not $ReadOnly) {
            $previousSelectionMode = [bool]$Document.PreserveSelection
            $Document.SaveSettings(); $settingsSaved = $true
            $Document.BeginCommandGroup('Corel MCP VBA'); $groupStarted = $true
            $Document.PreserveSelection = $false
        }
        $script:Corel.Optimization = $true
        $script:Corel.EventsEnabled = $false
        $raw = [string]$script:Corel.GMSManager.RunMacro($projectName, 'M_CorelMcpHarness.RunMain')
        if ($raw.StartsWith("ERROR`n")) {
            $parts = $raw -split "`n", 4
            $failure = New-Object InvalidOperationException($parts[3])
            $failure.Data['code'] = 'VBA_RUNTIME_ERROR'; $failure.Data['number'] = [int]$parts[1]; $failure.Data['source'] = $parts[2]
            throw $failure
        }
        if (-not $raw.StartsWith("OK`n")) { Throw-CorelError 'INVALID_VBA_RESULT' 'Исполнитель VBA вернул неожиданный результат.' }
        $json = $raw.Substring(3)
        if ([Text.Encoding]::UTF8.GetByteCount($json) -gt 1048576) { Throw-CorelError 'VBA_RESULT_TOO_LARGE' 'Размер результата JSON превышает 1 МиБ.' }
        # Validate with the host's strict JSON parser before unloading the GMS.
        # Keep the raw JSON in IPC: PowerShell 5.1 otherwise unwraps singleton
        # arrays and loses the distinction between [] and null.
        Set-Stage 'validating_result'
        if ($null -eq $script:ValidateJson) { Throw-CorelError 'VALIDATION_UNAVAILABLE' 'Канал проверки JSON исполнителя недоступен.' }
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
                catch { Add-CleanupError ('vba.' + $step.name.Replace(' ', '_')) $_.Exception }
            }
        }
        if ($null -ne $previousDocument) { try { $previousDocument.Activate() } catch { Add-CleanupError 'vba.document' $_.Exception } }
        if ($succeeded -and $null -ne $gms) {
            if ($null -ne $previousProject) { try { $editor.ActiveVBProject = $previousProject } catch { Add-CleanupError 'vba.active_project' $_.Exception } }
            try { $gms.Unload(); $script:Cleanup.project_retained = $false } catch { Add-CleanupError 'vba.gms_unload' $_.Exception }
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
    $script:Cleanup = @{ completed = $true; errors = @() }
    $mutex = New-Object Threading.Mutex($false, 'Local\CorelDrawMcp_v27')
    $owned = $false
    $outcome = @{ ok = $false; result = $null; error = $null }
    try {
        try { $owned = $mutex.WaitOne(0) }
        catch [Threading.AbandonedMutexException] { $owned = $true; $script:Warnings += 'Предыдущий исполнитель оставил мьютекс без освобождения; проверьте более ранние задания со статусом unknown перед их повторением.' }
        if (-not $owned) { Throw-CorelError 'BUSY' 'Другой исполнитель Corel MCP удерживает мьютекс приложения.' }
        Connect-Corel $ProgId
        if ($Tool -ne 'corel_status') { Assert-VbaIdle }
        $drawing = $null
        if ($Parameters.ContainsKey('document_id')) { $drawing = Get-Document $Parameters.document_id }
        switch ($Tool) {
            'corel_status' { $outcome.result = Get-CorelStatus }
            'corel_inspect_document' { $outcome.result = Inspect-Document $drawing $Parameters }
            'corel_describe_api' { $outcome.result = Get-CorelApiMetadata $script:Corel $Parameters }
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
                if ([IO.Path]::GetExtension($path) -ine '.cdr' -or -not [IO.File]::Exists($path)) { Throw-CorelError 'CDR_NOT_FOUND' 'Укажите существующий файл .cdr.' }
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
                if ($drawing.Dirty -and -not $Parameters.discard_changes) { Throw-CorelError 'UNSAVED_CHANGES' 'Сохраните документ или явно укажите discard_changes=true.' }
                if ($Parameters.discard_changes) { $drawing.Dirty = $false }
                $drawing.Close(); Sync-Documents
                $outcome.result = @{ closed = $true; document_id = $Parameters.document_id }
            }
            'corel_export' { $outcome.result = Export-Document $drawing $Parameters $RunDirectory $false }
            'corel_preview' { $outcome.result = Export-Document $drawing $Parameters $RunDirectory $true }
            'corel_measure_shapes' {
                Set-Stage 'extracting_geometry'
                $runVba = {
                    param($code, $temporaryDocument, $readOnly)
                    Invoke-Vba @{ code = $code } $RunDirectory $true $temporaryDocument $true
                }
                $cleanupError = { param($step, $exception) Add-CleanupError $step $exception }
                $outcome.result = Invoke-GeometrySnapshot $script:Corel $drawing $Parameters $RunDirectory $runVba $cleanupError
                if ($null -ne $outcome.result.snapshot_path) { $script:Artifacts += [string]$outcome.result.snapshot_path }
            }
            'corel_build_vba' { $outcome.result = Invoke-Vba $Parameters $RunDirectory $false $null }
            'corel_execute_vba' { $outcome.result = Invoke-Vba $Parameters $RunDirectory $true $drawing }
            default { Throw-CorelError 'UNKNOWN_TOOL' ('Неподдерживаемый инструмент: ' + $Tool) }
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
        if ($owned -and $null -ne $script:Corel) {
            try {
                $corelVersion = [regex]::Match([string]$script:Corel.Version, '\d+(?:\.\d+){1,3}').Value
                $outcome.versions = @{ powershell = $PSVersionTable.PSVersion.ToString(); coreldraw = $(if ($corelVersion) { $corelVersion } else { [string]$script:Corel.Version }) }
            } catch { }
        }
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
