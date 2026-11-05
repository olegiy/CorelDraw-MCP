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
    run = (await store.accept_run(command(read_only=False), None)).snapshot
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
        await runtime.start(command(read_only=False))
