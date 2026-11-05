import httpx
import pytest
from conftest import implementation
from test_manual_api import HarnessAccess


@pytest.mark.parametrize('path', ['/runs?offset=9223372036854775808', '/runs?offset=1.0',
    '/runs?offset=1e1', '/runs?limit=1.0', '/conversations/not-a-uuid/messages',
    '/runs?conversation_id=not-a-uuid'])
async def test_paging_rejects_invalid_integer_lexemes_and_ids(runtime, store, path):
    app = implementation('app').create_app(runtime=runtime, store=store, assets=runtime.assets, access=HarnessAccess())
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url='http://127.0.0.1:8878') as client:
        response = await client.get('/api/v1' + path)
        assert response.status_code == 422
        assert response.json()['code'] == 'SCHEMA_INVALID'
