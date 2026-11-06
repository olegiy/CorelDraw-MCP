"""Explicit opt-in panel smoke; default collection skips this unless CORELDRAW_AGENT_LIVE=1."""

import asyncio
import os

import pytest

pytestmark = pytest.mark.live


@pytest.mark.skipif(os.environ.get('CORELDRAW_AGENT_LIVE') != '1', reason='CORELDRAW_AGENT_LIVE=1 required')
def test_live_panel_two_temporary_documents_contract():
    """Only an explicit opt-in and reviewed manifest can start the native smoke.

    The operator supplies a separately reviewed temporary-document fixture and
    invokes the real panel smoke command outside the default test collection.
    """
    from panel_smoke import load_fixture, run_smoke

    from corel_agent.contracts import AgentError

    if os.environ.get('CORELDRAW_AGENT_LIVE') != '1':
        pytest.skip('CORELDRAW_AGENT_LIVE=1 required')
    path = os.environ.get('CORELDRAW_AGENT_LIVE_FIXTURE')
    if not path:
        raise AgentError('LIVE_FIXTURE_INVALID', 'Укажите проверенный CORELDRAW_AGENT_LIVE_FIXTURE.', 403)
    fixture = load_fixture(path)
    proof = asyncio.run(run_smoke(fixture))
    print(f"Отчёт live smoke: {proof['directory']}\\verification.json")
    assert proof['ok'], f"Live smoke не прошёл: {proof['failure_code']}; отчёт: {proof['directory']}"
