import json
from datetime import UTC, datetime, timedelta

import pytest
from conftest import command, drawing_arguments, wait_run
from test_approvals import calls
from test_readiness import drain, fault
from test_recovery import operation

from corel_agent.contracts import AgentError, OperationResult, new_id

NAME = 'CorelMcp_a1b2c3d4e5f6'
JOB = '00000000-0000-4000-8000-000000000001'


async def retained(runtime, store, *, outcome='failed', completed=True, errors=None, applied=None):
    barrier, run_id, operation = await fault(runtime, store)
    # Restore an actual historical v2 binding directly in the fixture history; never dispatch it.
    from corel_agent.policy import Policy
    operation = operation.model_copy(update={'tool': 'corel_execute_vba', 'policy_version': 2,
        'arguments': {'document_id': operation.document_id, 'request_id': operation.corel_request_id,
                      'code': 'historical retained diagnostic'}})
    operation = operation.model_copy(update={'digest': Policy.operation_digest(operation)})
    await store._db(lambda db: db.execute('UPDATE operations SET prepared=? WHERE id=?',
        (operation.model_dump_json(), operation.operation_id)), write=True)
    report = {'job_id': JOB, 'tool': operation.tool, 'request_id': operation.corel_request_id,
        'status': outcome, 'owner_pid': 123,
        'finished_at': (datetime.now(UTC) - timedelta(minutes=1)).isoformat(),
        'run_directory': f'C:\\runs\\{JOB}',
        'artifacts': [f'C:\\runs\\{JOB}\\{NAME}.gms'],
        'cleanup': {'completed': completed, 'errors': errors or [],
                    'project_name': NAME, 'project_retained': True}}
    if applied is not None:
        report['error'] = {'applied': applied}
    await store.complete_operation(OperationResult(operation_id=operation.operation_id, job_id=JOB,
        outcome=outcome, applied=applied, report=report, cleanup=report['cleanup']))
    barrier = barrier.model_copy(update={'job_id': JOB})
    await store.set_barrier(barrier, status='halted')
    return barrier, run_id, operation


async def audits(store):
    return await store._db(lambda db: [json.loads(row[0]) for row in db.execute(
        "SELECT value FROM meta WHERE key LIKE 'cleanup:%'")])


@pytest.mark.parametrize('outcome', ['failed', 'succeeded'])
async def test_refresh_proves_unloaded_retained_gms_without_rewriting_or_replaying(runtime, store, outcome):
    _, old_id, op = await retained(runtime, store, outcome=outcome)
    before = await store.get_operation(op.operation_id)
    old = await store.get_snapshot(old_id)
    refresh = await runtime.start(command())
    assert (await wait_run(store, refresh.id)).status == 'completed'
    await drain(runtime)
    proof = await store.readiness_proof()
    assert proof['reason'] is None and await store.get_barrier() is None
    assert await store.get_operation(op.operation_id) == before
    assert (await store.get_snapshot(old_id)).model_dump() == old.model_dump()
    audit = (await audits(store))[0]
    assert audit['operation_id'] == op.operation_id and audit['project_name'] == NAME
    assert audit['owner']['pid'] == runtime.mcp.transport.identity.pid
    assert audit['observation_id'] in [item['id'] for item in await store.get_operations(refresh.id)]
    assert any(event.type == 'warning' and event.data.code == 'RETAINED_GMS_ABSENT'
               for event in await store.get_events(refresh.id))
    assert calls(store, 'corel_edit_text') == []
    new = await runtime.start(command(tool='corel_edit_text', document_id='fixture-b', arguments=drawing_arguments('fixture')))
    assert (await wait_run(store, new.id)).status == 'completed'
    assert len(calls(store, 'corel_edit_text')) == 1
    # Durable and idempotent: a new status does not rewrite the original proof.
    original = await audits(store)
    await store.aclose()
    await store.open()
    assert (await store.readiness_proof())['reason'] is None
    second = await runtime.start(command())
    await wait_run(store, second.id)
    await drain(runtime)
    assert await audits(store) == original


@pytest.mark.parametrize('runtime', [(flag,) for flag in (
    '--inventory-loaded', '--inventory-renamed', '--inventory-path-alias', '--inventory-vbe', '--inventory-busy',
    '--inventory-incomplete', '--inventory-malformed', '--inventory-omitted')], indirect=True)
async def test_loaded_renamed_busy_or_unverifiable_project_cannot_be_confirmed(runtime, store):
    await retained(runtime, store)
    refresh = await runtime.start(command())
    await wait_run(store, refresh.id)
    await drain(runtime)
    assert await audits(store) == []
    assert (await store.readiness_proof())['reason'] == 'CLEANUP_NOT_PROVEN'
    with pytest.raises(AgentError):
        await runtime.start(command(tool='corel_edit_text', document_id='fixture-b', arguments=drawing_arguments('fixture')))
    assert calls(store, 'corel_edit_text') == []


@pytest.mark.parametrize('case', ['unknown', 'incomplete', 'restore_error', 'foreign_name', 'missing_artifact',
                                 'bad_digest', 'bad_binding', 'future_result', 'invalid_time'])
async def test_absence_does_not_override_unknown_cleanup_errors_or_unbound_reports(runtime, store, case):
    _, _, op = await retained(runtime, store, outcome='unknown' if case == 'unknown' else 'failed',
        completed=case != 'incomplete', errors=[{'step': 'restore', 'code': 'FAILED'}] if case == 'restore_error' else [])
    def change(db):
        row = db.execute('SELECT prepared,result FROM operations WHERE id=?', (op.operation_id,)).fetchone()
        prepared, result = json.loads(row[0]), json.loads(row[1])
        if case == 'foreign_name':
            result['cleanup']['project_name'] = 'UserGMS_keep'
            result['report']['cleanup']['project_name'] = 'UserGMS_keep'
        elif case == 'missing_artifact':
            result['report']['artifacts'] = []
        elif case == 'bad_digest':
            prepared['digest'] = '0' * 64
        elif case == 'bad_binding':
            result['report']['request_id'] = 'other'
        elif case == 'future_result':
            result['report']['finished_at'] = (datetime.now(UTC) + timedelta(days=1)).isoformat()
        elif case == 'invalid_time':
            result['report']['finished_at'] = 123
        db.execute('UPDATE operations SET prepared=?,result=?,report=? WHERE id=?',
            (json.dumps(prepared), json.dumps(result), json.dumps(result['report']), op.operation_id))
    await store._db(change, write=True)
    refresh = await runtime.start(command())
    await wait_run(store, refresh.id)
    await drain(runtime)
    assert await audits(store) == []
    assert (await store.readiness_proof())['reason'] is not None
    with pytest.raises(AgentError):
        await runtime.start(command(tool='corel_edit_text', document_id='fixture-b', arguments=drawing_arguments('fixture')))


@pytest.mark.parametrize('changed', ['retained', 'observation'])
async def test_confirmation_is_invalidated_if_either_original_binding_changes(runtime, store, changed):
    _, _, op = await retained(runtime, store)
    refresh = await runtime.start(command())
    await wait_run(store, refresh.id)
    await drain(runtime)
    audit = (await audits(store))[0]
    operation_id = op.operation_id if changed == 'retained' else audit['observation_id']
    def corrupt(db):
        result = json.loads(db.execute('SELECT result FROM operations WHERE id=?', (operation_id,)).fetchone()[0])
        result['report']['finished_at'] = '2020-01-01T00:00:00+00:00'
        db.execute('UPDATE operations SET result=?,report=? WHERE id=?',
                   (json.dumps(result), json.dumps(result['report']), operation_id))
    await store._db(corrupt, write=True)
    assert (await store.readiness_proof())['reason'] == 'CLEANUP_NOT_PROVEN'


@pytest.mark.parametrize('case', ['owner_changed', 'stale', 'wrong_pid'])
async def test_confirmation_requires_fresh_observation_and_pinned_current_owner(runtime, store, monkeypatch, case):
    await retained(runtime, store)
    # Suppress automatic recording so the evidence check can be exercised at its boundary.
    monkeypatch.setattr(runtime, '_confirm_retained_cleanup', lambda _: async_noop())
    refresh = await runtime.start(command())
    await wait_run(store, refresh.id)
    await drain(runtime)
    observed = (await store.get_operations(refresh.id))[0]
    if case in {'stale', 'wrong_pid'}:
        def change(db):
            row = db.execute('SELECT result FROM operations WHERE id=?', (observed['id'],)).fetchone()
            result = json.loads(row[0])
            if case == 'stale':
                result['report']['started_at'] = '2020-01-01T00:00:00+00:00'
                result['report']['finished_at'] = '2020-01-01T00:00:01+00:00'
            else:
                result['report']['owner_pid'] = 999999
            db.execute('UPDATE operations SET result=?,report=? WHERE id=?',
                (json.dumps(result), json.dumps(result['report']), observed['id']))
        await store._db(change, write=True)
    assert await store.confirm_retained_cleanup(observed['id'], runtime.mcp.transport.identity,
                                               lambda: case != 'owner_changed') == 0
    assert await audits(store) == []
    assert (await store.readiness_proof())['reason'] == 'CLEANUP_NOT_PROVEN'


async def async_noop():
    pass


async def test_owner_change_rolls_back_confirmation_and_audit_event(runtime, store, monkeypatch):
    await retained(runtime, store)
    monkeypatch.setattr(runtime, '_confirm_retained_cleanup', lambda _: async_noop())
    refresh = await runtime.start(command())
    await wait_run(store, refresh.id)
    await drain(runtime)
    observed = (await store.get_operations(refresh.id))[0]
    checks = iter([True, False])
    with pytest.raises(AgentError, match='Владелец изменился'):
        await store.confirm_retained_cleanup(observed['id'], runtime.mcp.transport.identity,
                                             lambda: next(checks))
    assert await audits(store) == []
    assert not any(event.type == 'warning' and event.data.code == 'RETAINED_GMS_ABSENT'
                   for event in await store.get_events(refresh.id))
    assert not store.failed


async def test_resolved_retention_does_not_release_another_unknown_effect(runtime, store):
    await retained(runtime, store)
    # A separate uncertain operation remains an independent reason to block changes.
    await store._db(lambda db: db.execute("DELETE FROM meta WHERE key='safety_barrier'"), write=True)
    old = (await store.list_runs())[0]
    run = (await store.accept_run(command(), None)).snapshot
    op = operation(run)
    await store.record_operation(op)
    await store.mark_dispatching(op.operation_id)
    job = new_id()
    report = {'job_id': job, 'tool': op.tool, 'request_id': op.corel_request_id,
              'status': 'unknown', 'cleanup': {'completed': True, 'errors': []}}
    await store.complete_operation(OperationResult(operation_id=op.operation_id, job_id=job,
        outcome='unknown', report=report, cleanup=report['cleanup']))
    refresh = await runtime.start(command())
    await wait_run(store, refresh.id)
    await drain(runtime)
    assert len(await audits(store)) == 1
    assert (await store.readiness_proof())['reason'] == 'OUTCOME_UNKNOWN'
    assert (await store.get_snapshot(old.id)).status == 'halted'
    with pytest.raises(AgentError):
        await runtime.start(command(tool='corel_edit_text', document_id='fixture-b', arguments=drawing_arguments('fixture')))
    assert calls(store, 'corel_edit_text') == []
