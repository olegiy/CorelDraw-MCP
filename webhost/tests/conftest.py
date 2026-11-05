import importlib
import shutil
from pathlib import Path
from uuid import uuid4

import pytest

ROOT = Path(__file__).resolve().parents[2]


def implementation(module):
    name = f"corel_agent.{module}"
    assert importlib.util.find_spec(name) is not None, f"Missing implementation: {name}"
    return importlib.import_module(name)


@pytest.fixture
def transport_factory(tmp_path):
    async def factory(script=None, *, args=()):
        owned = implementation("owned_stdio")
        transport = owned.OwnedStdio(
            command=shutil.which("node"),
            args=[str(script or ROOT / "test/fixture-server.mjs"), *args],
            directory=tmp_path,
            env={"CORELDRAW_MCP_RUNS": str(tmp_path / "runs")},
        )
        await transport.start()
        return transport
    return factory


def command(**updates):
    from corel_agent.contracts import RunCommand
    values = dict(client_request_id=str(uuid4()), conversation_id=str(uuid4()),
                  kind="manual", tool="corel_status", read_only=True)
    return RunCommand(**{**values, **updates})


@pytest.fixture
async def store(tmp_path):
    store = implementation("store").Store(tmp_path)
    await store.open()
    try:
        yield store
    finally:
        await store.aclose()


async def wait_run(store, run_id, timeout=5):
    import asyncio

    from corel_agent.contracts import TERMINAL_STATUSES
    async def wait():
        while True:
            snapshot = await store.get_snapshot(run_id)
            if snapshot.status in TERMINAL_STATUSES:
                return snapshot
            await asyncio.sleep(0.02)
    return await asyncio.wait_for(wait(), timeout)


@pytest.fixture
async def runtime(store, transport_factory, tmp_path, request):
    mcp_module = implementation("corel_mcp")
    runtime_module = implementation("runtime")
    transport = await transport_factory(Path(__file__).parent / "fixtures/panel-server.mjs",
                                         args=("--slow", *getattr(request, "param", ())))
    mcp = mcp_module.CorelMcp(transport=transport, poll_interval=0.02)
    await mcp.start()
    assets = implementation("assets").Assets(tmp_path, store)
    runtime = runtime_module.Runtime(store, mcp, assets)
    try:
        yield runtime
    finally:
        await runtime.aclose()
