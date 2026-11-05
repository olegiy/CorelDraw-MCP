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
