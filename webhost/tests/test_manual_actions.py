import pytest
from conftest import command, wait_run

from corel_agent.contracts import AgentError


async def test_manual_status_inspect_selection_preview(runtime, store):
    status = await runtime.start(command())
    assert (await wait_run(store, status.id)).status == "completed"
    documents = await store.get_meta("documents")
    assert len(documents["items"]) == 2 and not documents["stale"]
    inspected = await runtime.start(command(tool="corel_inspect_document", document_id="fixture-b"))
    assert (await wait_run(store, inspected.id)).status == "completed"
    ops = await store.get_operations(inspected.id)
    assert ops[0]["report"]["result"]["received"]["document_id"] == "fixture-b"
    preview = await runtime.start(command(tool="corel_preview", document_id="fixture-b", arguments={"target": "selection"}))
    assert (await wait_run(store, preview.id)).status == "completed"
    events = await store.get_events(preview.id)
    images = [event for event in events if event.type == "image"]
    assert images[0].data.document_id == "fixture-b" and images[0].data.target == "selection"


async def test_busy_read_uses_cache_or_409(runtime, store):
    run = await runtime.start(command())
    with pytest.raises(AgentError) as caught:
        await runtime.start(command())
    assert caught.value.code == "BUSY"
    assert await store.get_snapshot(run.id)
    await wait_run(store, run.id)


async def test_stale_document_not_rebound(runtime, store):
    run = await runtime.start(command())
    await wait_run(store, run.id)
    with pytest.raises(AgentError) as caught:
        await runtime.start(command(tool="corel_preview", document_id="not-the-session"))
    assert caught.value.code == "STALE_DOCUMENT"


@pytest.mark.parametrize("runtime", [("--empty-selection",)], indirect=True)
async def test_empty_selection_is_an_error_not_page_fallback(runtime, store):
    run = await runtime.start(command())
    await wait_run(store, run.id)
    preview = await runtime.start(command(tool="corel_preview", document_id="fixture-b",
                                          arguments={"target": "selection"}))
    assert (await wait_run(store, preview.id)).status == "failed"
    operations = await store.get_operations(preview.id)
    assert len(operations) == 1 and operations[0]["report"]["error"]["code"] == "EMPTY_SELECTION"
    assert not any(event.type == "image" for event in await store.get_events(preview.id))
