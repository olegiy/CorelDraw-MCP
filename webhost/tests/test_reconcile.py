import json

import pytest
from conftest import command, implementation
from test_recovery import operation

from corel_agent.contracts import AgentError, JobUpdate, ProcessIdentity


async def test_verified_dead_owner_recovers_terminal_report_without_dispatch(store, tmp_path, monkeypatch):
    module = implementation('recovery')
    run = (await store.accept_run(command(), None)).snapshot
    op = operation(run)
    await store.record_operation(op)
    await store.mark_dispatching(op.operation_id)
    job_id = 'req-' + 'a' * 64
    await store.record_job(JobUpdate(operation_id=op.operation_id, job_id=job_id,
        status='running', stage='executing', report={'job_id': job_id, 'status': 'running'}))
    pending = await store.recover()
    reports = tmp_path / 'corel-runs'
    job = reports / job_id
    job.mkdir(parents=True)
    report = {'job_id': job_id, 'tool': op.tool, 'request_id': op.corel_request_id,
              'status': 'succeeded', 'cleanup': {'completed': True, 'errors': []},
              'result': {'applied': True}, 'owner_pid': 123}
    (job / 'report.json').write_text(json.dumps(report))
    identity = ProcessIdentity(pid=123, creation_time='verified-test-identity')
    (tmp_path / 'mcp-owner.json').write_text(json.dumps({'phase': 'running', 'process': identity.model_dump()}))
    monkeypatch.setattr(module, 'identity_alive', lambda identity: False)
    assert await module.reconcile(store, pending, tmp_path, reports) is True
    assert (await store.get_snapshot(run.id)).status == 'interrupted'
    assert (await store.get_operation(op.operation_id))['result']['outcome'] == 'succeeded'
    assert not (tmp_path / 'mcp-owner.json').exists()


async def test_unverified_owner_never_releases_marker(store, tmp_path, monkeypatch):
    module = implementation('recovery')
    marker = tmp_path / 'mcp-owner.json'
    marker.write_text('{"phase":"spawn_pending"}')
    monkeypatch.setattr(module, 'identity_alive', lambda identity: False)
    with pytest.raises(AgentError):
        await module.reconcile(store, [], tmp_path, tmp_path / 'reports')
    assert marker.exists()


@pytest.mark.parametrize('outcome', ['succeeded', 'unknown'])
async def test_recovery_restart_refresh_allows_only_proven_new_context(store, tmp_path, monkeypatch, outcome):
    import shutil
    from pathlib import Path

    from conftest import wait_run
    from test_readiness import drain

    run = (await store.accept_run(command(), None)).snapshot
    op = operation(run)
    await store.record_operation(op)
    await store.mark_dispatching(op.operation_id)
    job_id = 'req-' + 'b' * 64
    await store.record_job(JobUpdate(operation_id=op.operation_id, job_id=job_id,
        status='running', stage='executing', report={'job_id': job_id, 'status': 'running'}))
    pending = await store.recover()
    reports = tmp_path / 'old-corel-runs'
    report_dir = reports / job_id
    report_dir.mkdir(parents=True)
    (report_dir / 'report.json').write_text(json.dumps({'job_id': job_id, 'tool': op.tool,
        'request_id': op.corel_request_id, 'owner_pid': 123, 'status': outcome,
        'cleanup': {'completed': outcome == 'succeeded'}, 'error': {'applied': True}}))
    (tmp_path / 'mcp-owner.json').write_text(json.dumps({'phase': 'running',
        'process': {'pid': 123, 'creation_time': 'old-owner'}}))
    recovery = implementation('recovery')
    monkeypatch.setattr(recovery, 'identity_alive', lambda identity: False)
    await recovery.reconcile(store, pending, tmp_path, reports)
    await store.put_meta('recovery_only', True)
    transport = implementation('owned_stdio').OwnedStdio(command=shutil.which('node'),
        args=[str(Path(__file__).parent / 'fixtures/panel-server.mjs')], directory=tmp_path,
        env={'CORELDRAW_MCP_RUNS': str(tmp_path / 'runs')})
    mcp = implementation('corel_mcp').CorelMcp(transport=transport, poll_interval=0.02)
    runtime = await implementation('__main__').prepare_runtime(store, mcp, tmp_path)
    try:
        assert await store.get_meta('recovery_only') is False
        status = await runtime.start(command())
        await wait_run(store, status.id)
        await drain(runtime)
        expected = 'uncertain' if outcome == 'unknown' else 'interrupted'
        assert (await store.get_snapshot(run.id)).status == expected
        assert (await store.get_operation(op.operation_id))['result']['outcome'] == outcome
        new_command = command(tool='corel_execute_vba', document_id='fixture-b', arguments={'code': 'fixture'})
        if outcome == 'unknown':
            assert await store.get_barrier() is not None
            with pytest.raises(AgentError):
                await runtime.start(new_command)
        else:
            assert await store.get_barrier() is None
            new = await runtime.start(new_command)
            assert (await wait_run(store, new.id)).status == 'completed'
        assert (await store.get_snapshot(run.id)).status == expected
    finally:
        await runtime.aclose()
