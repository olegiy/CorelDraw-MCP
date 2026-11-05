import asyncio
import json

import pytest
from conftest import command, wait_run

from corel_agent.contracts import AgentError, OutputRoot


async def pending_confirmation(store, run_id):
    async def wait():
        while True:
            snapshot = await store.get_snapshot(run_id)
            if snapshot.pending_confirmation:
                return snapshot.pending_confirmation
            assert snapshot.status not in {"failed", "rejected", "halted", "uncertain"}
            await asyncio.sleep(0.02)
    return await asyncio.wait_for(wait(), 5)


def calls(store, tool):
    path = store.directory / "runs/calls.jsonl"
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()
            if json.loads(line)["tool"] == tool] if path.exists() else []


async def prime(runtime, store):
    run = await runtime.start(command())
    await wait_run(store, run.id)


async def test_vba_waits_for_current_one_shot_approval(runtime, store):
    await prime(runtime, store)
    run = await runtime.start(command(tool="corel_execute_vba", read_only=False,
                                      document_id="fixture-b", arguments={"code": "fixture code"}))
    pending = await pending_confirmation(store, run.id)
    assert calls(store, "corel_execute_vba") == []
    assert pending.operation.arguments["code"] == "fixture code"
    assert pending.operation.document_id == "fixture-b"
    assert (await store.get_snapshot(run.id)).job_id is None
    await runtime.confirm(run.id, pending.confirmation_id, pending.operation_digest, True)
    assert (await wait_run(store, run.id)).status == "completed"
    assert len(calls(store, "corel_execute_vba")) == 1


async def test_stale_digest_or_double_confirm_rejected(runtime, store):
    await prime(runtime, store)
    run = await runtime.start(command(tool="corel_execute_vba", read_only=False,
                                      document_id="fixture-b", arguments={"code": "fixture code"}))
    pending = await pending_confirmation(store, run.id)
    with pytest.raises(AgentError) as caught:
        await runtime.confirm(run.id, pending.confirmation_id, "f" * 64, True)
    assert caught.value.status == 409 and not calls(store, "corel_execute_vba")
    results = await asyncio.gather(*(
        runtime.confirm(run.id, pending.confirmation_id, pending.operation_digest, True)
        for _ in range(2)), return_exceptions=True)
    assert sum(isinstance(result, AgentError) for result in results) == 1
    await wait_run(store, run.id)
    assert len(calls(store, "corel_execute_vba")) == 1


async def test_changed_existing_file_invalidates_confirmation(runtime, store, tmp_path):
    await prime(runtime, store)
    root = OutputRoot(path=str(tmp_path))
    await store.put_meta("workspace", {"output_roots": [root.model_dump()]})
    path = tmp_path / "existing.png"
    path.write_bytes(b"original sentinel")
    run = await runtime.start(command(tool="corel_export", read_only=False,
        document_id="fixture-b", output_root_id=root.id,
        arguments={"path": "@output/existing.png", "format": "png", "overwrite": True}))
    pending = await pending_confirmation(store, run.id)
    path.write_bytes(b"changed after proposal")
    with pytest.raises(AgentError) as caught:
        await runtime.confirm(run.id, pending.confirmation_id, pending.operation_digest, True)
    assert caught.value.status == 409
    assert (await wait_run(store, run.id)).pending_confirmation is None
    assert calls(store, "corel_export") == []
    assert path.read_bytes() == b"changed after proposal"


async def test_stop_while_awaiting_confirmation_never_dispatches(runtime, store):
    await prime(runtime, store)
    run = await runtime.start(command(tool="corel_execute_vba", read_only=False,
                                      document_id="fixture-b", arguments={"code": "fixture code"}))
    pending = await pending_confirmation(store, run.id)
    await runtime.stop(run.id)
    assert (await wait_run(store, run.id)).status == "stopped"
    with pytest.raises(AgentError):
        await runtime.confirm(run.id, pending.confirmation_id, pending.operation_digest, True)
    assert calls(store, "corel_execute_vba") == []


async def test_read_only_is_enforced_in_runtime_not_ui(runtime, store):
    await prime(runtime, store)
    with pytest.raises(AgentError) as caught:
        await runtime.start(command(tool="corel_execute_vba", read_only=True,
                                    document_id="fixture-b", arguments={"code": "fixture code"}))
    assert caught.value.status == 403 and calls(store, "corel_execute_vba") == []


async def test_insufficient_postcheck_budget_prevents_mutation(runtime, store):
    await prime(runtime, store)
    run = await runtime.start(command(tool="corel_execute_vba", read_only=False, max_calls=1,
                                      document_id="fixture-b", arguments={"code": "fixture code"}))
    assert (await wait_run(store, run.id)).status == "limit_reached"
    assert calls(store, "corel_execute_vba") == []
