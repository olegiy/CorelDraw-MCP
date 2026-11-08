import asyncio
import json
import os
import subprocess
import sys
from pathlib import Path

import pytest
from conftest import command, implementation

from corel_agent.contracts import AgentError, ProcessIdentity
from corel_agent.owned_stdio import identity_alive, new_id


@pytest.fixture(autouse=True)
def isolated_restart_port(monkeypatch):
    # Inert owners have no HTTP listener; reserve a test-only loopback port.
    reserve = implementation('launcher').reserve_port
    monkeypatch.setattr(implementation('restart'), 'reserve_port', lambda: reserve(0))


@pytest.fixture
async def store(tmp_path):
    from corel_agent.storage_acl import ensure_private
    directory = tmp_path / 'private-host'
    ensure_private(directory)
    value = implementation('store').Store(directory)
    await value.open()
    try:
        yield value
    finally:
        await value.aclose()


@pytest.fixture
def hung_owner(tmp_path, monkeypatch):
    monkeypatch.setenv('LOCALAPPDATA', str(tmp_path))
    (tmp_path / 'CorelDrawMcp/runs').mkdir(parents=True)
    processes = []

    def start(directory, *arguments):
        process = subprocess.Popen([sys.executable, str(Path(__file__).parent / 'fixtures/restart_owner.py'),
                                    str(directory), *arguments], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        record = json.loads(process.stdout.readline())
        processes.append((process, record))
        return record

    yield start
    # Only test-created inert processes, never real CorelDRAW or installed MCP.
    import win32api
    import win32event
    import win32process
    for process, record in processes:
        for key in ('host', 'child'):
            identity = ProcessIdentity.model_validate(record[key])
            if identity_alive(identity) is True:
                handle = win32api.OpenProcess(0x1001 | win32event.SYNCHRONIZE, False, identity.pid)
                try:
                    assert win32process.GetProcessTimes(handle)['CreationTime'].isoformat() == identity.creation_time
                    try:
                        win32api.TerminateProcess(handle, 0)
                    except Exception:
                        assert win32event.WaitForSingleObject(handle, 5000) == win32event.WAIT_OBJECT_0
                    win32event.WaitForSingleObject(handle, 5000)
                finally:
                    handle.Close()
        process.wait(timeout=5)


async def test_restart_reaps_only_verified_idle_owner_and_preserves_history(store, hung_owner):
    restart = implementation('restart')
    accepted = await store.accept_run(command(), None)
    await store.set_status(accepted.snapshot.id, 'completed')
    record = hung_owner(store.directory)
    profile = store.directory / 'profiles.json'
    profile.write_text('{"keep":true}', encoding='utf-8')
    await asyncio.to_thread(restart.stop_for_restart, store.directory, timeout=0)
    assert identity_alive(ProcessIdentity.model_validate(record['host'])) is False
    assert identity_alive(ProcessIdentity.model_validate(record['child'])) is False
    assert not (store.directory / 'runtime.json').exists()
    assert not (store.directory / 'mcp-owner.json').exists()
    assert (await store.get_snapshot(accepted.snapshot.id)).status == 'completed'
    assert profile.read_text(encoding='utf-8') == '{"keep":true}'


async def test_restart_reads_legacy_completed_snapshot_without_rewriting_it(store, hung_owner):
    restart = implementation('restart')
    run = (await store.accept_run(command(), None)).snapshot
    await store.set_status(run.id, 'completed')
    def legacy(db):
        data = json.loads(db.execute('SELECT snapshot FROM runs WHERE id=?', (run.id,)).fetchone()[0])
        data['read_only'] = True
        data['pending_confirmation'] = None
        raw = json.dumps(data)
        db.execute('UPDATE runs SET snapshot=? WHERE id=?', (raw, run.id))
        return raw
    original = await store._db(legacy, write=True)
    record = hung_owner(store.directory)
    await asyncio.to_thread(restart.stop_for_restart, store.directory, timeout=0)
    assert identity_alive(ProcessIdentity.model_validate(record['child'])) is False
    assert await store._db(lambda db: db.execute('SELECT snapshot FROM runs WHERE id=?', (run.id,)).fetchone()[0]) == original


async def test_restart_port_guard_still_blocks_before_any_termination(store, hung_owner, monkeypatch):
    restart = implementation('restart')
    record = hung_owner(store.directory)
    reserve = implementation('launcher').reserve_port
    listener = reserve(0)
    monkeypatch.setattr(restart, 'reserve_port', lambda: reserve(listener.getsockname()[1]))
    try:
        with pytest.raises(AgentError) as failure:
            await asyncio.to_thread(restart.stop_for_restart, store.directory, timeout=0)
        assert failure.value.code == 'PORT_BUSY'
        assert identity_alive(ProcessIdentity.model_validate(record['host'])) is True
        assert identity_alive(ProcessIdentity.model_validate(record['child'])) is True
    finally:
        listener.close()


async def test_restart_refuses_unfinished_run_without_killing_or_releasing(store, hung_owner):
    restart = implementation('restart')
    await store.accept_run(command(), None)
    record = hung_owner(store.directory)
    original = (store.directory / 'runtime.json').read_bytes()
    with pytest.raises(AgentError) as failure:
        await asyncio.to_thread(restart.stop_for_restart, store.directory, timeout=0)
    assert failure.value.code == 'RESTART_NOT_IDLE'
    assert identity_alive(ProcessIdentity.model_validate(record['host'])) is True
    assert identity_alive(ProcessIdentity.model_validate(record['child'])) is True
    assert (store.directory / 'runtime.json').read_bytes() == original


@pytest.mark.parametrize('status,finished', [('running', False), ('timed_out', False), ('unknown', True), ('succeeded', False)])
async def test_restart_refuses_untracked_incomplete_native_report(store, hung_owner, status, finished):
    restart = implementation('restart')
    record = hung_owner(store.directory)
    job_id = new_id()
    directory = Path(os.environ['LOCALAPPDATA']) / 'CorelDrawMcp/runs' / job_id
    directory.mkdir()
    (directory / 'report.json').write_text(json.dumps({'job_id': job_id, 'owner_pid': record['child']['pid'],
        'status': status, 'finished_at': '2026-11-07T00:00:00Z' if finished else None,
        'cleanup': {'completed': True}}), encoding='utf-8')
    with pytest.raises(AgentError) as failure:
        await asyncio.to_thread(restart.stop_for_restart, store.directory, timeout=0)
    assert failure.value.code == 'RESTART_NOT_IDLE'
    assert identity_alive(ProcessIdentity.model_validate(record['host'])) is True
    assert identity_alive(ProcessIdentity.model_validate(record['child'])) is True


async def test_restart_refuses_reused_identity(store, hung_owner):
    restart = implementation('restart')
    record = hung_owner(store.directory)
    path = store.directory / 'mcp-owner.json'
    marker = json.loads(path.read_text())
    marker['process']['creation_time'] = '2000-01-01T00:00:00Z'
    path.write_text(json.dumps(marker))
    with pytest.raises(AgentError) as failure:
        await asyncio.to_thread(restart.stop_for_restart, store.directory, timeout=0)
    assert failure.value.code == 'RESTART_OWNER_UNCERTAIN'
    assert identity_alive(ProcessIdentity.model_validate(record['host'])) is True
    assert identity_alive(ProcessIdentity.model_validate(record['child'])) is True


async def test_restart_refuses_invalid_owner_token(store, hung_owner):
    restart = implementation('restart')
    record = hung_owner(store.directory)
    path = store.directory / 'runtime.json'
    marker = json.loads(path.read_text())
    marker['token'] = ''
    path.write_text(json.dumps(marker))
    with pytest.raises(AgentError) as failure:
        await asyncio.to_thread(restart.stop_for_restart, store.directory, timeout=0)
    assert failure.value.code == 'RESTART_OWNER_UNCERTAIN'
    assert identity_alive(ProcessIdentity.model_validate(record['host'])) is True


async def test_restart_refuses_unrelated_child(store, hung_owner):
    restart = implementation('restart')
    record = hung_owner(store.directory)
    import shutil
    unrelated = subprocess.Popen([shutil.which('node'), '-e', 'setInterval(() => {}, 1000)'])
    try:
        from corel_agent.owned_stdio import process_identity
        path = store.directory / 'mcp-owner.json'
        marker = json.loads(path.read_text())
        marker['process'] = process_identity(unrelated.pid).model_dump()
        path.write_text(json.dumps(marker))
        with pytest.raises(AgentError) as failure:
            await asyncio.to_thread(restart.stop_for_restart, store.directory, timeout=0)
        assert failure.value.code == 'RESTART_OWNER_UNCERTAIN'
        assert unrelated.poll() is None
        assert identity_alive(ProcessIdentity.model_validate(record['host'])) is True
    finally:
        unrelated.terminate()
        unrelated.wait(timeout=5)


async def test_restart_refuses_changed_marker_before_termination(store, hung_owner, monkeypatch):
    restart = implementation('restart')
    record = hung_owner(store.directory)
    validate = restart._check_markers
    def changed(markers):
        path = store.directory / 'runtime.json'
        marker = json.loads(path.read_text())
        marker['token'] = new_id()
        path.write_text(json.dumps(marker))
        validate(markers)
    monkeypatch.setattr(restart, '_check_markers', changed)
    with pytest.raises(AgentError) as failure:
        await asyncio.to_thread(restart.stop_for_restart, store.directory, timeout=0)
    assert failure.value.code == 'RESTART_OWNER_UNCERTAIN'
    assert identity_alive(ProcessIdentity.model_validate(record['host'])) is True
    assert identity_alive(ProcessIdentity.model_validate(record['child'])) is True


def test_restart_without_owner_does_not_create_data(tmp_path):
    restart = implementation('restart')
    directory = tmp_path / 'absent'
    restart.stop_for_restart(directory, timeout=0)
    assert not directory.exists()


async def test_restart_graceful_stop_never_escalates_even_with_unfinished_run(store, hung_owner):
    restart = implementation('restart')
    accepted = await store.accept_run(command(), None)
    record = hung_owner(store.directory, '--honor-stop')
    await asyncio.to_thread(restart.stop_for_restart, store.directory, timeout=5)
    assert identity_alive(ProcessIdentity.model_validate(record['host'])) is False
    assert not (store.directory / 'runtime.json').exists()
    assert (await store.get_snapshot(accepted.snapshot.id)).status == 'preparing'


async def test_restart_blocks_new_dispatch_commits_until_owners_exit(store, hung_owner, monkeypatch):
    import sqlite3
    restart = implementation('restart')
    hung_owner(store.directory)
    validate = restart._check_markers
    checks = []
    def check(markers):
        db = sqlite3.connect(store.path, timeout=0)
        try:
            with pytest.raises(sqlite3.OperationalError, match='locked'):
                db.execute('BEGIN IMMEDIATE')
            checks.append(True)
        finally:
            db.close()
        validate(markers)
    monkeypatch.setattr(restart, '_check_markers', check)
    await asyncio.to_thread(restart.stop_for_restart, store.directory, timeout=0)
    assert checks


async def test_restart_refuses_safety_barrier(store, hung_owner):
    from corel_agent.contracts import SafetyBarrier
    restart = implementation('restart')
    record = hung_owner(store.directory)
    await store.set_barrier(SafetyBarrier(reason='COREL_OUTCOME_UNKNOWN'))
    with pytest.raises(AgentError) as failure:
        await asyncio.to_thread(restart.stop_for_restart, store.directory, timeout=0)
    assert failure.value.code == 'RESTART_NOT_IDLE'
    assert identity_alive(ProcessIdentity.model_validate(record['host'])) is True


async def test_restart_refuses_occupied_listener(store, hung_owner, monkeypatch):
    restart = implementation('restart')
    record = hung_owner(store.directory)
    def occupied():
        raise AgentError('PORT_BUSY', 'occupied')
    monkeypatch.setattr(restart, 'reserve_port', occupied)
    with pytest.raises(AgentError) as failure:
        await asyncio.to_thread(restart.stop_for_restart, store.directory, timeout=0)
    assert failure.value.code == 'PORT_BUSY'
    assert identity_alive(ProcessIdentity.model_validate(record['host'])) is True


def test_restart_image_check_refuses_current_process():
    from corel_agent.owned_stdio import process_identity
    restart = implementation('restart')
    with pytest.raises(AgentError) as failure:
        restart._pin_process(process_identity(os.getpid()), ['anything.exe'])
    assert failure.value.code == 'RESTART_OWNER_UNCERTAIN'
