"""Explicit opt-in panel smoke; default collection skips this unless CORELDRAW_AGENT_LIVE=1."""

import os

import pytest

pytestmark = pytest.mark.live


@pytest.mark.skipif(os.environ.get('CORELDRAW_AGENT_LIVE') != '1', reason='CORELDRAW_AGENT_LIVE=1 required')
def test_live_panel_requires_explicit_operator_opt_in():
    # Deliberately no implicit launch: live execution needs temporary documents,
    # before/after inventory and a separately reviewed Windows environment.
    assert os.environ['CORELDRAW_AGENT_LIVE'] == '1'


@pytest.mark.skipif(os.environ.get('CORELDRAW_AGENT_LIVE') != '1', reason='CORELDRAW_AGENT_LIVE=1 required')
def test_live_panel_two_temporary_documents_contract():
    """Live harness entrypoint is intentionally explicit and never auto-starts Corel.

    The operator supplies a separately reviewed temporary-document fixture and
    invokes the real panel smoke command outside the default test collection.
    """
    fixture = os.environ.get('CORELDRAW_AGENT_LIVE_FIXTURE')
    assert fixture and fixture.endswith('.json')
