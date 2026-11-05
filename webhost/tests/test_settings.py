import json

import pytest
from conftest import implementation
from pydantic import SecretStr

from corel_agent.contracts import AgentError, ProfileSnapshot, new_id


class FakeVault:
    def __init__(self):
        self.values = {}
        self.unavailable = False

    def get(self, revision):
        if self.unavailable:
            raise OSError("CANARY vault error")
        return self.values.get(revision)

    def set(self, revision, value):
        if self.unavailable:
            raise OSError("CANARY vault error")
        self.values[revision] = value


def profile(**updates):
    return ProfileSnapshot(**{**dict(id=new_id(), name="Тестовая модель", model="fixture",
        base_url="http://127.0.0.1:1234/v1", local=True, context_approved=True), **updates})


async def test_frozen_profile_and_credential_survive_settings_change(tmp_path):
    vault = FakeVault()
    settings = implementation("settings").Settings(tmp_path, vault=vault)
    original = await settings.save(profile(), SecretStr("CANARY-old-key"))
    frozen = await settings.freeze(original.id)
    await settings.save(original.model_copy(update={"model": "changed"}), SecretStr("CANARY-new-key"))
    assert frozen.profile.model == "fixture"
    assert frozen.secret.get_secret_value() == "CANARY-old-key"
    assert "CANARY" not in repr(frozen)
    assert "CANARY" not in (tmp_path / "profiles.json").read_text(encoding="utf-8")
    assert (await settings.freeze(original.id)).profile.model == "changed"


async def test_credential_store_failure_has_no_file_or_env_fallback(tmp_path, monkeypatch):
    vault = FakeVault()
    settings = implementation("settings").Settings(tmp_path, vault=vault)
    saved = await settings.save(profile(), SecretStr("CANARY-private-key"))
    vault.unavailable = True
    monkeypatch.setenv("OPENAI_API_KEY", "CANARY-env-key")
    with pytest.raises(AgentError) as caught:
        await settings.freeze(saved.id)
    assert caught.value.code == "VAULT_UNAVAILABLE"
    assert "CANARY" not in str(caught.value)
    with pytest.raises(AgentError):
        await settings.save(saved, SecretStr("CANARY-new-key"))
    assert "CANARY" not in (tmp_path / "profiles.json").read_text(encoding="utf-8")


async def test_profile_metadata_roundtrip_without_credentials(tmp_path):
    module = implementation("settings")
    vault = FakeVault()
    settings = module.Settings(tmp_path, vault=vault)
    saved = await settings.save(profile(), SecretStr("fixture-key"))
    reopened = module.Settings(tmp_path, vault=vault)
    assert (await reopened.list_profiles()) == [saved]
    assert (await reopened.freeze(saved.id)).secret.get_secret_value() == "fixture-key"
    assert module.VAULT_SERVICE == "coreldraw_agent"


async def test_corrupt_profile_file_is_preserved(tmp_path):
    path = tmp_path / "profiles.json"
    path.write_text('{"schema_version":999}', encoding="utf-8")
    settings = implementation("settings").Settings(tmp_path, vault=FakeVault())
    with pytest.raises(AgentError) as caught:
        await settings.list_profiles()
    assert caught.value.code == "SETTINGS_UNAVAILABLE"
    assert json.loads(path.read_text())["schema_version"] == 999


async def test_remote_context_requires_explicit_consent(tmp_path):
    settings = implementation("settings").Settings(tmp_path, vault=FakeVault())
    saved = await settings.save(profile(base_url="https://example.test/v1", local=False,
                                         context_approved=False), SecretStr("fixture-key"))
    with pytest.raises(AgentError) as caught:
        await settings.freeze(saved.id)
    assert caught.value.code == "CONTEXT_CONSENT_REQUIRED"


async def test_local_flag_cannot_label_remote_provider_as_local(tmp_path):
    settings = implementation("settings").Settings(tmp_path, vault=FakeVault())
    with pytest.raises(AgentError) as caught:
        await settings.save(profile(base_url="https://example.test/v1", local=True), SecretStr("fixture-key"))
    assert caught.value.code == "LOCAL_PROVIDER_INVALID"
