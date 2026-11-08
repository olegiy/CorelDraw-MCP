import asyncio
import json
from datetime import UTC, datetime, timedelta

import pytest
from conftest import command, implementation, wait_run
from test_approvals import calls, prime
from test_recovery import operation

from corel_agent.contracts import (
    AgentError,
    JobUpdate,
    OperationResult,
    SafetyBarrier,
    new_id,
    utc_now,
)


async def drain(runtime):
    await asyncio.gather(*tuple(runtime._tasks.values()))
    for _ in range(3):
        pending = tuple(getattr(runtime, '_readiness_tasks', ()))
        if not pending:
            break
        await asyncio.gather(*pending)


async def fault(runtime, store, *, outcome='succeeded', cleanup=True, state='terminal'):
    await prime(runtime, store)
    await drain(runtime)
    run = (await store.accept_run(command(), None)).snapshot
    op = operation(run)
    await store.record_operation(op)
    job_id = None
    if state != 'prepared':
        await store.mark_dispatching(op.operation_id)
        job_id = new_id()
        report = {'job_id': job_id, 'tool': op.tool, 'request_id': op.corel_request_id,
                  'owner_pid': 123, 'status': outcome, 'finished_at': utc_now(),
                  'cleanup': {'completed': cleanup, 'errors': [], 'project_retained': False}}
        await store.record_job(JobUpdate(operation_id=op.operation_id, job_id=job_id,
            status='running', stage='executing', report={**report, 'status': 'running'}))
        if state == 'terminal':
            await store.complete_operation(OperationResult(operation_id=op.operation_id, job_id=job_id,
                outcome=outcome, applied=True, cleanup=report['cleanup'], report=report))
    barrier = SafetyBarrier(reason='RUNTIME_INTERRUPTED', run_id=run.id,
                            operation_id=op.operation_id, job_id=job_id)
    await store.set_barrier(barrier, status='halted')
    return barrier, run.id, op


@pytest.mark.parametrize('outcome', ['succeeded', 'failed'])
async def test_confirmed_terminal_refresh_allows_only_new_context_without_review(runtime, store, outcome):
    barrier, old_id, op = await fault(runtime, store, outcome=outcome)
    old = await store.get_snapshot(old_id)
    status = await runtime.start(command())
    await wait_run(store, status.id)
    await drain(runtime)
    assert await store.get_barrier() is None
    assert (await store.get_snapshot(old_id)).model_dump() == old.model_dump()
    assert (await store.get_operation(op.operation_id))['result']['outcome'] == outcome
    new = await runtime.start(command(tool='corel_execute_vba', document_id='fixture-b', arguments={'code': 'fixture'}))
    assert (await wait_run(store, new.id)).status == 'completed'
    assert len(calls(store, 'corel_execute_vba')) == 1
    assert await store._db(lambda db: db.execute('SELECT count(*) FROM reviews').fetchone()[0]) == 0


@pytest.mark.parametrize('case', ['unknown', 'cleanup', 'missing_cleanup', 'pending', 'stale', 'recovery_pending'])
async def test_unproven_effects_or_scope_never_gain_admission(runtime, store, case):
    barrier, _, op = await fault(runtime, store, outcome='unknown' if case == 'unknown' else 'succeeded',
                                 cleanup=False if case == 'cleanup' else True,
                                 state='known_job' if case == 'pending' else 'terminal')
    if case == 'missing_cleanup':
        def remove(db):
            result = json.loads(db.execute('SELECT result FROM operations WHERE id=?', (op.operation_id,)).fetchone()[0])
            result['cleanup'] = None
            db.execute('UPDATE operations SET result=? WHERE id=?', (json.dumps(result), op.operation_id))
        await store._db(remove, write=True)
    elif case == 'stale':
        cached = await store.get_meta('documents')
        cached['at'] = (datetime.now(UTC) - timedelta(minutes=5)).isoformat()
        await store.put_meta('documents', cached)
    elif case == 'recovery_pending':
        await store.put_meta('recovery_pending', [{'job_id': 'fixture'}])
    with pytest.raises(AgentError):
        await runtime.start(command(tool='corel_execute_vba', document_id='fixture-b', arguments={'code': 'fixture'}))
    assert (await store.get_barrier()).id == barrier.id and calls(store, 'corel_execute_vba') == []


async def test_old_review_without_global_barrier_is_not_executable_authority(runtime, store):
    _, _, _ = await fault(runtime, store, outcome='unknown')
    await store.put_meta('safety_barrier', None)
    with pytest.raises(AgentError):
        await runtime.start(command(tool='corel_execute_vba', document_id='fixture-b', arguments={'code': 'fixture'}))
    assert calls(store, 'corel_execute_vba') == []


@pytest.mark.parametrize('effects', ['empty', 'prepared'])
async def test_empty_and_never_dispatched_fault_can_be_cleared(runtime, store, effects):
    if effects == 'prepared':
        _, old_id, _ = await fault(runtime, store, state='prepared')
    else:
        await prime(runtime, store)
        await drain(runtime)
        old_id = (await store.accept_run(command(), None)).snapshot.id
        await store.set_barrier(SafetyBarrier(reason='RUNTIME_INTERRUPTED', run_id=old_id), status='interrupted')
    old_status = (await store.get_snapshot(old_id)).status
    new = await runtime.start(command(tool='corel_execute_vba', document_id='fixture-b', arguments={'code': 'fixture'}))
    assert (await wait_run(store, new.id)).status == 'completed'
    assert await store.get_barrier() is None and (await store.get_snapshot(old_id)).status == old_status


async def test_refresh_task_must_finish_before_readiness_release(runtime, store, monkeypatch):
    barrier, _, _ = await fault(runtime, store)
    manual = runtime._manual
    completed, release = asyncio.Event(), asyncio.Event()
    async def paused(*args):
        await manual(*args)
        completed.set()
        await release.wait()
    monkeypatch.setattr(runtime, '_manual', paused)
    await runtime.start(command())
    await completed.wait()
    try:
        async with runtime._lock:
            assert await runtime._refresh_readiness_locked() is False
        assert (await store.get_barrier()).id == barrier.id
    finally:
        release.set()
        await drain(runtime)
    assert await store.get_barrier() is None


async def test_readiness_never_clears_a_replaced_barrier(runtime, store, monkeypatch):
    old, _, _ = await fault(runtime, store)
    replacement = SafetyBarrier(reason='REPLACED_FAULT')
    verify = runtime.mcp.transport.verify_owner
    async def replace():
        await verify()
        await store.set_barrier(replacement)
    monkeypatch.setattr(runtime.mcp.transport, 'verify_owner', replace)
    async with runtime._lock:
        assert await runtime._refresh_readiness_locked() is False
    assert (await store.get_barrier()).id == replacement.id != old.id


@pytest.mark.parametrize('alive', [False, None])
async def test_reused_or_unknown_owner_identity_never_clears_fault(runtime, store, monkeypatch, alive):
    barrier, _, _ = await fault(runtime, store)
    with monkeypatch.context() as scoped:
        scoped.setattr(implementation('owned_stdio'), 'identity_alive', lambda identity: alive)
        async with runtime._lock:
            assert await runtime._refresh_readiness_locked() is False
    assert (await store.get_barrier()).id == barrier.id


async def test_changed_marker_during_verification_does_not_clear_fault(runtime, store, monkeypatch):
    barrier, _, _ = await fault(runtime, store)
    marker = runtime.mcp.transport.marker.path
    original = marker.read_bytes()
    verify = runtime.mcp.transport.verify_owner
    count = 0
    async def change():
        nonlocal count
        await verify()
        count += 1
        if count == 2:
            data = json.loads(original)
            data['token'] = new_id()
            marker.write_text(json.dumps(data))
    monkeypatch.setattr(runtime.mcp.transport, 'verify_owner', change)
    try:
        async with runtime._lock:
            assert await runtime._refresh_readiness_locked() is False
        assert (await store.get_barrier()).id == barrier.id
    finally:
        marker.write_bytes(original)


async def test_readiness_helper_requires_held_runtime_lock(runtime):
    with pytest.raises(RuntimeError):
        await runtime._refresh_readiness_locked()


async def test_prepared_row_with_recorded_dispatch_is_not_never_sent_evidence(runtime, store):
    _, old_id, op = await fault(runtime, store)
    await store._db(lambda db: db.execute(
        "UPDATE operations SET state='prepared',job_id=NULL,result=NULL WHERE id=?", (op.operation_id,)), write=True)
    barrier = SafetyBarrier(reason='DISPATCH_OUTCOME_UNKNOWN', run_id=old_id, operation_id=op.operation_id)
    await store.set_barrier(barrier, status='halted')
    async with runtime._lock:
        assert await runtime._refresh_readiness_locked() is False
    assert (await store.get_barrier()).id == barrier.id
