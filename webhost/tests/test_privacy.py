import pytest
from conftest import implementation

from corel_agent.contracts import AgentError


def test_quotes_unicode_and_multiline_vba_are_not_rewritten():
    privacy = implementation("privacy").Privacy()
    code = 'Function Main() As String\n Dim text As String\n text = "Привет ""мир"""\n Main = "{}"\nEnd Function'
    assert privacy.validate_vba(code) == code


@pytest.mark.parametrize("alias", ["@output/file.cdr", "@file/fixture", "@path/fixture", "<локальный путь>"])
def test_vba_alias_is_not_rewritten_or_blocked_by_permission_policy(alias):
    privacy = implementation("privacy").Privacy()
    code = f'Function Main() As String\n Main = "{alias}"\nEnd Function'
    assert privacy.validate_vba(code) == code


def test_model_context_preserves_paths_and_masks_known_secrets():
    privacy = implementation("privacy").Privacy(secrets=("CANARY-CREDENTIAL-123",))
    report = {"document_id": "fixture-b", "static_id": 1001,
              "path": "C:\\Приватные рисунки\\example.cdr",
              "text": 'Строка "C:\\secret\\a.cdr"\nCANARY-CREDENTIAL-123',
              "error": {"applied": True}, "cleanup": {"completed": False}}
    masked = privacy.for_model(report)
    assert masked['path'] == report['path'] and "CANARY-CREDENTIAL-123" not in str(masked)
    assert masked["document_id"] == "fixture-b" and masked["static_id"] == 1001
    assert masked["error"]["applied"] is True and masked["cleanup"]["completed"] is False


def test_known_secret_in_vba_is_denied_not_silently_rewritten():
    privacy = implementation("privacy").Privacy(secrets=("CANARY-CREDENTIAL-123",))
    with pytest.raises(AgentError) as caught:
        privacy.validate_vba('Main = "CANARY-CREDENTIAL-123"')
    assert caught.value.code == "SECRET_IN_OPERATION"
