import json

import pytest

from corel_agent.contracts import AgentError


async def test_transport_owner_check_binds_token_identity_and_phase(transport_factory):
    transport = await transport_factory()
    try:
        await transport.verify_owner()
        original = transport.marker.path.read_text(encoding='utf-8')
        modified = json.loads(original)
        modified['phase'] = 'spawn_pending'
        transport.marker.path.write_text(json.dumps(modified), encoding='utf-8')
        try:
            with pytest.raises(AgentError):
                await transport.verify_owner()
        finally:
            transport.marker.path.write_text(original, encoding='utf-8')
    finally:
        await transport.aclose()


async def test_mismatched_marker_blocks_dispatch_before_native_send(runtime, store):
    from conftest import command
    original = runtime.mcp.transport.marker.path.read_text(encoding='utf-8')
    payload = json.loads(original)
    payload['token'] = 'not-the-owner'
    runtime.mcp.transport.marker.path.write_text(json.dumps(payload), encoding='utf-8')
    try:
        with pytest.raises(AgentError) as caught:
            await runtime.start(command())
        assert caught.value.code == 'OWNERSHIP_UNCERTAIN'
        assert not (store.directory / 'runs/calls.jsonl').exists()
    finally:
        runtime.mcp.transport.marker.path.write_text(original, encoding='utf-8')
