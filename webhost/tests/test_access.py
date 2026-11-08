import httpx
import pytest
from conftest import command, implementation

ORIGIN = "http://127.0.0.1:8878"


@pytest.fixture
async def authenticated(runtime, store):
    controller = implementation("access").Access()
    app = implementation("app").create_app(runtime=runtime, store=store, assets=runtime.assets,
                                            access=controller)
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url=ORIGIN,
                                headers={"Origin": ORIGIN, "Sec-Fetch-Site": "same-origin"}) as client:
        token = controller.issue_bootstrap()
        response = await client.post("/api/v1/access/bootstrap", headers={"X-Corel-Bootstrap": token}, json={})
        assert response.status_code == 200
        csrf = response.json()["csrf_token"]
        client.headers["X-Corel-CSRF"] = csrf
        yield controller, client, response, token


async def test_bootstrap_once_and_cookie_contract(authenticated):
    controller, client, response, token = authenticated
    cookie = response.headers["set-cookie"].lower()
    assert "corel_agent_session=" in cookie and "httponly" in cookie
    assert "samesite=strict" in cookie and "path=/api/v1" in cookie
    assert "max-age" not in cookie and "expires=" not in cookie and "secure" not in cookie
    assert token not in response.text and token not in cookie
    replay = await client.post("/api/v1/access/bootstrap", headers={"X-Corel-Bootstrap": token}, json={})
    assert replay.status_code == 401 and token not in replay.text
    session = await client.get("/api/v1/access/session")
    assert session.status_code == 200 and session.json()["csrf_token"] == client.headers["X-Corel-CSRF"]


async def test_origin_host_csrf_and_unauthenticated_sse_denied(authenticated, runtime, store):
    controller, client, _, _ = authenticated
    for headers in [
        {"Origin": "https://attacker.example"}, {"Host": "localhost:8878"},
        {"Sec-Fetch-Site": "cross-site"}, {"X-Corel-CSRF": "wrong"},
    ]:
        response = await client.post("/api/v1/actions", json=command().model_dump(), headers=headers)
        assert response.status_code == 403
    original = client.headers.pop("Origin")
    assert (await client.post("/api/v1/actions", json=command().model_dump())).status_code == 403
    client.headers["Origin"] = original
    original = client.headers.pop("X-Corel-CSRF")
    assert (await client.post("/api/v1/actions", json=command().model_dump())).status_code == 403
    client.headers["X-Corel-CSRF"] = original
    app = implementation("app").create_app(runtime=runtime, store=store, assets=runtime.assets, access=controller)
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url=ORIGIN) as anonymous:
        assert (await anonymous.get("/api/v1/runs/anything/events")).status_code == 401
        assert (await anonymous.get("/api/v1/images/anything")).status_code == 401
    assert await store.list_runs() == []


async def test_restart_invalidates_old_cookie(authenticated, runtime, store):
    _, client, _, _ = authenticated
    app = implementation("app").create_app(runtime=runtime, store=store, assets=runtime.assets,
                                            access=implementation("access").Access())
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url=ORIGIN,
                                cookies=client.cookies) as fresh:
        assert (await fresh.get("/api/v1/access/session")).status_code == 401


async def test_body_limit_checks_chunks_without_content_length(authenticated, store):
    _, client, _, _ = authenticated
    async def body():
        for _ in range(33):
            yield b" " * 65536
    response = await client.post("/api/v1/actions", content=body(), headers={"Content-Type": "application/json"})
    assert response.status_code == 413
    assert await store.list_runs() == []


async def test_security_headers_and_no_path_query(authenticated):
    _, client, _, _ = authenticated
    response = await client.get("/api/v1/health")
    assert response.headers["cache-control"] == "no-store"
    assert response.headers["referrer-policy"] == "no-referrer"
    assert response.headers["x-content-type-options"] == "nosniff"
    policy = response.headers["content-security-policy"]
    assert "frame-ancestors 'none'" in policy and "base-uri 'none'" in policy
    assert "unsafe-inline" not in policy
    response = await client.get("/api/v1/images/anything?path=C:/private.cdr")
    assert response.status_code == 403


async def test_permission_booleans_are_not_coerced_from_strings(authenticated, store):
    _, client, _, _ = authenticated
    payload = command().model_dump()
    payload["read_only"] = "false"
    response = await client.post("/api/v1/actions", json=payload)
    assert response.status_code == 422
    assert await store.list_runs() == []


async def test_malformed_csrf_and_extreme_content_length_fail_closed(authenticated):
    _, client, _, _ = authenticated
    response = await client.post("/api/v1/actions", json=command().model_dump(),
                                 headers={"X-Corel-CSRF": b"\xff" * 43})
    assert response.status_code == 403
    response = await client.post("/api/v1/actions", json=command().model_dump(),
                                 headers={"Content-Length": "9" * 5000})
    assert response.status_code == 413


async def test_global_telemetry_cannot_observe_protected_requests(monkeypatch):
    from fastapi.telemetry import _asgi
    class UnexpectedProvider:
        def get_tracer(self, *args, **kwargs):
            raise AssertionError("Implicit request telemetry is forbidden")
    monkeypatch.setattr(_asgi.trace, "get_tracer_provider", lambda: UnexpectedProvider())
    app = implementation("app").create_app()
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url=ORIGIN) as client:
        response = await client.get("/api/v1/health", headers={"Authorization": "CANARY-PRIVATE"})
    assert response.status_code == 401 and "CANARY-PRIVATE" not in response.text


async def test_confirmation_endpoint_is_removed_but_access_controls_remain(authenticated, runtime, store):
    from test_persisted import legacy_confirmation
    _, client, _, _ = authenticated
    old = await legacy_confirmation(store)
    url = f"/api/v1/runs/{old['id']}/confirm"
    payload = {'confirmation_id': old['confirmation']['confirmation_id'], 'operation_digest': '0' * 64, 'approved': True}
    assert (await client.post(url, json=payload, headers={'X-Corel-CSRF': 'wrong'})).status_code == 403
    assert '/api/v1/runs/{run_id}/confirm' not in implementation('app').create_app().openapi()['paths']
    assert (await client.post(url, json=payload)).status_code == 404
