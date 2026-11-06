import os
import shutil
import subprocess
from concurrent.futures import ThreadPoolExecutor

import pytest
from conftest import ROOT, implementation


def test_uninstall_refuses_parent_junction_before_deleting_external_venv(tmp_path):
    project = tmp_path / 'project'
    scripts = project / 'scripts'
    scripts.mkdir(parents=True)
    shutil.copyfile(ROOT / 'scripts/Uninstall-CorelAgent.ps1', scripts / 'Uninstall-CorelAgent.ps1')
    external = tmp_path / 'external'
    venv = external / '.venv'
    venv.mkdir(parents=True)
    sentinel = venv / 'preserve.txt'
    sentinel.write_text('external data')
    link = project / 'webhost'
    subprocess.run(['cmd.exe', '/c', 'mklink', '/J', str(link), str(external)], capture_output=True, check=True)
    try:
        import os
        env = {**os.environ, 'LOCALAPPDATA': str(tmp_path / 'local')}
        result = subprocess.run(['pwsh', '-NoProfile', '-File', str(scripts / 'Uninstall-CorelAgent.ps1')],
                                env=env, capture_output=True, timeout=30)
        assert result.returncode != 0
        assert sentinel.read_text() == 'external data'
    finally:
        link.rmdir()


@pytest.fixture
def uninstall_tree(tmp_path):
    project = tmp_path / 'project with spaces'
    script = project / 'scripts/Uninstall-CorelAgent.ps1'
    script.parent.mkdir(parents=True)
    shutil.copyfile(ROOT / 'scripts/Uninstall-CorelAgent.ps1', script)
    local = tmp_path / 'local'
    disposable = ['webhost/.venv', 'webui/dist', 'webui/node_modules', 'webui/codegen/node_modules']
    retained = [project / name for name in ['dist/corel-mcp.js', 'node_modules/package/index.js',
                'webhost/corel_agent/source.py', 'webui/codegen/package-lock.json', 'codex/config.json']]
    retained += [local / name for name in ['CorelDrawAgent/history.sqlite3', 'CorelDrawAgent/profiles.json',
                 'CorelDrawAgent/assets/image.png', 'CorelDrawMcp/runs/job/report.json', 'Vault-canary']]
    for directory in disposable:
        path = project / directory / 'fixture.txt'
        path.parent.mkdir(parents=True)
        path.write_bytes(b'disposable fixture')
    for path in retained:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(b'preserve fixture bytes')
    return project, script, local, disposable, retained


@pytest.mark.parametrize('what_if', [False, True])
def test_uninstall_removes_only_panel_dependencies_and_preserves_data(uninstall_tree, what_if):
    project, script, local, disposable, retained = uninstall_tree
    result = subprocess.run(['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(script),
                             *(['-WhatIf'] if what_if else [])], env={**os.environ, 'LOCALAPPDATA': str(local)},
                            capture_output=True, timeout=30)
    assert result.returncode == 0, result.stderr.decode(errors='replace')
    for directory in disposable:
        assert (project / directory).exists() is what_if
    for path in retained:
        assert path.read_bytes() == b'preserve fixture bytes'


@pytest.mark.parametrize('marker', ['runtime.json', 'mcp-owner.json'])
def test_uninstall_refuses_any_owner_marker_without_partial_deletion(uninstall_tree, marker):
    project, script, local, disposable, retained = uninstall_tree
    path = local / 'CorelDrawAgent' / marker
    path.write_bytes(b'unknown owner fixture')
    result = subprocess.run(['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(script)],
                            env={**os.environ, 'LOCALAPPDATA': str(local)}, capture_output=True, timeout=30)
    assert result.returncode != 0
    assert path.read_bytes() == b'unknown owner fixture'
    assert all((project / directory / 'fixture.txt').read_bytes() == b'disposable fixture' for directory in disposable)
    assert all(path.read_bytes() == b'preserve fixture bytes' for path in retained)


def test_uninstall_serializes_with_new_host_owner_reservation(uninstall_tree, tmp_path):
    import win32event

    project, script, local, disposable, _ = uninstall_tree
    launcher = implementation('launcher')
    directory = local / 'CorelDrawAgent'
    name = 'Local\\CorelAgentUninstallTest_' + tmp_path.name
    ready = win32event.CreateEvent(None, True, False, name + '_ready')
    proceed = win32event.CreateEvent(None, True, False, name + '_proceed')
    wrapper = tmp_path / 'concurrent-uninstall.ps1'
    wrapper.write_text('''
$ErrorActionPreference = 'Stop'
$global:paused = $false
function Get-ChildItem {
    if (-not $global:paused) {
        $global:paused = $true
        $ready = [Threading.EventWaitHandle]::OpenExisting($env:TEST_EVENT + '_ready')
        $proceed = [Threading.EventWaitHandle]::OpenExisting($env:TEST_EVENT + '_proceed')
        try { $ready.Set() > $null; if (-not $proceed.WaitOne(10000)) { throw 'Fixture timeout' } }
        finally { $ready.Dispose(); $proceed.Dispose() }
    }
    Microsoft.PowerShell.Management\\Get-ChildItem @args
}
& $env:TEST_SCRIPT
''', encoding='utf-8')
    owner = launcher.HostOwner(directory)
    child = subprocess.Popen(['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(wrapper)],
        env={**os.environ, 'LOCALAPPDATA': str(local), 'TEST_EVENT': name, 'TEST_SCRIPT': str(script)},
        stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    try:
        assert win32event.WaitForSingleObject(ready, 10000) == win32event.WAIT_OBJECT_0

        def reserve():
            # This is the real synchronous startup reservation used by serve,
            # without any MCP/COM or a second server.
            with launcher.startup_lock(directory):
                owner.reserve()

        with ThreadPoolExecutor(max_workers=1) as pool:
            future = pool.submit(reserve)
            try:
                from concurrent.futures import TimeoutError
                with pytest.raises(TimeoutError):
                    future.result(timeout=0.2)
            finally:
                win32event.SetEvent(proceed)
            stdout, stderr = child.communicate(timeout=15)
            assert child.returncode == 0, (stdout + stderr).decode(errors='replace')
            future.result(timeout=5)
        assert all(not (project / item).exists() for item in disposable)
        assert (directory / 'runtime.json').exists()
    finally:
        win32event.SetEvent(proceed)
        child.communicate(timeout=15)
        ready.Close()
        proceed.Close()
        if owner.identity:
            owner.release()
