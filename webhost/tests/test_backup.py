import sqlite3

import pytest
from conftest import command

from corel_agent.contracts import AgentError


async def test_consistent_backup_preserves_committed_run_without_mutating_original(store):
    run = (await store.accept_run(command(), None)).snapshot
    await store.set_status(run.id, 'completed')
    path = await store.backup()
    assert path.parent == store.directory / 'backups'
    with sqlite3.connect(path) as backup:
        assert backup.execute('PRAGMA quick_check').fetchone()[0] == 'ok'
        assert backup.execute('SELECT id FROM runs').fetchone()[0] == run.id
        assert backup.execute('PRAGMA user_version').fetchone()[0] == 1
    assert (await store.get_snapshot(run.id)).status == 'completed'


async def test_backup_failure_does_not_replace_database_or_release_run(store, monkeypatch):
    run = (await store.accept_run(command(), None)).snapshot
    monkeypatch.setattr(store, '_backup_sync', lambda: (_ for _ in ()).throw(OSError('disk full')))
    with pytest.raises(AgentError) as caught:
        await store.backup()
    assert caught.value.code == 'BACKUP_UNAVAILABLE'
    assert (await store.get_snapshot(run.id)).status == 'preparing'


async def test_backup_rejects_reparse_backup_directory(store):
    import subprocess
    target = store.directory / 'external'
    target.mkdir()
    link = store.directory / 'backups'
    subprocess.run(['cmd.exe', '/c', 'mklink', '/J', str(link), str(target)],
                   capture_output=True, check=True)
    try:
        with pytest.raises(AgentError):
            await store.backup()
        assert list(target.iterdir()) == []
    finally:
        link.rmdir()
