import httpx
import pytest
from conftest import implementation
from test_manual_api import HarnessAccess
from test_settings import FakeVault, profile


@pytest.fixture
async def client(runtime, store):
    settings = implementation("settings").Settings(store.directory, vault=FakeVault())
    runtime.settings = settings
    app = implementation("app").create_app(runtime=runtime, store=store, assets=runtime.assets,
                                            access=HarnessAccess())
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://127.0.0.1:8878") as client:
        yield client


@pytest.mark.parametrize('removed', ['read_only', 'output_root_id', 'allow_new_document'])
async def test_removed_command_fields_are_rejected_not_coerced(client, removed):
    from conftest import command
    response = await client.post('/api/v1/actions', json={**command().model_dump(), removed: False})
    assert response.status_code == 422


async def test_confirmation_endpoint_is_absent_even_for_legacy_pending_run(client, store):
    from conftest import implementation
    assert '/api/v1/runs/{run_id}/confirm' not in implementation('app').create_app().openapi()['paths']
    from test_persisted import legacy_confirmation
    old = await legacy_confirmation(store)
    response = await client.post(f"/api/v1/runs/{old['id']}/confirm", json={
        'confirmation_id': old['confirmation']['confirmation_id'], 'operation_digest': '0' * 64, 'approved': True})
    assert response.status_code == 404
    assert not hasattr(store, 'confirm') and not hasattr(store, 'await_confirmation')


async def test_conversation_history_is_paginated_without_com(client, store):
    response = await client.post("/api/v1/conversations", json={"title": "Чертёж"})
    assert response.status_code == 201
    conversation = response.json()
    page = (await client.get("/api/v1/conversations?limit=1")).json()
    assert page["items"][0]["id"] == conversation["id"]
    messages = await client.get(f'/api/v1/conversations/{conversation["id"]}/messages')
    assert messages.json()["items"] == []
    assert not (store.directory / "runs/calls.jsonl").exists()


async def test_workspace_directory_is_a_destination_not_registration_permission(client, tmp_path):
    response = await client.put("/api/v1/workspace", json={"output_directory": str(tmp_path)})
    assert response.status_code == 200
    assert (await client.get("/api/v1/workspace")).json() == {"output_directory": str(tmp_path)}
    path = tmp_path / "пример.cdr"
    path.write_bytes(b"fixture")
    response = await client.post("/api/v1/files/register", json={"path": str(path)})
    assert response.status_code == 404
    denied = await client.put("/api/v1/workspace", json={"output_directory": str(tmp_path / "not-created")})
    assert denied.status_code == 403 and not (tmp_path / "not-created").exists()


@pytest.mark.parametrize('count', [1, 2])
async def test_schema_one_workspace_roots_are_read_without_permission(client, store, tmp_path, count):
    from corel_agent.contracts import new_id
    roots = [{'id': new_id(), 'path': str(tmp_path / str(index))} for index in range(count)]
    await store.put_meta('workspace', {'output_roots': roots})
    response = await client.get('/api/v1/workspace')
    assert response.status_code == 200
    assert response.json() == {'output_directory': roots[0]['path'] if count == 1 else None}
    assert (await client.put('/api/v1/workspace', json={'output_roots': roots})).status_code == 422


async def test_profile_api_never_returns_or_persists_key(client, store):
    item = profile()
    response = await client.put(f"/api/v1/profiles/{item.id}", json={"profile": item.model_dump(), "api_key": "CANARY-api-key"})
    assert response.status_code == 200 and "CANARY" not in response.text
    response = await client.get("/api/v1/profiles")
    assert response.status_code == 200 and response.json()["items"][0]["id"] == item.id
    assert "CANARY" not in response.text
    assert "CANARY" not in (store.directory / "profiles.json").read_text(encoding="utf-8")


def test_offline_openapi_includes_typed_events_and_error_contract():
    module = implementation("export_openapi")
    schema = module.schema()
    assert "/api/v1/runs" in schema["paths"]
    assert "EventPage" in schema["components"]["schemas"]
    response = schema["paths"]["/api/v1/runs"]["post"]["responses"]["422"]
    assert response["content"]["application/json"]["schema"]["$ref"].endswith("/ErrorResponse")


async def test_profile_native_and_image_test_does_not_call_corel(client, runtime, store, monkeypatch):
    import json

    from test_provider import call, response

    from corel_agent.assets import decode_png
    from corel_agent.contracts import ImageBlock
    from corel_agent.provider import Provider
    requests = []
    def handler(request):
        requests.append(request)
        payload = json.loads(request.content)
        assert payload['tools'][0]['function']['name'] == 'corel_agent_probe'
        image = next(item for item in payload['messages'][0]['content'] if item['type'] == 'image_url')
        assert decode_png(ImageBlock(mime_type='image/png', data=image['image_url']['url'].split(',', 1)[1]))[1:] == (1, 1)
        return httpx.Response(200, json=response([call('corel_agent_probe', '{"ok":true,"pixel_color":"red"}')]))
    monkeypatch.setattr(implementation("app"), "Provider", lambda profile, secret: Provider(
        profile.model_copy(update={'stream_responses': False}), secret, transport=httpx.MockTransport(handler)))
    item = profile()
    await client.put(f"/api/v1/profiles/{item.id}", json={"profile": item.model_dump(), "api_key": "fake"})
    result = await client.post(f"/api/v1/profiles/{item.id}/test", json={})
    assert result.status_code == 200 and result.json()["native_tools"] is True and result.json()['images'] is True
    assert len(requests) == 1
    assert not (store.directory / "runs/calls.jsonl").exists()


@pytest.mark.parametrize('status,color', [(400, None), (200, 'blue')])
async def test_incompatible_model_probe_fails_without_retry(client, store, monkeypatch, status, color):
    from test_provider import call, response

    from corel_agent.provider import Provider
    requests = []
    def handler(request):
        requests.append(request)
        return httpx.Response(status, json=response([call('corel_agent_probe',
            '{"ok":true,"pixel_color":"blue"}')]) if color else {'error': 'CANARY-response'})
    monkeypatch.setattr(implementation('app'), 'Provider', lambda profile, secret: Provider(
        profile.model_copy(update={'stream_responses': False}), secret, transport=httpx.MockTransport(handler)))
    item = profile()
    await client.put(f'/api/v1/profiles/{item.id}', json={'profile': item.model_dump(), 'api_key': 'CANARY-key'})
    result = await client.post(f'/api/v1/profiles/{item.id}/test', json={})
    assert result.status_code == 502 and len(requests) == 1
    assert 'CANARY' not in result.text
    assert not (store.directory / 'runs/calls.jsonl').exists()


@pytest.mark.parametrize('legacy', ['context_approved', 'vision', 'send_preview'])
async def test_new_profile_requests_reject_removed_fields(client, legacy):
    item = profile()
    response = await client.put(f'/api/v1/profiles/{item.id}', json={
        'profile': {**item.model_dump(), legacy: True}, 'api_key': 'CANARY-key'})
    assert response.status_code == 422 and 'CANARY' not in response.text


async def test_diagnostics_is_durable_manual_run_and_download_is_registered(client, store):
    from conftest import command, wait_run
    response = await client.post('/api/v1/diagnostics', json=command().model_dump())
    assert response.status_code == 202
    run = await wait_run(store, response.json()['id'])
    assert run.status == 'completed'
    registered = await store.list_assets()
    assert len(registered) == 1
    response = await client.get(f'/api/v1/artifacts/{registered[0]["id"]}')
    assert response.status_code == 200 and response.headers['content-type'] == 'application/json'
    assert 'attachment' in response.headers['content-disposition']
    assert str(store.directory) not in response.text
    assert not (store.directory / 'runs/calls.jsonl').exists()
    metrics = await client.get('/api/v1/metrics')
    assert metrics.status_code == 200 and metrics.json()['metrics']['runs'] == 1


async def test_backup_endpoint_uses_owned_directory_and_rejects_paths(client, store):
    response = await client.post('/api/v1/history/backup', json={})
    assert response.status_code == 201
    assert response.json()['created'] is True
    assert len(list((store.directory / 'backups').glob('*.sqlite3'))) == 1
    assert str(store.directory) not in response.text
    response = await client.post('/api/v1/history/backup', json={'path': 'C:/private.sqlite3'})
    assert response.status_code == 422


async def test_new_metrics_do_not_expose_approvals(client):
    response = await client.get('/api/v1/metrics')
    assert response.status_code == 200 and 'approvals' not in response.json()['metrics']


async def test_saved_key_is_known_before_any_agent_run_and_never_sent_to_mcp(client, runtime, store):
    from conftest import command
    from test_approvals import calls, prime
    item = profile()
    response = await client.put(f'/api/v1/profiles/{item.id}', json={
        'profile': item.model_dump(), 'api_key': 'CANARY-credential-XYZ'})
    assert response.status_code == 200
    await prime(runtime, store)
    response = await client.post('/api/v1/actions', json=command(tool='corel_execute_vba',
        document_id='fixture-b', arguments={'code': 'CANARY-credential-XYZ'}).model_dump())
    assert response.status_code == 403 and 'CANARY' not in response.text
    assert calls(store, 'corel_execute_vba') == []


async def test_key_value_cannot_be_persisted_in_profile_metadata(client, store):
    item = profile(model='CANARY-credential-XYZ')
    response = await client.put(f'/api/v1/profiles/{item.id}', json={
        'profile': item.model_dump(), 'api_key': 'CANARY-credential-XYZ'})
    assert response.status_code == 403 and 'CANARY' not in response.text
    assert not (store.directory / 'profiles.json').exists()


async def test_health_exposes_disk_guard_and_resume_cursor_increments_metric_without_com(client, runtime, store):
    from conftest import command, wait_run
    run = await runtime.start(command())
    snapshot = await wait_run(store, run.id)
    native_before = (store.directory / 'runs/calls.jsonl').read_bytes()
    store.quota_bytes = 1
    response = await client.get('/api/v1/health')
    assert response.status_code == 200 and response.json()['storage']['full'] is True
    assert str(store.directory) not in response.text
    response = await client.get(f'/api/v1/runs/{run.id}/events', headers={'Last-Event-ID': str(snapshot.last_seq)})
    assert response.status_code == 200
    assert (await client.get('/api/v1/metrics')).json()['metrics']['reconnects'] == 1
    assert (store.directory / 'runs/calls.jsonl').read_bytes() == native_before
