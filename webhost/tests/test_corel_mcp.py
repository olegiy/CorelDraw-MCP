from conftest import implementation
from fixtures.session import ScriptedSession
from test_assets import png

from corel_agent.contracts import PreparedOperation, new_id


def operation(tool="corel_execute_vba"):
    return PreparedOperation(operation_id=new_id(), run_id=new_id(), tool=tool,
                             arguments={"document_id": "fixture"}, document_id="fixture",
                             digest="0" * 64)


async def test_job_running_then_success():
    session = ScriptedSession()
    mcp = implementation("corel_mcp").CorelMcp(session=session, poll_interval=0.01)
    updates = []
    async def emit(update):
        updates.append(update)
    result = await mcp.call_job(operation(), emit)
    assert result.outcome == "succeeded" and result.applied is True
    assert [update.status for update in updates] == ["running", "succeeded"]
    assert session.calls[0][0] == "corel_execute_vba"
    assert all(name == "corel_get_job" for name, _ in session.calls[1:])


async def test_timed_out_then_success():
    session = ScriptedSession(("running", "timed_out", "succeeded"))
    mcp = implementation("corel_mcp").CorelMcp(session=session, poll_interval=0.01)
    updates = []
    async def emit(update):
        updates.append(update.status)
    result = await mcp.call_job(operation(), emit)
    assert result.outcome == "succeeded" and "timed_out" in updates
    assert sum(name == "corel_execute_vba" for name, _ in session.calls) == 1


async def test_cleanup_failure_not_retried():
    session = ScriptedSession(("succeeded",), cleanup={"completed": False, "errors": [{"step": "fixture"}]})
    mcp = implementation("corel_mcp").CorelMcp(session=session, poll_interval=0.01)
    async def emit(update):
        pass
    result = await mcp.call_job(operation(), emit)
    assert result.outcome == "succeeded" and result.cleanup["completed"] is False
    assert result.isError is True and len(session.calls) == 1


async def test_transport_loss_before_job_id_uncertain():
    session = ScriptedSession(fail_dispatch=True)
    mcp = implementation("corel_mcp").CorelMcp(session=session)
    async def emit(update):
        raise AssertionError("No fabricated job binding")
    result = await mcp.call_job(operation(), emit)
    assert result.outcome == "unknown" and result.job_id is None and result.applied is None
    assert "canary" not in str(result.report)
    assert len(session.calls) == 1


async def test_poll_timeout_is_monitoring_degraded():
    session = ScriptedSession(fail_poll=True)
    mcp = implementation("corel_mcp").CorelMcp(session=session, poll_interval=0.01)
    updates = []
    async def emit(update):
        updates.append(update)
    result = await mcp.call_job(operation(), emit)
    assert result.outcome == "succeeded"
    assert any(update.monitoring_state == "degraded" for update in updates)
    assert updates[-1].monitoring_state == "healthy"


async def test_preview_image_from_get_job():
    session = ScriptedSession(("succeeded",), tool="corel_preview", image=png())
    mcp = implementation("corel_mcp").CorelMcp(session=session)
    async def emit(update):
        pass
    result = await mcp.call_job(operation("corel_preview"), emit)
    assert result.images[0].mime_type == "image/png"
    assert session.calls[-1][0] == "corel_get_job" and session.calls[-1][1]["include_preview"]
