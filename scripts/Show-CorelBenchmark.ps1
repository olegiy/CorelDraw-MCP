#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][int]$CorelPid,
    [Parameter(Mandatory=$true)][string]$DocumentPath,
    [Parameter(Mandatory=$true)][string]$RunDirectory
)
$ErrorActionPreference='Stop'
$instances=@(Get-Process -Name CorelDRW -ErrorAction Stop)
if($instances.Count -ne 1 -or $instances[0].Id -ne $CorelPid){throw 'Нужен единственный процесс CorelDRAW с указанным PID.'}
if(-not [IO.Path]::IsPathRooted($RunDirectory)){throw 'Нужен абсолютный каталог диагностики.'}
$binding=& node (Join-Path $PSScriptRoot 'verify-benchmark-document.mjs') $DocumentPath
if($LASTEXITCODE -ne 0){throw 'Точный активный benchmark-документ не подтверждён.'}
$windows=(& agent-desktop list-windows --app CorelDRW | ConvertFrom-Json)
if(-not $windows.ok){throw 'Не удалось прочитать окна CorelDRAW.'}
$main=@($windows.data | Where-Object {$_.pid -eq $CorelPid -and $_.title -like 'CorelDRAW*'})
if($main.Count -ne 1){throw 'Главное окно CorelDRAW не определено однозначно.'}
[IO.Directory]::CreateDirectory($RunDirectory) | Out-Null
$stamp=[Guid]::NewGuid().ToString('N')
if($main[0].minimized){
    & agent-desktop --headed restore --window-id $main[0].id |
        Set-Content -LiteralPath (Join-Path $RunDirectory "$stamp-restore.json") -Encoding UTF8
    $updated=(& agent-desktop list-windows --app CorelDRW | ConvertFrom-Json)
    $visible=@($updated.data | Where-Object {$_.id -eq $main[0].id -and -not $_.minimized})
    if(-not $updated.ok -or $visible.Count -ne 1){throw 'Окно осталось свёрнутым; видимый показ не подтверждён.'}
}
$focus=& agent-desktop --headed focus-window --window-id $main[0].id
$focus | Set-Content -LiteralPath (Join-Path $RunDirectory "$stamp-focus.json") -Encoding UTF8
& agent-desktop snapshot --window-id $main[0].id --compact --include-bounds |
    Set-Content -LiteralPath (Join-Path $RunDirectory "$stamp-snapshot.json") -Encoding UTF8
& agent-desktop screenshot --window-id $main[0].id (Join-Path $RunDirectory "$stamp.png")
if($LASTEXITCODE -ne 0){throw 'Снимок холста не получен; показ не подтверждён.'}
# Фокус/Activate не доказывает видимость рисунка: оператор должен прочитать снимок.
[ordered]@{binding=($binding | ConvertFrom-Json);window_id=$main[0].id;screenshot=(Join-Path $RunDirectory "$stamp.png");requires_visual_inspection=$true} | ConvertTo-Json -Depth 8
