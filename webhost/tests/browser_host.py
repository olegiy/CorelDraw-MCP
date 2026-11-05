"""Test-only same-origin browser host. Cannot select real MCP, model, or Vault."""

import asyncio
import shutil
import sys
import tempfile
from contextlib import asynccontextmanager
from pathlib import Path

import uvicorn
from fastapi import FastAPI
from fastapi.responses import RedirectResponse
from fastapi.staticfiles import StaticFiles
from pydantic import SecretStr

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from corel_agent.access import Access  # noqa: E402
from corel_agent.app import create_app  # noqa: E402
from corel_agent.assets import Assets  # noqa: E402
from corel_agent.contracts import Completion, NativeToolCall, ProfileSnapshot, new_id  # noqa: E402
from corel_agent.corel_mcp import CorelMcp  # noqa: E402
from corel_agent.owned_stdio import OwnedStdio  # noqa: E402
from corel_agent.runtime import Runtime  # noqa: E402
from corel_agent.settings import Settings  # noqa: E402
from corel_agent.store import Store  # noqa: E402


class MemoryVault:
    def __init__(self):
        self.data = {}
    def get(self, key):
        return self.data.get(key)
    def set(self, key, value):
        self.data[key] = value


class FixtureProvider:
    def __init__(self):
        self.turn = 0
    async def complete(self, history, tools):
        await asyncio.sleep(1)
        self.turn += 1
        if self.turn == 1:
            if any(item.get('role') == 'user' and item.get('content') == 'fixture:cleanup-failure' for item in history):
                return Completion(tool_calls=(NativeToolCall(id=new_id(), name='corel_execute_vba', arguments={'code': 'cleanup fail'}),))
            return Completion(tool_calls=(NativeToolCall(id=new_id(), name='corel_preview', arguments={'target': 'page'}),))
        return Completion(content='Превью получено; операция завершена.')
    async def aclose(self):
        pass


async def main():
    root = Path(__file__).resolve().parents[2]
    temporary = tempfile.TemporaryDirectory(prefix='corel-agent-browser-')
    directory = Path(temporary.name)
    loop = asyncio.get_running_loop()
    previous_handler = loop.get_exception_handler()
    def exception_handler(loop, context):
        error = context.get('exception')
        # Chromium closes its HTTP socket while the Windows proactor callback
        # is still queued. This is a test-host teardown artifact, not app data.
        if isinstance(error, ConnectionResetError) and getattr(error, 'winerror', None) == 10054:
            return
        if previous_handler:
            previous_handler(loop, context)
        else:
            loop.default_exception_handler(context)
    loop.set_exception_handler(exception_handler)
    transport = OwnedStdio(command=shutil.which('node'), args=[str(root / 'webhost/tests/fixtures/panel-server.mjs'), '--slow'],
                           directory=directory, env={'CORELDRAW_MCP_RUNS': str(directory / 'runs')})
    mcp = CorelMcp(transport=transport, poll_interval=0.02)
    store = Store(directory)
    await store.open()
    await mcp.start()
    settings = Settings(directory, vault=MemoryVault())
    await settings.save(ProfileSnapshot(id=new_id(), name='Фиктивная модель', model='fixture',
        base_url='http://127.0.0.1:1/v1', local=True, context_approved=True), SecretStr('fixture-not-real'))
    runtime = Runtime(store, mcp, Assets(directory, store), settings=settings,
                      provider_factory=lambda frozen: FixtureProvider())
    access = Access('127.0.0.1:18880')
    @asynccontextmanager
    async def lifespan(app):
        try:
            yield
        finally:
            await runtime.aclose()
            await store.aclose()
    app = create_app(runtime=runtime, store=store, assets=runtime.assets, access=access, lifespan=lifespan)
    shell = FastAPI(lifespan=lifespan, docs_url=None, openapi_url=None,
                    telemetry={'tracing': False, 'metrics': False, 'logs': False})
    @shell.get('/')
    async def entry():
        return RedirectResponse('/panel/#bootstrap=' + access.issue_bootstrap())
    app.mount('/panel', StaticFiles(directory=root / 'webui/dist', html=True), name='test-panel')
    app.mount('/assets', StaticFiles(directory=root / 'webui/dist/assets'), name='test-assets')
    shell.mount('/', app)
    try:
        await uvicorn.Server(uvicorn.Config(shell, host='127.0.0.1', port=18880,
            access_log=False, log_config=None, timeout_graceful_shutdown=None)).serve()
    finally:
        await runtime.aclose()
        await store.aclose()
        temporary.cleanup()


if __name__ == '__main__':
    asyncio.run(main())
