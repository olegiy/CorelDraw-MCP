import asyncio
from copy import deepcopy

import pytest
from conftest import command, drawing_arguments, implementation, wait_run
from pydantic import SecretStr
from test_approvals import calls, prime
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


async def test_typed_multicommand_recipe_uses_observed_revision_and_same_page_within_budget(runtime, store):
    import json

    from corel_agent.contracts import NativeToolCall
    create = completion('corel_create_shapes', page_index=2, layer_id='layer', items=[
        {'kind': 'rectangle', 'x_mm': 0, 'y_mm': 0, 'width_mm': 10, 'height_mm': 10}])
    plan = completion('corel_layout_shapes', page_index=2, static_ids=[1001])
    provider, cmd = await configure(runtime, store, [create, plan, completion()])
    original = provider.complete

    async def complete(history, tools, emit_delta=None):
        if len(provider.histories) == 2:
            provider.histories.append(deepcopy(history))
            observed = json.loads(next(item['content'] for item in reversed(history) if item['role'] == 'tool'))
            revision = observed['report']['result']['layout_revision']
            return Completion(tool_calls=(NativeToolCall(id='layout-apply', name='corel_layout_shapes', arguments={
                'page_index': 2, 'static_ids': [1001], 'dry_run': False, 'expected_revision': revision}),))
        return await original(history, tools, emit_delta)

    provider.complete = complete
    run = await runtime.start(cmd)
    snapshot = await wait_run(store, run.id, timeout=10)
    assert snapshot.status == 'completed' and snapshot.max_calls == 20
    assert snapshot.counters.calls == 9 and snapshot.counters.turns == 4
    system = '\n'.join(item['content'] for item in provider.histories[0] if item['role'] == 'system')
    assert 'corel_execute_vba' not in system and 'corel_build_vba' not in system
    assert calls(store, 'corel_layout_shapes')[-1]['args']['expected_revision'] == '1' * 64
    assert all(item['args']['document_id'] == 'fixture-b' and item['args']['page_index'] == 2
               for tool in ('corel_create_shapes', 'corel_layout_shapes', 'corel_preview', 'corel_inspect_document')
               for item in calls(store, tool))


async def test_historical_code_message_is_data_and_cannot_reappear_as_tool_dispatch(runtime, store):
    from corel_agent.contracts import new_id
    provider, cmd = await configure(runtime, store, [completion()])
    old = await runtime.start(cmd)
    await wait_run(store, old.id)
    await store.add_message(old.id, 'assistant', 'Историческая операция corel_execute_vba', {
        'role': 'assistant', 'tool_calls': [{'id': 'old', 'function': {'name': 'corel_execute_vba', 'arguments': '{"code":"historical"}'}}]})
    following = FakeProvider([completion()])
    runtime.provider_factory = lambda frozen: following
    run = await runtime.start(cmd.model_copy(update={'client_request_id': new_id(), 'message': 'Продолжай'}))
    assert (await wait_run(store, run.id)).status == 'completed'
    assert not any(item.get('tool_calls') for item in following.histories[0])
    assert calls(store, 'corel_execute_vba') == []


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


async def test_verified_preview_to_model_is_automatic(runtime, store):
    provider, cmd = await configure(runtime, store, [completion('corel_preview'), completion()])
    run = await runtime.start(cmd)
    assert (await wait_run(store, run.id)).status == 'completed'
    image_messages = [message for message in provider.histories[1]
                      if isinstance(message.get('content'), list)]
    assert len(image_messages) == 1
    image = image_messages[0]['content'][1]
    assert image['type'] == 'image_url'
    assert image['image_url']['url'].startswith('data:image/png;base64,')
    messages = await store.get_messages(cmd.conversation_id)
    assert not any('data:image/png;base64,' in item['text'] for item in messages)


async def test_duplicate_request_returns_frozen_profile_without_vault_lookup(runtime, store):
    provider, cmd = await configure(runtime, store, [completion()])
    run = await runtime.start(cmd)
    await wait_run(store, run.id)
    runtime.settings._vault.unavailable = True
    duplicate = await runtime.start(cmd)
    assert duplicate.id == run.id and duplicate.profile.model == "fixture"
    assert len(provider.histories) == 1


async def test_next_run_in_same_conversation_receives_previous_finished_dialog(runtime, store):
    provider, cmd = await configure(runtime, store, [completion()])
    first = await runtime.start(cmd)
    await wait_run(store, first.id)
    followup = FakeProvider([completion()])
    runtime.provider_factory = lambda frozen: followup
    from corel_agent.contracts import new_id
    second = await runtime.start(cmd.model_copy(update={'client_request_id': new_id(), 'message': 'Продолжи прежний ответ'}))
    assert (await wait_run(store, second.id)).status == 'completed'
    history = followup.histories[0]
    assert any(item.get('role') == 'user' and item.get('content') == cmd.message for item in history)
    assert any(item.get('role') == 'assistant' and item.get('content') == 'Готово' for item in history)
    assert history[-1]['content'] == 'Продолжи прежний ответ'


async def test_background_drawing_executes_and_postchecks_without_confirmation(runtime, store):
    provider, cmd = await configure(runtime, store, [completion("corel_edit_text", **drawing_arguments("fixture")), completion()])
    run = await runtime.start(cmd)
    snapshot = await wait_run(store, run.id)
    assert snapshot.status == "completed" and snapshot.counters.calls == 3
    assert len(provider.histories) == 2
    assert len(calls(store, 'corel_edit_text')) == 1
    assert not any(event.type == 'confirmation' for event in await store.get_events(run.id))


async def test_failed_drawing_is_not_repaired_or_retried(runtime, store):
    provider, cmd = await configure(runtime, store, [completion("corel_edit_text", **drawing_arguments("fail")), completion()])
    run = await runtime.start(cmd)
    assert (await wait_run(store, run.id)).status == "failed"
    assert len(provider.histories) == 1 and len(calls(store, "corel_edit_text")) == 1
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
    provider, cmd = await configure(runtime, store, [completion("corel_edit_text", **drawing_arguments("fixture")), completion()])
    run = await runtime.start(cmd)
    async def job_started():
        while not calls(store, "corel_edit_text"):
            await asyncio.sleep(0.01)
    await asyncio.wait_for(job_started(), 5)
    await runtime.stop(run.id)
    assert (await wait_run(store, run.id)).status == "stopped"
    operations = await store.get_operations(run.id)
    assert operations[0]["result"]["outcome"] == "succeeded"
    assert len(provider.histories) == 1 and runtime.mcp.transport.returncode is None


async def test_call_budget_is_reserved_before_mutation_even_with_model_tools(runtime, store):
    provider, cmd = await configure(runtime, store, [completion("corel_edit_text", **drawing_arguments("fixture"))])
    run = await runtime.start(cmd.model_copy(update={"max_calls": 1}))
    assert (await wait_run(store, run.id)).status == "limit_reached"
    assert calls(store, "corel_edit_text") == []


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


async def test_explicit_new_document_intent_binds_actual_created_id(runtime, store):
    provider, cmd = await configure(runtime, store, [completion('corel_create_document'), completion('corel_preview'), completion()])
    cmd = cmd.model_copy(update={'document_id': None, 'new_document': True})
    run = await runtime.start(cmd)
    final = await wait_run(store, run.id)
    assert final.status == 'completed'
    assert final.document_id.startswith('fixture-created-')
    preview = calls(store, 'corel_preview')[-1]
    assert preview['args']['document_id'] == final.document_id
    assert len(calls(store, 'corel_create_document')) == 1


async def test_new_document_intent_cannot_detach_selected_document(runtime, store):
    provider, cmd = await configure(runtime, store, [completion()])
    with pytest.raises(AgentError) as caught:
        await runtime.start(cmd.model_copy(update={'new_document': True}))
    assert caught.value.code == 'DOCUMENT_INTENT_CONFLICT'
    assert provider.histories == []
