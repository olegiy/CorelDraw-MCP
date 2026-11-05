from conftest import implementation
from fixtures.session import ScriptedSession

from corel_agent.contracts import PreparedOperation, new_id


def op():
    return PreparedOperation(operation_id=new_id(), run_id=new_id(), tool="corel_execute_vba",
                             arguments={"code": "fixture"}, document_id="fixture", digest="0" * 64)


async def test_incompatible_initial_status_is_unknown_not_failed():
    session = ScriptedSession(("incompatible",))
    mcp = implementation("corel_mcp").CorelMcp(session=session)
    result = await mcp.call_job(op(), lambda update: None)
    assert result.outcome == "unknown" and result.applied is None


async def test_persistence_failure_blocks_following_mutation():
    session = ScriptedSession(("succeeded",))
    mcp = implementation("corel_mcp").CorelMcp(session=session)
    async def fail(_):
        raise RuntimeError("ledger unavailable")
    result = await mcp.call_job(op(), fail)
    assert result.outcome == "succeeded"
    assert mcp.persistence_failed is True
    import pytest

    from corel_agent.contracts import AgentError
    with pytest.raises(AgentError) as caught:
        await mcp.call_job(op(), fail)
    assert caught.value.code == "PERSISTENCE_FAILED" and len(session.calls) == 1


async def test_verified_dead_owned_transport_ends_monitoring_as_unknown_without_replay():
    session = ScriptedSession(("running",), fail_poll=True)
    class DeadTransport:
        returncode = 1
    mcp = implementation("corel_mcp").CorelMcp(session=session, transport=DeadTransport(), poll_interval=0.01)
    updates = []
    async def emit(update):
        updates.append(update)
    import asyncio
    result = await asyncio.wait_for(mcp.call_job(op(), emit), 0.5)
    assert result.outcome == "unknown" and result.job_id == session.base['job_id']
    assert result.report['error']['code'] == 'MCP_PROCESS_EXITED'
    assert mcp.ready is False
    assert sum(name == 'corel_execute_vba' for name, _ in session.calls) == 1
