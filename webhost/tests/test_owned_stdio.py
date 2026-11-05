import asyncio
import json
from pathlib import Path

import pytest
from conftest import implementation


async def test_long_child_is_not_killed_on_graceful_close(transport_factory, tmp_path):
    script = Path(__file__).parent / "fixtures/long-child.mjs"
    transport = await transport_factory(script)
    result = await transport.session.call_tool("corel_execute_vba", {
        "document_id": "fixture", "request_id": "long-child", "code": "delay",
    })
    assert result.structuredContent["status"] == "running"
    await transport.aclose()
    report = json.loads(next((tmp_path / "runs").glob("*/report.json")).read_text())
    assert report["status"] == "succeeded"
    assert transport.returncode == 0
    assert not (tmp_path / "mcp-owner.json").exists()


async def test_cancelled_subscriber_does_not_close_mcp(transport_factory):
    transport = await transport_factory()
    try:
        result = await transport.session.call_tool("corel_execute_vba", {
            "document_id": "fixture", "request_id": "cancel-read", "code": "delay",
        })
        job_id = result.structuredContent["job_id"]
        read = asyncio.create_task(transport.session.call_tool("corel_get_job", {
            "job_id": job_id, "wait_seconds": 1,
        }))
        await asyncio.sleep(0.05)
        read.cancel()
        with pytest.raises(asyncio.CancelledError):
            await read
        result = await transport.session.call_tool("corel_get_job", {"job_id": job_id})
        assert result.structuredContent["job_id"] == job_id
        assert transport.returncode is None
    finally:
        await transport.aclose()


def test_spawn_pending_blocks_second_owner(tmp_path):
    owned = implementation("owned_stdio")
    first = owned.OwnerMarker(tmp_path / "mcp-owner.json")
    first.reserve()
    second = owned.OwnerMarker(tmp_path / "mcp-owner.json")
    with pytest.raises(owned.LifecycleError) as error:
        second.reserve()
    assert error.value.code == "OWNERSHIP_UNCERTAIN"
    assert json.loads(first.path.read_text())["phase"] == "spawn_pending"


async def test_direct_transport_close_survives_repeated_cancellation(transport_factory, tmp_path):
    transport = await transport_factory(Path(__file__).parent / 'fixtures/long-child.mjs')
    await transport.session.call_tool('corel_execute_vba', {'document_id': 'fixture',
        'request_id': 'double-cancel-close', 'code': 'delay'})
    close = asyncio.create_task(transport.aclose())
    await asyncio.sleep(0.02)
    close.cancel()
    await asyncio.sleep(0.02)
    close.cancel()
    await asyncio.sleep(0.02)
    try:
        assert not close.done()
    finally:
        await asyncio.shield(transport._task)
    with pytest.raises(asyncio.CancelledError):
        await close
    assert transport.returncode == 0 and not (tmp_path / 'mcp-owner.json').exists()


async def test_proven_missing_executable_releases_only_own_spawn_marker(tmp_path):
    owned = implementation('owned_stdio')
    transport = owned.OwnedStdio(command=str(tmp_path / 'missing-fixture.exe'), args=[], directory=tmp_path)
    with pytest.raises(owned.LifecycleError) as caught:
        await transport.start()
    assert caught.value.code == 'MCP_START_FAILED'
    await transport.aclose()
    assert transport.process is None and not transport.marker.path.exists()
