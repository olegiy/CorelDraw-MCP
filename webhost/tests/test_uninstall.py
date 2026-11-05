import shutil
import subprocess

from conftest import ROOT


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
