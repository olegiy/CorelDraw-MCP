import subprocess

from conftest import ROOT


def test_panel_powershell_scripts_parse_without_execution():
    for name in ('Install', 'Start', 'Stop', 'Verify', 'Uninstall'):
        path = ROOT / 'scripts' / f'{name}-CorelAgent.ps1'
        assert path.is_file()
        literal = str(path).replace("'", "''")
        command = ("$tokens=$null; $errors=$null; "
                   f"[System.Management.Automation.Language.Parser]::ParseFile('{literal}',[ref]$tokens,[ref]$errors) > $null; "
                   "if ($errors.Count) { $errors; exit 1 }")
        # Parse only: no installation, process start/stop, or deletion is performed.
        result = subprocess.run(['pwsh', '-NoProfile', '-Command', command],
                                capture_output=True, timeout=30)
        assert result.returncode == 0, result.stderr.decode(errors='replace')
