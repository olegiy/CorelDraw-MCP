import json

import pytest
from conftest import command, implementation
from test_recovery import operation

from corel_agent.contracts import AgentError, JobUpdate, ProcessIdentity


async def test_verified_dead_owner_recovers_terminal_report_without_dispatch(store, tmp_path, monkeypatch):
    module = implementation('recovery')
    run = (await store.accept_run(command(read_only=False), None)).snapshot
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


async def test_recovery_restart_refresh_and_review_allow_only_new_context(store, tmp_path, monkeypatch):
    import shutil
    from pathlib import Path

    import httpx
    from conftest import wait_run
    from test_manual_api import HarnessAccess

    run = (await store.accept_run(command(read_only=False), None)).snapshot
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
        'request_id': op.corel_request_id, 'owner_pid': 123, 'status': 'unknown',
        'cleanup': {'completed': False}, 'error': {'applied': True}}))
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
        barrier = await store.get_barrier()
        assert barrier is not None
        app = implementation('app').create_app(runtime=runtime, store=store, assets=runtime.assets, access=HarnessAccess())
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url='http://127.0.0.1:8878') as client:
            response = await client.post('/api/v1/recovery/review', json={'note': 'Новый worker, рисунок и отчёт проверены',
                'barrier_id': barrier.id, 'document_id': 'fixture-b', 'manual_corel_checked': True,
                'release_for_new_context': True})
            assert response.status_code == 201
        assert (await store.get_snapshot(run.id)).status == 'uncertain'
        assert (await store.get_operation(op.operation_id))['result']['outcome'] == 'unknown'
        assert await store.get_barrier() is None
        new = await runtime.start(command(tool='corel_inspect_document', document_id='fixture-b', read_only=False))
        assert (await wait_run(store, new.id)).status == 'completed'
    finally:
        await runtime.aclose()
