import asyncio

import pytest
from conftest import command

from corel_agent.contracts import AgentError, ProfileSnapshot, new_id


async def test_duplicate_request_returns_original_run(store):
    cmd = command(profile_id=new_id())
    profile = ProfileSnapshot(id=cmd.profile_id, name="Первоначальная", model="first",
                              base_url="http://127.0.0.1:1234/v1", local=True)
    first = await store.accept_run(cmd, profile)
    later = await store.accept_run(cmd, profile.model_copy(update={"model": "changed"}))
    assert first.is_new and not later.is_new
    assert later.snapshot.id == first.snapshot.id
    assert later.snapshot.profile.model == "first"
    assert later.snapshot.last_seq >= 1
    assert len(await store.list_runs()) == 1


async def test_conflicting_request_409(store):
    cmd = command()
    await store.accept_run(cmd, None)
    with pytest.raises(AgentError) as caught:
        await store.accept_run(cmd.model_copy(update={"tool": "corel_preview"}), None)
    assert caught.value.status == 409 and caught.value.code == "REQUEST_ID_CONFLICT"


async def test_early_stop_tombstone(store):
    cmd = command()
    assert await store.stop_request(cmd.client_request_id) is None
    accepted = await store.accept_run(cmd, None)
    assert accepted.snapshot.stop_requested
    assert accepted.snapshot.status == "stopped"
    repeated = await store.accept_run(cmd, None)
    assert not repeated.is_new and repeated.snapshot.id == accepted.snapshot.id


async def test_parallel_runs_one_reservation(store):
    results = await asyncio.gather(*(store.accept_run(command(), None) for _ in range(6)),
                                   return_exceptions=True)
    assert sum(not isinstance(item, Exception) for item in results) == 1
    assert all(item.code == "BUSY" for item in results if isinstance(item, AgentError))


async def test_event_seq_and_snapshot_watermark_are_consistent(store):
    run = (await store.accept_run(command(), None)).snapshot
    await asyncio.gather(*(store.append_event(run.id, "warning", {
        "code": "FIXTURE", "message": str(index),
    }) for index in range(12)))
    events = await store.get_events(run.id, after=0, limit=100)
    snapshot = await store.get_snapshot(run.id)
    assert [item.seq for item in events] == list(range(1, snapshot.last_seq + 1))
    assert len(await store.get_events(run.id, after=snapshot.last_seq - 2, limit=1)) == 1


async def test_full_store_blocks_new_mutation_but_keeps_report_reads(store):
    run = (await store.accept_run(command(), None)).snapshot
    await store.set_status(run.id, "completed")
    store.quota_bytes = 1
    with pytest.raises(AgentError) as caught:
        await store.accept_run(command(tool="corel_create_document"), None)
    assert caught.value.code == "STORAGE_FULL"
    assert (await store.get_snapshot(run.id)).status == "completed"


@pytest.mark.parametrize('cmd', [dict(tool='corel_execute_vba', document_id='fixture'),
                               dict(kind='agent', profile_id=new_id()), dict(tool='corel_open_document'),
                               dict(tool='corel_close_document', document_id='fixture')])
async def test_barrier_admission_is_based_on_actual_command_not_a_read_only_flag(store, cmd):
    from corel_agent.contracts import SafetyBarrier
    await store.set_barrier(SafetyBarrier(reason='FIXTURE_UNKNOWN'))
    with pytest.raises(AgentError) as caught:
        await store.accept_run(command(**cmd), None)
    assert caught.value.code == 'SAFETY_BARRIER'
    assert await store.list_runs() == []


@pytest.mark.parametrize('tool', ['corel_status', 'corel_inspect_document', 'corel_preview', 'host_diagnostics'])
async def test_observation_admission_remains_available_under_barrier_and_full_storage(store, tool):
    from corel_agent.contracts import SafetyBarrier
    await store.set_barrier(SafetyBarrier(reason='FIXTURE_UNKNOWN'))
    store.quota_bytes = 1
    run = await store.accept_run(command(tool=tool), None)
    assert run.is_new and run.snapshot.safety_barrier
