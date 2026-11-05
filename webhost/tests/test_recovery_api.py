import httpx
from conftest import implementation
from test_manual_api import HarnessAccess


async def test_recovery_review_records_manual_observation_without_clearing_barrier(runtime, store):
    app = implementation("app").create_app(runtime=runtime, store=store, assets=runtime.assets,
                                            access=HarnessAccess())
    await store.put_meta("safety_barrier", {"id": "00000000-0000-0000-0000-000000000001",
        "reason": "COREL_OUTCOME_UNKNOWN", "run_id": None, "operation_id": None, "job_id": "known-job",
        "at": "2026-11-05T00:00:00Z"})
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://127.0.0.1:8878") as client:
        response = await client.post("/api/v1/recovery/review", json={"note": "Оператор проверил CorelDRAW", "document_id": "fixture-b"})
        assert response.status_code == 201
        assert (await client.get("/api/v1/health")).json()["safety_barrier"] is not None
        assert (await client.get("/api/v1/recovery/reviews")).json()["items"][0]["document_id"] == "fixture-b"


async def test_reconcile_endpoint_refuses_live_mcp_and_caller_paths(runtime, store):
    app = implementation("app").create_app(runtime=runtime, store=store, assets=runtime.assets,
                                            access=HarnessAccess())
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://127.0.0.1:8878") as client:
        response = await client.post('/api/v1/recovery/reconcile', json={})
        assert response.status_code == 409 and response.json()['code'] == 'RECOVERY_NOT_ALLOWED'
        response = await client.post('/api/v1/recovery/reconcile', json={'reports': 'C:/private'})
        assert response.status_code == 422


async def test_reconcile_endpoint_uses_fixed_root_and_never_starts_model(runtime, store, monkeypatch):
    from pathlib import Path
    module = implementation('recovery')
    seen = []
    async def fake_reconcile(ledger, bindings, directory, reports):
        seen.append((bindings, directory, reports))
        await ledger.put_meta('recovery_pending', [])
        return True
    monkeypatch.setattr(module, 'reconcile', fake_reconcile)
    await store.put_meta('recovery_pending', [{'job_id': 'fixture'}])
    # The real test-owned transport is closed naturally, never force-killed.
    await runtime.mcp.aclose()
    monkeypatch.setenv('LOCALAPPDATA', str(store.directory))
    app = implementation('app').create_app(runtime=runtime, store=store, assets=runtime.assets, access=HarnessAccess())
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url='http://127.0.0.1:8878') as client:
        response = await client.post('/api/v1/recovery/reconcile', json={})
        assert response.status_code == 200 and response.json()['reconciled'] is True
    assert seen[0][2] == Path(store.directory) / 'CorelDrawMcp' / 'runs'
    assert await store.get_meta('recovery_only') is True
    assert runtime.mcp.ready is False


async def test_idle_or_prepared_crash_owner_can_be_explicitly_reconciled(runtime, store, monkeypatch):
    module = implementation('recovery')
    seen = []
    async def fake_reconcile(ledger, bindings, directory, reports):
        seen.append(bindings)
        return True
    monkeypatch.setattr(module, 'reconcile', fake_reconcile)
    await runtime.mcp.aclose()
    (store.directory / 'mcp-owner.json').write_text('test-owned marker')
    monkeypatch.setenv('LOCALAPPDATA', str(store.directory))
    app = implementation('app').create_app(runtime=runtime, store=store, assets=runtime.assets, access=HarnessAccess())
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url='http://127.0.0.1:8878') as client:
        result = await client.post('/api/v1/recovery/reconcile', json={})
        assert result.status_code == 200
    assert seen == [[]]


async def test_operator_review_releases_only_matching_barrier_and_preserves_old_outcome(runtime, store):
    from conftest import command, wait_run

    from corel_agent.contracts import SafetyBarrier
    run = await runtime.start(command())
    await wait_run(store, run.id)
    barrier = SafetyBarrier(reason='COREL_OUTCOME_UNKNOWN', run_id=run.id)
    await store.set_barrier(barrier)
    app = implementation('app').create_app(runtime=runtime, store=store, assets=runtime.assets, access=HarnessAccess())
    payload = {'note': 'Рисунок и диагностика проверены оператором', 'document_id': 'fixture-b',
               'barrier_id': barrier.id, 'release_for_new_context': True, 'manual_corel_checked': True}
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url='http://127.0.0.1:8878') as client:
        response = await client.post('/api/v1/recovery/review', json=payload)
        assert response.status_code == 201
        assert await store.get_barrier() is None
        assert (await store.get_snapshot(run.id)).status == 'uncertain'
        review = (await client.get('/api/v1/recovery/reviews')).json()['items'][0]
        assert review['barrier_id'] == barrier.id and review['manual_corel_checked'] is True
        assert (await client.post('/api/v1/recovery/review', json=payload)).status_code == 409


async def test_release_review_requires_manual_check_fresh_scope_and_verified_owner(runtime, store):
    from corel_agent.contracts import SafetyBarrier
    barrier = SafetyBarrier(reason='CLEANUP_NOT_PROVEN')
    await store.set_barrier(barrier)
    app = implementation('app').create_app(runtime=runtime, store=store, assets=runtime.assets, access=HarnessAccess())
    payload = {'note': 'review', 'document_id': 'fixture-b', 'barrier_id': barrier.id,
               'release_for_new_context': True, 'manual_corel_checked': True}
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url='http://127.0.0.1:8878') as client:
        assert (await client.post('/api/v1/recovery/review', json={**payload, 'manual_corel_checked': False})).status_code == 409
        assert (await client.post('/api/v1/recovery/review', json=payload)).status_code == 409
        assert await store.get_barrier() is not None


async def test_barrier_release_refuses_unresolved_dispatch_even_after_manual_check(runtime, store):
    from conftest import command, wait_run
    from test_recovery import operation

    from corel_agent.contracts import SafetyBarrier
    refreshed = await runtime.start(command())
    await wait_run(store, refreshed.id)
    run = (await store.accept_run(command(read_only=False), None)).snapshot
    op = operation(run)
    await store.record_operation(op)
    await store.mark_dispatching(op.operation_id)
    barrier = SafetyBarrier(reason='DISPATCH_OUTCOME_UNKNOWN', run_id=run.id, operation_id=op.operation_id)
    await store.set_barrier(barrier)
    app = implementation('app').create_app(runtime=runtime, store=store, assets=runtime.assets, access=HarnessAccess())
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url='http://127.0.0.1:8878') as client:
        response = await client.post('/api/v1/recovery/review', json={'note': 'checked', 'document_id': 'fixture-b',
            'barrier_id': barrier.id, 'manual_corel_checked': True, 'release_for_new_context': True})
        assert response.status_code == 409 and response.json()['code'] == 'REVIEW_JOB_PENDING'
    assert await store.get_barrier() is not None
