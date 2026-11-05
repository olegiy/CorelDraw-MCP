import asyncio
from copy import deepcopy

import pytest
from conftest import command, implementation, wait_run
from pydantic import SecretStr
from test_approvals import calls, pending_confirmation, prime
from test_settings import FakeVault, profile

from corel_agent.contracts import AgentError, Completion, NativeToolCall


class FakeProvider:
    def __init__(self, completions):
        self.completions = list(completions)
        self.histories = []
        self.closed = False
        self.started = asyncio.Event()
        self.release = None

    async def complete(self, history, tools, emit_delta=None):
        self.histories.append(deepcopy(history))
        self.started.set()
        if self.release:
            await self.release.wait()
        return self.completions.pop(0)

    async def aclose(self):
        self.closed = True


def completion(tool=None, **arguments):
    return Completion(content="Готово" if not tool else None,
        tool_calls=(NativeToolCall(id=f"call-{tool}", name=tool, arguments=arguments),) if tool else ())


async def configure(runtime, store, completions):
    await prime(runtime, store)
    settings = implementation("settings").Settings(store.directory, vault=FakeVault())
    saved = await settings.save(profile(), SecretStr("fixture-key"))
    provider = FakeProvider(completions)
    runtime.settings = settings
    runtime.provider_factory = lambda frozen: provider
    return provider, command(kind="agent", tool=None, profile_id=saved.id, document_id="fixture-b",
                              message="Покажи выбранный рисунок")


async def test_agent_serializes_terminal_tool_results_before_next_turn(runtime, store):
    provider, cmd = await configure(runtime, store, [completion("corel_preview", target="selection"), completion()])
    run = await runtime.start(cmd)
    snapshot = await wait_run(store, run.id)
    assert snapshot.status == "completed" and snapshot.counters.turns == 2
    assert len(calls(store, "corel_preview")) == 1
    history = provider.histories[1]
    assert any(item["role"] == "tool" and '"outcome":"succeeded"' in item["content"] for item in history)
    assert history[0]["role"] == "system" and "Corel" in history[0]["content"]
    assert provider.closed


async def test_duplicate_request_returns_frozen_profile_without_vault_lookup(runtime, store):
    provider, cmd = await configure(runtime, store, [completion()])
    run = await runtime.start(cmd)
    await wait_run(store, run.id)
    runtime.settings._vault.unavailable = True
    duplicate = await runtime.start(cmd)
    assert duplicate.id == run.id and duplicate.profile.model == "fixture"
    assert len(provider.histories) == 1


async def test_dangerous_background_step_waits_for_confirmation(runtime, store):
    provider, cmd = await configure(runtime, store, [completion("corel_execute_vba", code="fixture"), completion()])
    run = await runtime.start(cmd.model_copy(update={"read_only": False}))
    pending = await pending_confirmation(store, run.id)
    assert calls(store, "corel_execute_vba") == [] and len(provider.histories) == 1
    await runtime.confirm(run.id, pending.confirmation_id, pending.operation_digest, True)
    snapshot = await wait_run(store, run.id)
    assert snapshot.status == "completed" and snapshot.counters.calls == 3
    assert len(provider.histories) == 2


async def test_failed_vba_is_not_repaired_or_retried(runtime, store):
    provider, cmd = await configure(runtime, store, [completion("corel_execute_vba", code="fail"), completion()])
    run = await runtime.start(cmd.model_copy(update={"read_only": False}))
    pending = await pending_confirmation(store, run.id)
    await runtime.confirm(run.id, pending.confirmation_id, pending.operation_digest, True)
    assert (await wait_run(store, run.id)).status == "failed"
    assert len(provider.histories) == 1 and len(calls(store, "corel_execute_vba")) == 1
    operations = await store.get_operations(run.id)
    assert operations[0]["result"]["applied"] is True


async def test_stop_during_model_wait_cancels_only_model_not_com(runtime, store):
    provider, cmd = await configure(runtime, store, [completion("corel_preview")])
    provider.release = asyncio.Event()
    run = await runtime.start(cmd)
    await provider.started.wait()
    await runtime.stop(run.id)
    assert (await wait_run(store, run.id)).status == "stopped"
    assert calls(store, "corel_preview") == [] and provider.closed


async def test_explicit_stop_drains_job_and_persists_applied_outcome(runtime, store):
    provider, cmd = await configure(runtime, store, [completion("corel_execute_vba", code="fixture"), completion()])
    run = await runtime.start(cmd.model_copy(update={"read_only": False}))
    pending = await pending_confirmation(store, run.id)
    await runtime.confirm(run.id, pending.confirmation_id, pending.operation_digest, True)
    async def job_started():
        while not calls(store, "corel_execute_vba"):
            await asyncio.sleep(0.01)
    await asyncio.wait_for(job_started(), 5)
    await runtime.stop(run.id)
    assert (await wait_run(store, run.id)).status == "stopped"
    operations = await store.get_operations(run.id)
    assert operations[0]["result"]["outcome"] == "succeeded"
    assert len(provider.histories) == 1 and runtime.mcp.transport.returncode is None


async def test_call_budget_is_reserved_before_mutation_even_with_model_tools(runtime, store):
    provider, cmd = await configure(runtime, store, [completion("corel_execute_vba", code="fixture")])
    run = await runtime.start(cmd.model_copy(update={"read_only": False, "max_calls": 1}))
    assert (await wait_run(store, run.id)).status == "limit_reached"
    assert calls(store, "corel_execute_vba") == []


async def test_stop_before_first_model_call_never_looks_up_profile(runtime, store):
    cmd = command(kind="agent", tool=None, profile_id="00000000-0000-0000-0000-000000000001")
    await runtime.stop_request(cmd.client_request_id)
    run = await runtime.start(cmd)
    assert run.status == "stopped"


async def test_unbound_agent_does_not_guess_active_document(runtime, store):
    provider, cmd = await configure(runtime, store, [completion("corel_preview")])
    with pytest.raises(AgentError) as caught:
        await runtime.start(cmd.model_copy(update={"document_id": None}))
    assert caught.value.code == "DOCUMENT_REQUIRED" and provider.histories == []
