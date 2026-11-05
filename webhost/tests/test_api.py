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


async def test_conversation_history_is_paginated_without_com(client, store):
    response = await client.post("/api/v1/conversations", json={"title": "Чертёж"})
    assert response.status_code == 201
    conversation = response.json()
    page = (await client.get("/api/v1/conversations?limit=1")).json()
    assert page["items"][0]["id"] == conversation["id"]
    messages = await client.get(f'/api/v1/conversations/{conversation["id"]}/messages')
    assert messages.json()["items"] == []
    assert not (store.directory / "runs/calls.jsonl").exists()


async def test_workspace_and_registered_file_are_operator_only_structured_refs(client, tmp_path):
    from corel_agent.contracts import new_id
    root_id = new_id()
    response = await client.put("/api/v1/workspace", json={"output_roots": [{"id": root_id, "path": str(tmp_path)}]})
    assert response.status_code == 200
    assert (await client.get("/api/v1/workspace")).json()["output_roots"][0]["id"] == root_id
    path = tmp_path / "пример.cdr"
    path.write_bytes(b"fixture")
    response = await client.post("/api/v1/files/register", json={"path": str(path)})
    assert response.status_code == 201
    assert set(response.json()) == {"file_ref", "display_name"}
    assert response.json()["display_name"] == path.name
    denied = await client.put("/api/v1/workspace", json={"output_roots": [{"id": root_id, "path": str(tmp_path / "not-created")}]})
    assert denied.status_code == 403 and not (tmp_path / "not-created").exists()


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


async def test_profile_native_test_does_not_call_corel(client, runtime, store, monkeypatch):
    from corel_agent.contracts import Completion, NativeToolCall
    class Probe:
        def __init__(self, profile, secret, **kwargs):
            pass
        async def complete(self, history, tools, **kwargs):
            assert [tool.name for tool in tools] == ["corel_agent_probe"]
            return Completion(tool_calls=(NativeToolCall(id="probe", name="corel_agent_probe",
                                                         arguments={"ok": True}),))
        async def aclose(self):
            pass
    monkeypatch.setattr(implementation("app"), "Provider", Probe, raising=False)
    item = profile()
    await client.put(f"/api/v1/profiles/{item.id}", json={"profile": item.model_dump(), "api_key": "fake"})
    result = await client.post(f"/api/v1/profiles/{item.id}/test", json={})
    assert result.status_code == 200 and result.json()["native_tools"] is True
    assert not (store.directory / "runs/calls.jsonl").exists()


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
