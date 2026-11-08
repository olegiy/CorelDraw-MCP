import socket

import pytest
from conftest import implementation

from corel_agent.contracts import AgentError


def test_busy_port_fails_before_mcp_or_owner_spawn(tmp_path):
    launcher = implementation("launcher")
    listener = socket.socket()
    listener.bind(('127.0.0.1', 0))
    listener.listen(1)
    try:
        with pytest.raises(AgentError) as caught:
            launcher.reserve_port(listener.getsockname()[1])
        assert caught.value.code == 'PORT_BUSY'
        assert not (tmp_path / 'runtime.json').exists()
    finally:
        listener.close()


def test_singleton_reservation_and_release_require_exact_identity(tmp_path):
    launcher = implementation('launcher')
    owner = launcher.HostOwner(tmp_path)
    owner.reserve()
    with pytest.raises(AgentError):
        launcher.HostOwner(tmp_path).reserve()
    owner.release()
    assert not (tmp_path / 'runtime.json').exists()


def test_unknown_previous_owner_is_not_automatically_removed(tmp_path):
    launcher = implementation('launcher')
    path = tmp_path / 'runtime.json'
    path.write_text('{"phase":"spawn_pending"}', encoding='utf-8')
    with pytest.raises(AgentError):
        launcher.HostOwner(tmp_path).reserve()
    assert path.read_text() == '{"phase":"spawn_pending"}'


def test_elevated_start_is_refused_before_files_created(tmp_path, monkeypatch):
    launcher = implementation('launcher')
    monkeypatch.setattr(launcher, 'is_elevated', lambda: True)
    with pytest.raises(AgentError) as caught:
        launcher.preflight(tmp_path)
    assert caught.value.code == 'ELEVATION_DENIED'
    assert not (tmp_path / 'runtime.json').exists()


def test_named_control_events_are_owned_and_signal_without_pid_kill(tmp_path):
    launcher = implementation('launcher')
    events = launcher.ControlEvents(tmp_path)
    try:
        assert events.take_open() is False and events.take_stop() is False
        events.request_open()
        assert events.take_open() is True and events.take_open() is False
        events.request_stop()
        assert events.take_stop() is True
    finally:
        events.close()


def test_owner_identity_is_verified_before_control_event(tmp_path):
    launcher = implementation('launcher')
    with pytest.raises(AgentError):
        launcher.signal_existing(tmp_path, 'stop')
    owner = launcher.HostOwner(tmp_path)
    owner.reserve()
    events = launcher.ControlEvents(tmp_path)
    try:
        launcher.signal_existing(tmp_path, 'open')
        assert events.take_open() is True
    finally:
        events.close()
        owner.release()


def test_entrypoint_has_no_startup_side_effect_on_import(tmp_path, monkeypatch):
    import importlib
    monkeypatch.setenv('LOCALAPPDATA', str(tmp_path))
    module = importlib.import_module('corel_agent.__main__')
    assert callable(module.main)
    assert not (tmp_path / 'CorelDrawAgent').exists()


async def test_no_browser_does_not_accept_unverified_existing_owner(tmp_path, monkeypatch):
    module = implementation('__main__')
    monkeypatch.setattr(module, 'preflight', lambda directory: None)
    (tmp_path / 'runtime.json').write_text('{"phase":"spawn_pending"}')
    with pytest.raises(AgentError):
        await module.serve(tmp_path, no_browser=True)


def test_identity_mismatch_is_not_proof_that_previous_process_exited(tmp_path, monkeypatch):
    module = implementation('owned_stdio')
    from corel_agent.contracts import ProcessIdentity
    expected = ProcessIdentity(pid=123, creation_time='original')
    monkeypatch.setattr(module, 'process_identity', lambda pid: ProcessIdentity(pid=pid, creation_time='different'))
    assert module.identity_alive(expected) is None


async def test_startup_known_job_creates_recovery_only_runtime_without_second_mcp(store, monkeypatch):
    from conftest import command
    from test_recovery import operation

    from corel_agent.contracts import JobUpdate
    module = implementation('__main__')
    run = (await store.accept_run(command(), None)).snapshot
    op = operation(run)
    await store.record_operation(op)
    await store.mark_dispatching(op.operation_id)
    await store.record_job(JobUpdate(operation_id=op.operation_id, job_id='known-job',
        status='running', stage='executing', report={'job_id': 'known-job', 'status': 'running'}))
    class NoSpawn:
        ready = False
        catalog = ()
        async def start(self):
            raise AssertionError('Second MCP must never spawn')
    runtime = await module.prepare_runtime(store, NoSpawn(), store.directory)
    assert runtime.mcp.ready is False
    assert (await store.get_snapshot(run.id)).job_id == 'known-job'
    assert await store.get_meta('recovery_pending')
    with pytest.raises(AgentError):
        await runtime.start(command())


def test_dead_host_marker_is_released_only_after_exact_double_check(tmp_path, monkeypatch):
    import json
    module = implementation('launcher')
    marker = tmp_path / 'runtime.json'
    marker.write_text(json.dumps({'phase': 'running', 'process': {'pid': 123, 'creation_time': 'test'}}))
    monkeypatch.setattr(module, 'identity_alive', lambda identity: False)
    assert module.release_dead_host(tmp_path) is True
    assert not marker.exists()


def test_unknown_host_marker_never_released(tmp_path, monkeypatch):
    import json
    module = implementation('launcher')
    marker = tmp_path / 'runtime.json'
    original = json.dumps({'phase': 'running', 'process': {'pid': 123, 'creation_time': 'test'}})
    marker.write_text(original)
    monkeypatch.setattr(module, 'identity_alive', lambda identity: None)
    assert module.release_dead_host(tmp_path) is False
    assert marker.read_text() == original


async def test_clean_start_clears_recovery_only_without_resuming_old_plan(store):
    module = implementation('__main__')
    await store.put_meta('recovery_only', True)
    class FreshMcp:
        ready = False
        catalog = ()
        async def start(self):
            self.ready = True
    mcp = FreshMcp()
    await module.prepare_runtime(store, mcp, store.directory)
    assert mcp.ready is True
    assert await store.get_meta('recovery_only') is False


def test_entrypoint_sanitizes_unexpected_startup_failure(tmp_path, monkeypatch, capsys):
    module = implementation('__main__')
    monkeypatch.setenv('LOCALAPPDATA', str(tmp_path))
    monkeypatch.setattr(module.sys, 'argv', ['corel_agent', '--no-browser'])

    async def failing_start(*args, **kwargs):
        raise OSError('CANARY-credential C:\\private\\drawing.cdr')

    monkeypatch.setattr(module, 'serve', failing_start)
    assert module.main() == 1
    captured = capsys.readouterr()
    assert captured.out == ''
    assert 'HOST_START_FAILED' in captured.err
    assert 'CANARY' not in captured.err and 'drawing.cdr' not in captured.err


async def test_exited_process_with_retained_windows_handle_is_not_a_live_owner(tmp_path):
    import asyncio
    import json
    import sys

    from corel_agent.contracts import ProcessIdentity
    from corel_agent.owned_stdio import identity_alive

    child = await asyncio.create_subprocess_exec(sys.executable, '-c',
        'import sys; from pathlib import Path; from corel_agent.launcher import HostOwner; '
        'HostOwner(Path(sys.argv[1])).reserve()', str(tmp_path))
    assert await child.wait() == 0
    marker = json.loads((tmp_path / 'runtime.json').read_text())
    identity = ProcessIdentity.model_validate(marker['process'])
    assert identity_alive(identity) is False


def test_concurrent_dead_marker_release_cannot_delete_new_host_owner(tmp_path, monkeypatch):
    import json
    import threading
    from concurrent.futures import ThreadPoolExecutor

    launcher = implementation('launcher')
    marker = tmp_path / 'runtime.json'
    marker.write_text(json.dumps({'phase': 'running', 'process': {'pid': 123, 'creation_time': 'dead'}}))
    checking, proceed = threading.Event(), threading.Event()
    calls = threading.local()

    def dead(identity):
        if threading.current_thread().name.endswith('_0'):
            calls.count = getattr(calls, 'count', 0) + 1
            if calls.count == 2:
                checking.set()
                assert proceed.wait(5)
        return False

    monkeypatch.setattr(launcher, 'identity_alive', dead)
    owner = launcher.HostOwner(tmp_path)

    def replacement():
        released = launcher.release_dead_host(tmp_path)
        owner.reserve()
        return released

    with ThreadPoolExecutor(max_workers=2) as pool:
        first = pool.submit(launcher.release_dead_host, tmp_path)
        try:
            assert checking.wait(5)
            second = pool.submit(replacement)
            # Let the other launcher reach reserve while the first checks exit.
            from concurrent.futures import TimeoutError
            try:
                second.result(timeout=0.2)
            except TimeoutError:
                pass
        finally:
            proceed.set()
        assert sorted([first.result(timeout=5), second.result(timeout=5)]) == [False, True]
    try:
        assert marker.exists()
        assert json.loads(marker.read_text())['token'] == owner.token
    finally:
        if marker.exists():
            owner.release()


def test_missing_control_event_has_fixed_owner_diagnostic(tmp_path):
    launcher = implementation('launcher')
    owner = launcher.HostOwner(tmp_path)
    owner.reserve()
    try:
        with pytest.raises(AgentError) as caught:
            launcher.signal_existing(tmp_path, 'stop')
        assert caught.value.code == 'HOST_OWNER_UNCERTAIN'
    finally:
        owner.release()


async def test_busy_production_port_refuses_launcher_before_mcp(tmp_path, monkeypatch):
    module = implementation('__main__')
    launcher = implementation('launcher')
    try:
        listener = launcher.reserve_port()
    except AgentError as error:
        assert error.code == 'PORT_BUSY'
        pytest.skip('Порт 8878 уже занят; существующий сервис не затрагивается.')

    def no_mcp(**kwargs):
        raise AssertionError('MCP must not be constructed before port reservation')

    monkeypatch.setattr(module, 'CorelMcp', no_mcp)
    directory = tmp_path / 'blocked runtime'
    try:
        with pytest.raises(AgentError) as caught:
            await module.serve(directory, no_browser=True)
        assert caught.value.code == 'PORT_BUSY'
        assert not (directory / 'runtime.json').exists()
        assert not (directory / 'mcp-owner.json').exists()
        assert not (directory / 'history.sqlite3').exists()
    finally:
        listener.close()
