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
