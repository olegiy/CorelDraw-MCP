import asyncio

from conftest import command, wait_run

from corel_agent.events import run_stream


async def test_disconnect_continues_safe_steps(runtime, store):
    run = await runtime.start(command())
    stream = run_stream(store, runtime.hub, run.id)
    event = await anext(stream)
    assert event["event"] == "update"
    await stream.aclose()
    snapshot = await wait_run(store, run.id)
    assert snapshot.status == "completed" and not snapshot.stop_requested
    assert runtime.hub.subscriber_count == 0


async def test_terminal_event_replay_uses_cursor_not_array_length(runtime, store):
    run = await runtime.start(command())
    snapshot = await wait_run(store, run.id)
    events = [event async for event in run_stream(store, runtime.hub, run.id, snapshot.last_seq - 1)]
    assert len(events) == 1 and events[0]["id"] == str(snapshot.last_seq)
    assert [event async for event in run_stream(store, runtime.hub, run.id, snapshot.last_seq)] == []


async def test_snapshot_and_event_reads_never_create_native_calls(runtime, store):
    run = await runtime.start(command())
    await wait_run(store, run.id)
    path = store.directory / "runs/calls.jsonl"
    before = path.read_bytes()
    await asyncio.gather(*(store.get_snapshot(run.id) for _ in range(20)))
    await store.get_events(run.id, after=0, limit=500)
    assert path.read_bytes() == before
