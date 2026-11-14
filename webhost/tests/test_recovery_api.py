import json

import httpx
from conftest import command, drawing_arguments, implementation, wait_run
from test_manual_api import HarnessAccess
from test_readiness import fault

from corel_agent.contracts import new_id, utc_now


async def test_review_is_read_only_audit_and_cannot_authorize_execution(runtime, store):
    app = implementation('app').create_app(runtime=runtime, store=store, assets=runtime.assets, access=HarnessAccess())
    data = {'note': 'Старое наблюдение', 'document_id': 'fixture-b', 'barrier_id': None,
            'release_for_new_context': True, 'manual_corel_checked': True, 'at': utc_now()}
    raw = json.dumps(data)
    await store._db(lambda db: db.execute('INSERT INTO reviews VALUES(?,?,?)', (new_id(), raw, utc_now())), write=True)
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url='http://127.0.0.1:8878') as client:
        assert (await client.post('/api/v1/recovery/review', json=data)).status_code == 404
        audit = (await client.get('/api/v1/recovery/reviews')).json()['items']
        assert len(audit) == 1 and audit[0]['data'] == data
    assert await store._db(lambda db: db.execute('SELECT payload FROM reviews').fetchone()[0]) == raw
    assert not hasattr(store, 'add_review') and not hasattr(store, 'release_barrier')


async def test_confirmed_fault_is_released_at_new_admission_without_review(runtime, store):
    _, old_id, op = await fault(runtime, store)
    old = await store.get_snapshot(old_id)
    app = implementation('app').create_app(runtime=runtime, store=store, assets=runtime.assets, access=HarnessAccess())
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url='http://127.0.0.1:8878') as client:
        response = await client.post('/api/v1/actions', json=command(tool='corel_edit_text',
            document_id='fixture-b', arguments=drawing_arguments('fixture')).model_dump())
        assert response.status_code == 202
        assert (await wait_run(store, response.json()['id'])).status == 'completed'
    assert await store.get_barrier() is None
    assert (await store.get_snapshot(old_id)).model_dump() == old.model_dump()
    assert (await store.get_operation(op.operation_id))['result']['outcome'] == 'succeeded'


async def test_health_explains_unproven_fault_without_executable_flags(runtime, store):
    barrier, _, _ = await fault(runtime, store, outcome='unknown')
    app = implementation('app').create_app(runtime=runtime, store=store, assets=runtime.assets, access=HarnessAccess())
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url='http://127.0.0.1:8878') as client:
        response = await client.get('/api/v1/health')
        assert response.status_code == 200
        assert response.json()['safety_barrier']['id'] == barrier.id
        assert response.json()['readiness_reason'] == 'OUTCOME_UNKNOWN'
        assert (await client.post('/api/v1/recovery/review', json={'manual_corel_checked': True})).status_code == 404


async def test_reconcile_endpoint_refuses_live_mcp_and_caller_paths(runtime, store):
    app = implementation('app').create_app(runtime=runtime, store=store, assets=runtime.assets, access=HarnessAccess())
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url='http://127.0.0.1:8878') as client:
        response = await client.post('/api/v1/recovery/reconcile', json={})
        assert response.status_code == 409 and response.json()['code'] == 'RECOVERY_NOT_ALLOWED'
        response = await client.post('/api/v1/recovery/reconcile', json={'reports': 'C:/private'})
        assert response.status_code == 422


async def test_reconcile_endpoint_uses_fixed_root_and_never_starts_model(runtime, store, monkeypatch):
    from pathlib import Path
    seen = []
    async def fake_reconcile(ledger, bindings, directory, reports):
        seen.append((bindings, directory, reports))
        await ledger.put_meta('recovery_pending', [])
        return True
    monkeypatch.setattr(implementation('recovery'), 'reconcile', fake_reconcile)
    await store.put_meta('recovery_pending', [{'job_id': 'fixture'}])
    await runtime.mcp.aclose()
    monkeypatch.setenv('LOCALAPPDATA', str(store.directory))
    app = implementation('app').create_app(runtime=runtime, store=store, assets=runtime.assets, access=HarnessAccess())
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url='http://127.0.0.1:8878') as client:
        response = await client.post('/api/v1/recovery/reconcile', json={})
        assert response.status_code == 200 and response.json()['reconciled'] is True
    assert seen[0][2] == Path(store.directory) / 'CorelDrawMcp/runs'
    assert await store.get_meta('recovery_only') is True and runtime.mcp.ready is False


async def test_idle_or_prepared_crash_owner_can_be_explicitly_reconciled(runtime, store, monkeypatch):
    seen = []
    async def fake_reconcile(ledger, bindings, directory, reports):
        seen.append(bindings)
        return True
    monkeypatch.setattr(implementation('recovery'), 'reconcile', fake_reconcile)
    await runtime.mcp.aclose()
    (store.directory / 'mcp-owner.json').write_text('test-owned marker')
    monkeypatch.setenv('LOCALAPPDATA', str(store.directory))
    app = implementation('app').create_app(runtime=runtime, store=store, assets=runtime.assets, access=HarnessAccess())
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url='http://127.0.0.1:8878') as client:
        assert (await client.post('/api/v1/recovery/reconcile', json={})).status_code == 200
    assert seen == [[]]
