#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$utf8 = New-Object Text.UTF8Encoding($false)
[Console]::InputEncoding = $utf8; [Console]::OutputEncoding = $utf8
Add-Type -Path (Join-Path $PSScriptRoot 'vba-dialog.fixture.cs')
try {
    $window = [CorelMcp.Fixtures.Dialogs]::Start()
    [Console]::WriteLine((@{ type = 'ready'; pid = $PID; hwnd = $window } | ConvertTo-Json -Compress))
    while ($null -ne ($line = [Console]::ReadLine())) {
        $command = $line | ConvertFrom-Json
        switch ($command.command) {
            'create' { $result = @{ hwnd = [CorelMcp.Fixtures.Dialogs]::Create($command.profile, $command.text) } }
            'close' { [CorelMcp.Fixtures.Dialogs]::Close($command.hwnd); $result = @{ closed = $true } }
            'stats' { $result = @{ actions = [CorelMcp.Fixtures.Dialogs]::Actions } }
            'hang' { [CorelMcp.Fixtures.Dialogs]::Hang(); $result = @{ hanging = $true } }
            default { throw 'Неизвестная команда fixture.' }
        }
        [Console]::WriteLine(($result | ConvertTo-Json -Compress))
    }
} finally { [CorelMcp.Fixtures.Dialogs]::Stop() }
