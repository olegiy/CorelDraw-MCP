import json

import pytest
from conftest import command

from corel_agent.contracts import PreparedOperation, new_id, utc_now
from corel_agent.store import fingerprint


async def legacy_confirmation(store):
    from test_settings import profile
    frozen = profile()
    cmd = command(profile_id=frozen.id)
    accepted = await store.accept_run(cmd, frozen)
    snapshot = accepted.snapshot.model_dump()
    operation = PreparedOperation(operation_id=new_id(), run_id=snapshot['id'],
        tool='corel_execute_vba', arguments={'code': 'legacy code', 'document_id': 'fixture-b'},
        document_id='fixture-b', digest='0' * 64, policy_version=1).model_dump(mode='json')
    confirmation = {'confirmation_id': new_id(), 'operation_digest': operation['digest'],
                    'operation': operation, 'reasons': ['Legacy VBA']}
    legacy_command = cmd.model_dump()
    legacy_command.pop('output_directory', None)
    legacy_command.update(read_only=False, output_root_id=None)
    snapshot.pop('output_directory', None)
    snapshot.update(status='awaiting_confirmation', read_only=False, output_root_id=None,
                    pending_confirmation=confirmation, active_operation_id=operation['operation_id'], last_seq=2)
    snapshot['profile'].update(context_approved=True, vision=False, send_preview=False)
    event = {'schema_version': 1, 'run_id': snapshot['id'], 'seq': 2, 'at': utc_now(),
        'type': 'confirmation', 'data': confirmation, 'state': {'status': 'awaiting_confirmation',
            'document_id': None, 'stop_requested': False, 'active_operation_id': operation['operation_id'],
            'job_id': None, 'counters': snapshot['counters'], 'monitoring_state': 'healthy', 'safety_barrier': None}}
    def seed(db):
        db.execute('UPDATE runs SET command=?,snapshot=? WHERE id=?',
                   (json.dumps(legacy_command), json.dumps(snapshot), snapshot['id']))
        db.execute('UPDATE requests SET fingerprint=? WHERE run_id=?', (fingerprint(legacy_command), snapshot['id']))
        db.execute("INSERT INTO operations(id,run_id,prepared,state) VALUES(?,?,?,'prepared')",
                   (operation['operation_id'], snapshot['id'], json.dumps(operation)))
        db.execute("INSERT INTO approvals VALUES(?,?,?,'pending')",
                   (confirmation['confirmation_id'], snapshot['id'], operation['digest']))
        db.execute('INSERT INTO events VALUES(?,?,?)', (snapshot['id'], 2, json.dumps(event)))
    await store._db(seed, write=True)
    return {'id': snapshot['id'], 'command': legacy_command, 'snapshot': snapshot, 'operation': operation,
            'confirmation': confirmation, 'event': event, 'new_command': cmd}


@pytest.mark.parametrize('found', [False, True])
async def test_legacy_snapshot_and_command_root_id_resolve_only_saved_workspace(store, tmp_path, found):
    root_id = new_id()
    await store.put_meta('workspace', {'output_roots': [{'id': root_id, 'path': str(tmp_path)}]})
    accepted = await store.accept_run(command(), None)
    run_id = accepted.snapshot.id
    def legacy(db):
        row = db.execute('SELECT command,snapshot FROM runs WHERE id=?', (run_id,)).fetchone()
        cmd, snapshot = json.loads(row['command']), json.loads(row['snapshot'])
        for value in (cmd, snapshot):
            value.pop('output_directory', None)
            value['output_root_id'] = root_id if found else new_id()
        db.execute('UPDATE runs SET command=?,snapshot=? WHERE id=?', (json.dumps(cmd), json.dumps(snapshot), run_id))
    await store._db(legacy, write=True)
    assert (await store.get_command(run_id)).output_directory == (str(tmp_path) if found else None)
    assert (await store.get_snapshot(run_id)).output_directory == (str(tmp_path) if found else None)
    assert (await store.list_runs())[0].output_directory == (str(tmp_path) if found else None)


async def test_legacy_context_uses_its_own_root_path(runtime, store, tmp_path):
    accepted = await store.accept_run(command(), None)
    run_id = accepted.snapshot.id
    await store.put_meta('workspace', {'output_roots': [{'id': new_id(), 'path': str(tmp_path / 'different')}]})
    await store.put_meta(f'context:{run_id}', {'run_id': run_id, 'document_id': None, 'read_only': True,
        'output_root': {'id': new_id(), 'path': str(tmp_path)}, 'files': [], 'allow_new_document': False})
    assert (await runtime._context(run_id)).output_directory == str(tmp_path)


async def test_legacy_pending_evidence_is_read_without_actionable_permissions(store):
    old = await legacy_confirmation(store)
    snapshot = await store.get_snapshot(old['id'])
    assert snapshot.status == 'interrupted'
    assert 'read_only' not in snapshot.model_dump() and 'pending_confirmation' not in snapshot.model_dump()
    assert snapshot.profile.credential_revision == old['snapshot']['profile']['credential_revision']
    assert 'context_approved' not in snapshot.profile.model_dump()
    events = await store.get_events(old['id'])
    assert events[-1].type == 'warning' and events[-1].data.code == 'LEGACY_CONFIRMATION'
    assert events[-1].seq == 2 and events[-1].state.status == 'awaiting_confirmation'
    raw = await store._db(lambda db: json.loads(db.execute('SELECT payload FROM events WHERE run_id=? AND seq=2', (old['id'],)).fetchone()[0]))
    assert raw == old['event']


async def test_legacy_request_fingerprint_is_not_rewritten_for_new_commands(store):
    from corel_agent.contracts import AgentError
    old = await legacy_confirmation(store)
    with pytest.raises(AgentError) as caught:
        await store.lookup_command(old['new_command'])
    assert caught.value.code == 'REQUEST_ID_CONFLICT'
    raw = await store._db(lambda db: json.loads(db.execute('SELECT command FROM runs WHERE id=?', (old['id'],)).fetchone()[0]))
    assert raw == old['command']
