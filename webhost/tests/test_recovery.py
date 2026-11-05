import pytest
from conftest import command, implementation

from corel_agent.contracts import (
    AgentError,
    ConfirmationRequest,
    ConfirmCommand,
    JobUpdate,
    PreparedOperation,
    new_id,
)


def operation(run):
    return PreparedOperation(operation_id=new_id(), run_id=run.id, tool="corel_execute_vba",
                             document_id="fixture", corel_request_id="web:fixture",
                             arguments={"document_id": "fixture", "code": "delay",
                                        "request_id": "web:fixture"}, digest="0" * 64)


async def test_crash_after_dispatch_no_replay(store):
    run = (await store.accept_run(command(read_only=False), None)).snapshot
    op = operation(run)
    await store.record_operation(op)
    await store.mark_dispatching(op.operation_id)
    assert await store.recover() == []
    snapshot = await store.get_snapshot(run.id)
    assert snapshot.status == "uncertain" and snapshot.safety_barrier
    assert (await store.get_operation(op.operation_id))["job_id"] is None
    with pytest.raises(AgentError) as caught:
        await store.accept_run(command(read_only=False), None)
    assert caught.value.code == "SAFETY_BARRIER"


async def test_known_job_recovery_only_returns_poll_binding(store):
    run = (await store.accept_run(command(read_only=False), None)).snapshot
    op = operation(run)
    await store.record_operation(op)
    await store.mark_dispatching(op.operation_id)
    await store.record_job(JobUpdate(operation_id=op.operation_id, job_id="known-job",
                                    status="running", stage="executing",
                                    report={"job_id": "known-job", "status": "running"}))
    recovered = await store.recover()
    assert len(recovered) == 1 and recovered[0]["job_id"] == "known-job"
    assert (await store.get_snapshot(run.id)).stop_requested


async def test_prepared_and_approval_are_interrupted_after_restart(store):
    run = (await store.accept_run(command(), None)).snapshot
    op = operation(run)
    await store.record_operation(op)
    pending = ConfirmationRequest(operation_digest=op.digest, operation=op, reasons=("VBA",))
    await store.await_confirmation(run.id, pending)
    await store.recover()
    snapshot = await store.get_snapshot(run.id)
    assert snapshot.status == "interrupted" and snapshot.pending_confirmation is None
    with pytest.raises(AgentError):
        await store.confirm(run.id, ConfirmCommand(confirmation_id=pending.confirmation_id,
                            operation_digest=pending.operation_digest, approved=True))


async def test_confirmation_is_an_atomic_one_shot_transition(store):
    import asyncio
    run = (await store.accept_run(command(), None)).snapshot
    op = operation(run)
    await store.record_operation(op)
    pending = ConfirmationRequest(operation_digest=op.digest, operation=op, reasons=("VBA",))
    await store.await_confirmation(run.id, pending)
    confirm = ConfirmCommand(confirmation_id=pending.confirmation_id,
                             operation_digest=pending.operation_digest, approved=True)
    results = await asyncio.gather(store.confirm(run.id, confirm), store.confirm(run.id, confirm),
                                   return_exceptions=True)
    assert sum(isinstance(item, AgentError) for item in results) == 1
    assert (await store.get_snapshot(run.id)).pending_confirmation is None


async def test_corrupt_store_is_not_replaced(tmp_path):
    path = tmp_path / "history.sqlite3"
    path.write_bytes(b"not a database - preserve this evidence")
    store = implementation("store").Store(tmp_path)
    with pytest.raises(AgentError) as caught:
        await store.open()
    assert caught.value.status == 503
    assert path.read_bytes() == b"not a database - preserve this evidence"


async def test_unknown_barrier_survives_reopen(store):
    run = (await store.accept_run(command(read_only=False), None)).snapshot
    op = operation(run)
    await store.record_operation(op)
    await store.mark_dispatching(op.operation_id)
    await store.recover()
    await store.aclose()
    await store.open()
    assert await store.get_barrier() is not None


async def test_missing_cleanup_after_terminal_report_sets_barrier(store):
    run = (await store.accept_run(command(read_only=False), None)).snapshot
    op = operation(run)
    await store.record_operation(op)
    await store.mark_dispatching(op.operation_id)
    from corel_agent.contracts import OperationResult
    await store.complete_operation(OperationResult(operation_id=op.operation_id, job_id="known-job",
        outcome="succeeded", report={"status": "succeeded"}, applied=True, cleanup=None))
    await store.recover()
    assert await store.get_barrier() is not None
    with pytest.raises(AgentError) as caught:
        await store.accept_run(command(read_only=False), None)
    assert caught.value.code == "SAFETY_BARRIER"


async def test_failed_applied_operation_without_cleanup_sets_barrier(store):
    run = (await store.accept_run(command(read_only=False), None)).snapshot
    op = operation(run)
    await store.record_operation(op)
    await store.mark_dispatching(op.operation_id)
    from corel_agent.contracts import OperationResult
    await store.complete_operation(OperationResult(operation_id=op.operation_id, job_id="known-job",
        outcome="failed", report={"status": "failed"}, applied=True, cleanup=None))
    await store.recover()
    assert await store.get_barrier() is not None


async def test_recovery_invalidates_old_worker_document_cache(store):
    await store.put_meta("documents", {"items": [{"document_id": "old-worker:1"}], "stale": False})
    await store.recover()
    assert (await store.get_meta("documents"))["stale"] is True


async def test_recovery_retains_actual_job_binding_from_emergency_sidecar(store):
    run = (await store.accept_run(command(read_only=False), None)).snapshot
    op = operation(run)
    await store.record_operation(op)
    await store.mark_dispatching(op.operation_id)
    await store.emergency(op.operation_id, JobUpdate(operation_id=op.operation_id, job_id="known-job",
        status="running", stage="executing", report={"job_id": "known-job", "status": "running"}).model_dump())
    recovered = await store.recover()
    assert recovered[0]["job_id"] == "known-job"
    assert (await store.get_operation(op.operation_id))["job_id"] == "known-job"
