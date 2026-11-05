import httpx
from conftest import command, implementation, wait_run


class HarnessAccess:
    """Only an in-process ASGI test harness may inject this implementation."""
    async def check(self, request):
        pass


async def test_manual_api_accepts_after_commit_and_serves_only_registered_png(runtime, store):
    app = implementation("app").create_app(runtime=runtime, store=store, assets=runtime.assets,
                                            access=HarnessAccess())
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://127.0.0.1:8878") as client:
        response = await client.post("/api/v1/actions", json=command().model_dump())
        assert response.status_code == 202
        run = response.json()
        assert (await store.get_snapshot(run["id"])).last_seq >= 1
        await wait_run(store, run["id"])
        docs = (await client.get("/api/v1/documents")).json()
        assert len(docs["items"]) == 2
        response = await client.post("/api/v1/actions", json=command(tool="corel_preview", document_id="fixture-b").model_dump())
        await wait_run(store, response.json()["id"])
        events = (await client.get(f'/api/v1/runs/{response.json()["id"]}/event-log')).json()["items"]
        image = next(item["data"] for item in events if item["type"] == "image")
        response = await client.get(f'/api/v1/images/{image["image_id"]}')
        assert response.headers["content-type"] == "image/png" and response.content.startswith(b"\x89PNG")
        assert (await client.get("/api/v1/images/not-an-id?path=C:/private.cdr")).status_code == 404


async def test_default_api_is_closed_and_validation_does_not_echo_input(runtime, store):
    factory = implementation("app").create_app
    closed = factory(runtime=runtime, store=store, assets=runtime.assets)
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=closed), base_url="http://127.0.0.1:8878") as client:
        assert (await client.get("/api/v1/documents")).status_code == 401
        assert (await client.get("/api/v1/runs/anything/events")).status_code == 401
        assert (await client.get("/openapi.json")).status_code == 404
    app = factory(runtime=runtime, store=store, assets=runtime.assets, access=HarnessAccess())
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://127.0.0.1:8878") as client:
        response = await client.post("/api/v1/actions", json={"api_key": "SECRET-CANARY"})
        assert response.status_code == 422 and "SECRET-CANARY" not in response.text
