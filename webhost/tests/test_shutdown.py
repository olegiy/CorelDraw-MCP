import asyncio

import pytest
from conftest import command, wait_run


async def test_repeated_shutdown_cancellation_waits_for_native_drain(runtime, store):
    run = await runtime.start(command())
    for _ in range(200):
        if (await store.get_snapshot(run.id)).job_id:
            break
        await asyncio.sleep(0.01)
    closing = asyncio.create_task(runtime.aclose())
    await asyncio.sleep(0.02)
    closing.cancel()
    await asyncio.sleep(0.02)
    closing.cancel()
    await asyncio.sleep(0.02)
    assert not closing.done() and not runtime._shutdown_task.done()
    with pytest.raises(asyncio.CancelledError):
        await closing
    assert (await wait_run(store, run.id)).status == "stopped"
    assert runtime.mcp.transport.returncode == 0
