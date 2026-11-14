import hashlib
import json

import pytest
from conftest import command
from test_recovery import operation

from corel_agent.contracts import OperationResult, new_id


async def history(store, *, outcome='succeeded', cleanup=True, applied=True):
    run = (await store.accept_run(command(), None)).snapshot
    op = operation(run)
    await store.record_operation(op)
    await store.mark_dispatching(op.operation_id)
    job_id = new_id()
    report = {'job_id': job_id, 'tool': op.tool, 'request_id': op.corel_request_id,
              'status': outcome, 'cleanup': {'completed': cleanup, 'errors': []}}
    if outcome == 'failed':
        report['error'] = {'applied': applied}
    await store.complete_operation(OperationResult(operation_id=op.operation_id, job_id=job_id,
        outcome=outcome, applied=applied, cleanup=report['cleanup'], report=report))
    await store.set_status(run.id, 'completed' if outcome == 'succeeded' else 'uncertain')
    await store.aclose()
    return store.path


async def test_preupgrade_history_is_read_only_and_accepts_only_confirmed_terminal_evidence(store):
    from corel_agent.preupgrade import inspect_history
    path = await history(store)
    before = hashlib.sha256(path.read_bytes()).hexdigest()
    files = sorted(item.name for item in path.parent.iterdir())
    result = inspect_history(path)
    assert result['ok'] is True and result['operations'] == 1
    assert hashlib.sha256(path.read_bytes()).hexdigest() == before
    assert sorted(item.name for item in path.parent.iterdir()) == files


@pytest.mark.parametrize('applied', [None, True, False])
async def test_preupgrade_failed_history_requires_proven_unapplied_outcome(store, applied):
    from corel_agent.preupgrade import inspect_history
    path = await history(store, outcome='failed', applied=applied)
    before = path.read_bytes()
    result = inspect_history(path)
    assert result['ok'] is (applied is False)
    assert path.read_bytes() == before


@pytest.mark.parametrize('outcome,cleanup', [('unknown', True), ('succeeded', False)])
async def test_preupgrade_history_cannot_release_unknown_or_cleanup_failure(store, outcome, cleanup):
    from corel_agent.preupgrade import inspect_history
    path = await history(store, outcome=outcome, cleanup=cleanup)
    assert inspect_history(path)['ok'] is False


async def test_preupgrade_rejects_uncheckpointed_wal_and_preserves_it(store):
    from corel_agent.preupgrade import inspect_history
    path = await history(store)
    wal = path.with_name(path.name + '-wal')
    wal.write_bytes(b'uncheckpointed evidence')
    assert inspect_history(path)['reason'] == 'HISTORY_NOT_CHECKPOINTED'
    assert wal.read_bytes() == b'uncheckpointed evidence'


async def test_preupgrade_keeps_legacy_prepared_operations_as_evidence(store):
    from corel_agent.preupgrade import inspect_history
    run = (await store.accept_run(command(), None)).snapshot
    op = operation(run).model_copy(update={'tool': 'corel_execute_vba', 'policy_version': 2})
    await store.record_operation(op)
    await store.set_status(run.id, 'interrupted', stop_requested=True)
    await store.aclose()
    before = store.path.read_bytes()
    assert inspect_history(store.path)['ok'] is True
    assert store.path.read_bytes() == before
    assert json.loads(op.model_dump_json())['tool'] == 'corel_execute_vba'


@pytest.mark.parametrize('applied', [None, True, False])
async def test_preupgrade_retained_absence_does_not_resolve_unknown_mutation(runtime, store, applied):
    from conftest import wait_run
    from test_readiness import drain
    from test_retained_cleanup import retained

    from corel_agent.preupgrade import inspect_history
    await retained(runtime, store, applied=applied)
    refresh = await runtime.start(command())
    await wait_run(store, refresh.id)
    await drain(runtime)
    await runtime.aclose()
    await store.aclose()
    result = inspect_history(store.path)
    assert result['ok'] is (applied is False)
    if applied is False:
        assert len(result['resolved_reports']) == 1
        assert result['resolved_reports'][0]['cleanup']['project_retained'] is True
    else:
        assert result['reason'] == 'MUTATION_OUTCOME_UNRESOLVED'
