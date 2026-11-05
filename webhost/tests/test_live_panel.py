"""Explicit opt-in panel smoke; default collection skips this unless CORELDRAW_AGENT_LIVE=1."""

import os

import pytest

pytestmark = pytest.mark.live


@pytest.mark.skipif(os.environ.get('CORELDRAW_AGENT_LIVE') != '1', reason='CORELDRAW_AGENT_LIVE=1 required')
def test_live_panel_two_temporary_documents_contract():
    """Live harness entrypoint is intentionally explicit and never auto-starts Corel.

    The operator supplies a separately reviewed temporary-document fixture and
    invokes the real panel smoke command outside the default test collection.
    """
    pytest.skip('Настоящий live smoke панели ещё не реализован: нужны два временных документа, '
                'фактические операции, before/after inventory и проверенные отчёты; M3 не подтверждён.')
