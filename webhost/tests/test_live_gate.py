import importlib

import pytest


def test_placeholder_live_fixture_cannot_claim_smoke_success(monkeypatch):
    monkeypatch.setenv('CORELDRAW_AGENT_LIVE', '1')
    monkeypatch.setenv('CORELDRAW_AGENT_LIVE_FIXTURE', 'nonexistent-placeholder.json')
    module = importlib.import_module('test_live_panel')
    with pytest.raises(pytest.skip.Exception):
        module.test_live_panel_two_temporary_documents_contract()
