import importlib

import pytest


def test_missing_live_fixture_is_rejected_before_any_corel_start(monkeypatch):
    from corel_agent.contracts import AgentError

    monkeypatch.setenv('CORELDRAW_AGENT_LIVE', '1')
    monkeypatch.setenv('CORELDRAW_AGENT_LIVE_FIXTURE', 'nonexistent-placeholder.json')
    module = importlib.import_module('test_live_panel')
    with pytest.raises(AgentError) as caught:
        module.test_live_panel_two_temporary_documents_contract()
    assert caught.value.code == 'LIVE_FIXTURE_INVALID'
