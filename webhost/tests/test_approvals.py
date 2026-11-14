import json

import pytest
from conftest import command, drawing_arguments, wait_run


def calls(store, tool):
    path = store.directory / 'runs/calls.jsonl'
    return [json.loads(line) for line in path.read_text(encoding='utf-8').splitlines()
            if json.loads(line)['tool'] == tool] if path.exists() else []


async def prime(runtime, store):
    run = await runtime.start(command())
    await wait_run(store, run.id)


@pytest.mark.parametrize('tool,args', [('corel_edit_text', drawing_arguments('fixture code')),
                                     ('corel_close_document', {'discard_changes': True})])
async def test_mutation_runs_without_permission_or_approval(runtime, store, tool, args):
    await prime(runtime, store)
    run = await runtime.start(command(tool=tool, document_id='fixture-b', arguments=args))
    assert (await wait_run(store, run.id)).status == 'completed'
    assert len(calls(store, tool)) == 1
    assert not any(event.type == 'confirmation' for event in await store.get_events(run.id))
    assert await store._db(lambda db: db.execute('SELECT count(*) FROM approvals').fetchone()[0]) == 0
    assert await store._db(lambda db: db.execute('SELECT count(*) FROM reviews').fetchone()[0]) == 0


async def test_existing_export_destination_is_replaced_without_confirmation(runtime, store, tmp_path):
    await prime(runtime, store)
    path = tmp_path / 'existing.png'
    path.write_bytes(b'original sentinel')
    run = await runtime.start(command(tool='corel_export', document_id='fixture-b',
                                     arguments={'path': str(path), 'format': 'png'}))
    path.write_bytes(b'changed after proposal')
    assert (await wait_run(store, run.id)).status == 'completed'
    assert len(calls(store, 'corel_export')) == 1 and path.read_bytes().startswith(b'\x89PNG')


async def test_insufficient_postcheck_budget_prevents_mutation(runtime, store):
    await prime(runtime, store)
    run = await runtime.start(command(tool='corel_edit_text', max_calls=1,
                                     document_id='fixture-b', arguments=drawing_arguments('fixture code')))
    assert (await wait_run(store, run.id)).status == 'limit_reached'
    assert calls(store, 'corel_edit_text') == []


@pytest.mark.parametrize('mutation', ['arguments', 'document', 'version', 'context', 'barrier', 'budget'])
async def test_changed_prepared_binding_is_rejected_immediately_before_dispatch(runtime, store, monkeypatch, mutation):
    await prime(runtime, store)
    verify = runtime.mcp.transport.verify_owner
    changed = False
    async def owner_check():
        nonlocal changed
        await verify()
        def corrupt(db):
            row = db.execute("SELECT * FROM operations WHERE state='prepared' AND json_extract(prepared,'$.tool')='corel_edit_text'").fetchone()
            if not row:
                return False
            if mutation == 'barrier':
                from corel_agent.contracts import SafetyBarrier
                store._set_barrier(db, SafetyBarrier(reason='FIXTURE_UNKNOWN'))
            elif mutation == 'budget':
                snapshot = json.loads(db.execute('SELECT snapshot FROM runs WHERE id=?', (row['run_id'],)).fetchone()[0])
                snapshot['max_calls'] = 1
                db.execute('UPDATE runs SET snapshot=? WHERE id=?', (json.dumps(snapshot), row['run_id']))
            elif mutation == 'context':
                key = f"context:{row['run_id']}"
                context = json.loads(db.execute('SELECT value FROM meta WHERE key=?', (key,)).fetchone()[0])
                context['document_id'] = 'fixture-a'
                db.execute('UPDATE meta SET value=? WHERE key=?', (json.dumps(context), key))
            else:
                operation = json.loads(row['prepared'])
                if mutation == 'arguments':
                    operation['arguments']['text'] = 'changed code'
                elif mutation == 'document':
                    operation['document_id'] = 'fixture-a'
                else:
                    operation['policy_version'] = 1
                db.execute('UPDATE operations SET prepared=? WHERE id=?', (json.dumps(operation), row['id']))
            return True
        if not changed:
            changed = await store._db(corrupt, write=True)
    monkeypatch.setattr(runtime.mcp.transport, 'verify_owner', owner_check)
    run = await runtime.start(command(tool='corel_edit_text', document_id='fixture-b',
                                     arguments=drawing_arguments('fixture code')))
    assert (await wait_run(store, run.id)).status == ('limit_reached' if mutation == 'budget' else 'failed')
    assert changed and calls(store, 'corel_edit_text') == []


async def test_stop_before_dispatch_keeps_stopped_and_does_not_send(runtime, store, monkeypatch):
    await prime(runtime, store)
    verify = runtime.mcp.transport.verify_owner
    async def stop_after_proposal():
        await verify()
        pending = await store._db(lambda db: db.execute(
            "SELECT run_id FROM operations WHERE state='prepared' AND json_extract(prepared,'$.tool')='corel_edit_text'").fetchone())
        if pending:
            await runtime.stop(pending[0])
    monkeypatch.setattr(runtime.mcp.transport, 'verify_owner', stop_after_proposal)
    run = await runtime.start(command(tool='corel_edit_text', document_id='fixture-b',
                                     arguments=drawing_arguments('fixture code')))
    assert (await wait_run(store, run.id)).status == 'stopped'
    assert calls(store, 'corel_edit_text') == []
