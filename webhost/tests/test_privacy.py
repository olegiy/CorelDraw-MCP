import pytest
from conftest import implementation


def test_quotes_unicode_and_multiline_text_are_not_rewritten():
    privacy = implementation("privacy").Privacy()
    text = 'Привет "мир"\nВторая строка {}'
    assert privacy.for_model(text) == text


@pytest.mark.parametrize("alias", ["@output/file.cdr", "@file/fixture", "@path/fixture", "<локальный путь>"])
def test_text_alias_is_not_rewritten_or_blocked_by_privacy_policy(alias):
    privacy = implementation("privacy").Privacy()
    assert privacy.for_model(alias) == alias


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


def test_secret_detection_covers_nested_operation_arguments():
    privacy = implementation("privacy").Privacy(secrets=("CANARY-CREDENTIAL-123",))
    assert privacy.contains_secret({'updates': [{'text': 'CANARY-CREDENTIAL-123'}]})
    assert not privacy.contains_secret({'updates': [{'text': 'Без секрета'}]})
