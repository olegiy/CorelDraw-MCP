import json
import os
import shutil
import subprocess
import sys

import pytest
from conftest import ROOT


@pytest.mark.parametrize('shell', ['powershell', 'pwsh'])
def test_panel_powershell_scripts_parse_without_execution(shell):
    for name in ('Install', 'Start', 'Stop', 'Restart', 'Verify', 'Uninstall'):
        path = ROOT / 'scripts' / f'{name}-CorelAgent.ps1'
        assert path.is_file()
        literal = str(path).replace("'", "''")
        command = ("$tokens=$null; $errors=$null; "
                   f"[System.Management.Automation.Language.Parser]::ParseFile('{literal}',[ref]$tokens,[ref]$errors) > $null; "
                   "if ($errors.Count) { $errors; exit 1 }")
        # Parse only: no installation, process start/stop, or deletion is performed.
        result = subprocess.run([shell, '-NoProfile', '-Command', command],
                                capture_output=True, timeout=30)
        assert result.returncode == 0, result.stderr.decode(errors='replace')


def script_harness(tmp_path, name, *, arguments=(), python_ok=True, node_version='v24.18.1', fail_call=0, fail_check=''):
    project = tmp_path / 'project with spaces'
    scripts = project / 'scripts'
    scripts.mkdir(parents=True)
    script = scripts / f'{name}-CorelAgent.ps1'
    shutil.copyfile(ROOT / 'scripts' / script.name, script)
    python = project / 'webhost/.venv/Scripts/python.exe'
    python.parent.mkdir(parents=True)
    python.touch()
    (project / 'webui').mkdir()
    log = tmp_path / 'commands.jsonl'
    # Only external CLI commands are replaced. PowerShell executes the actual
    # script, location stack, error checks and argument forwarding in a copy.
    harness = tmp_path / 'harness.ps1'
    harness.write_text('''
$ErrorActionPreference = 'Stop'
function Invoke-Fixture([string]$Program, [object[]]$Arguments) {
    $record = @{program=$Program; arguments=@($Arguments); cwd=(Get-Location).Path}
    [IO.File]::AppendAllText($env:TEST_LOG, ($record | ConvertTo-Json -Compress) + "`n")
    $global:LASTEXITCODE = 0
    if ($Program -eq 'node') {
        if ($Arguments[0] -eq '--version') { $env:TEST_NODE_VERSION }
        if ($env:TEST_FAIL_CHECK -and [IO.Path]::GetFileName([string]$Arguments[0]) -eq $env:TEST_FAIL_CHECK) {
            $global:LASTEXITCODE = 1
        }
        return
    }
    if ($Program -eq 'python' -and $Arguments[0] -eq '-c') {
        $version = if ($env:TEST_PYTHON_OK -eq '1') { '(3, 12)' } else { '(3, 11)' }
        & $env:TEST_REAL_PYTHON -c ("import sys; sys.version_info=$version; " + $Arguments[1])
        return
    }
    $global:callCount++
    if ($global:callCount -eq [int]$env:TEST_FAIL_CALL) { $global:LASTEXITCODE = 1 }
}
function global:node { Invoke-Fixture 'node' $args }
function global:py { throw 'Unexpected real venv creation in fixture' }
function global:npm { Invoke-Fixture 'npm' $args }
Set-Item -LiteralPath ('Function:' + $env:TEST_PYTHON) -Value { Invoke-Fixture 'python' $args }
$global:callCount = 0
& $env:TEST_SCRIPT @args
''', encoding='utf-8')
    env = {**os.environ, 'TEST_LOG': str(log), 'TEST_PYTHON': str(python),
           'TEST_REAL_PYTHON': sys.executable,
           'TEST_SCRIPT': str(script), 'TEST_PYTHON_OK': '1' if python_ok else '0',
           'TEST_NODE_VERSION': node_version, 'TEST_FAIL_CALL': str(fail_call), 'TEST_FAIL_CHECK': fail_check,
           'LOCALAPPDATA': str(tmp_path / 'local')}
    result = subprocess.run(['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass',
                             '-File', str(harness), *arguments], env=env, cwd=tmp_path,
                            capture_output=True, timeout=30)
    calls = [json.loads(line) for line in log.read_text(encoding='utf-8-sig').splitlines()] if log.exists() else []
    return project, result, calls


@pytest.mark.parametrize('python_ok,node_version', [(False, 'v24.18.1'), (True, 'v22.20.0')])
def test_install_refuses_wrong_toolchain_before_installing_packages(tmp_path, python_ok, node_version):
    _, result, calls = script_harness(tmp_path, 'Install', python_ok=python_ok, node_version=node_version)
    assert result.returncode != 0
    assert calls, result.stderr.decode(errors='replace')
    assert not any(call['program'] == 'npm' or call['arguments'][:2] == ['-m', 'pip'] for call in calls)


def test_install_preserves_locked_commands_and_workdirs_with_spaces(tmp_path):
    project, result, calls = script_harness(tmp_path, 'Install')
    assert result.returncode == 0, result.stderr.decode(errors='replace')
    installs = [call for call in calls if call['program'] != 'node' and call['arguments'][0] != '-c']
    assert [(call['program'], call['arguments']) for call in installs] == [
        ('python', ['-m', 'pip', 'install', '--require-hashes', '-r', str(project / 'webhost/requirements.lock.txt')]),
        ('npm', ['ci']), ('npm', ['run', 'build']), ('npm', ['ci']),
        ('npm', ['--prefix', 'codegen', 'ci']), ('npm', ['run', 'build']),
    ]
    assert [call['cwd'] for call in installs[1:]] == [str(project)] * 2 + [str(project / 'webui')] * 3
    assert not (tmp_path / 'local').exists()


@pytest.mark.parametrize('check', ['check-preupgrade.mjs', 'worker-package.mjs'])
def test_install_refuses_unsafe_upgrade_or_worker_before_dependency_install(tmp_path, check):
    _, result, calls = script_harness(tmp_path, 'Install', fail_check=check)
    assert result.returncode != 0
    assert calls[-1]['program'] == 'node'
    assert calls[-1]['arguments'][0].endswith(check)
    assert not any(call['program'] != 'node' for call in calls)
    assert not (tmp_path / 'local').exists()


def test_verify_release_refuses_missing_package_before_running_suites(tmp_path):
    _, result, calls = script_harness(tmp_path, 'Verify', fail_check='worker-package.mjs')
    assert result.returncode != 0
    assert calls == [{'program': 'node', 'arguments': [str(tmp_path / 'project with spaces/scripts/worker-package.mjs')],
                      'cwd': str(tmp_path / 'project with spaces')}]


@pytest.mark.parametrize('fail_call', [1, 2, 3, 4, 5, 6])
def test_install_stops_at_first_package_or_build_failure(tmp_path, fail_call):
    _, result, calls = script_harness(tmp_path, 'Install', fail_call=fail_call)
    assert result.returncode != 0
    installs = [call for call in calls if call['program'] != 'node' and call['arguments'][0] != '-c']
    assert len(installs) == fail_call
    assert not (tmp_path / 'local').exists()


@pytest.mark.parametrize('name,arguments,expected', [
    ('Start', (), ['-m', 'corel_agent']),
    ('Start', ('-NoBrowser',), ['-m', 'corel_agent', '--no-browser']),
    ('Stop', (), ['-m', 'corel_agent', '--stop']),
    ('Restart', (), ['-m', 'corel_agent', '--restart', '--restart-timeout', '15']),
    ('Restart', ('-NoBrowser', '-TimeoutSeconds', '2'),
     ['-m', 'corel_agent', '--restart', '--restart-timeout', '2', '--no-browser']),
])
@pytest.mark.parametrize('fail_call', [0, 1])
def test_control_scripts_forward_only_explicit_flags_and_fail_closed(tmp_path, name, arguments, expected, fail_call):
    project, result, calls = script_harness(tmp_path, name, arguments=arguments, fail_call=fail_call)
    assert (result.returncode == 0) == (fail_call == 0)
    assert calls == [{'program': 'python', 'arguments': expected, 'cwd': str(project / 'webhost')}]
    assert not (tmp_path / 'local').exists()
