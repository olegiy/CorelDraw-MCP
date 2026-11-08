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
        base_url="http://127.0.0.1:1234/v1", local=True), **updates})


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


async def test_remote_profile_freezes_without_context_consent(tmp_path):
    settings = implementation("settings").Settings(tmp_path, vault=FakeVault())
    saved = await settings.save(profile(base_url="https://example.test/v1", local=False), SecretStr("fixture-key"))
    assert (await settings.freeze(saved.id)).profile.base_url == "https://example.test/v1"


async def test_schema_one_profile_migrates_legacy_flags_without_changing_vault(tmp_path):
    vault = FakeVault()
    profile_id = new_id()
    revision = new_id()
    vault.set(revision, "fixture-key")
    (tmp_path / "profiles.json").write_text(json.dumps({"schema_version": 1, "profiles": [{
        "id": profile_id, "name": "legacy", "base_url": "http://127.0.0.1:1234/v1", "model": "fixture",
        "local": True, "context_approved": True, "vision": True, "send_preview": True,
        "stream_responses": True, "credential_revision": revision,
    }]}), encoding="utf-8")
    settings = implementation("settings").Settings(tmp_path, vault=vault)
    original_bytes = (tmp_path / "profiles.json").read_bytes()
    loaded = await settings.list_profiles()
    assert not hasattr(loaded[0], "context_approved")
    assert (await settings.freeze(profile_id)).secret.get_secret_value() == "fixture-key"
    assert vault.values == {revision: "fixture-key"}
    saved = await settings.save(loaded[0])
    assert json.loads((tmp_path / "profiles.json").read_text())['schema_version'] == 2
    assert saved.credential_revision == revision
    backup = tmp_path / "profiles.schema-1.backup.json"
    assert backup.read_bytes() == original_bytes
    assert "fixture-key" not in backup.read_text()
    await settings.save(saved.model_copy(update={"model": "second-save"}))
    assert backup.read_bytes() == original_bytes
    assert vault.values == {revision: "fixture-key"}


@pytest.mark.parametrize("version,extra", [(1, {"unknown": True}), (1, {"vision": "yes"}),
                                          (2, {"vision": True}), (True, {})])
async def test_profile_migration_never_ignores_unknown_or_malformed_fields(tmp_path, version, extra):
    path = tmp_path / "profiles.json"
    path.write_text(json.dumps({"schema_version": version,
                               "profiles": [{**profile().model_dump(), **extra}]}))
    original = path.read_bytes()
    settings = implementation("settings").Settings(tmp_path, vault=FakeVault())
    with pytest.raises(AgentError) as caught:
        await settings.list_profiles()
    assert caught.value.code == "SETTINGS_UNAVAILABLE"
    assert path.read_bytes() == original


async def test_local_flag_cannot_label_remote_provider_as_local(tmp_path):
    settings = implementation("settings").Settings(tmp_path, vault=FakeVault())
    with pytest.raises(AgentError) as caught:
        await settings.save(profile(base_url="https://example.test/v1", local=True), SecretStr("fixture-key"))
    assert caught.value.code == "LOCAL_PROVIDER_INVALID"


@pytest.fixture
def legacy_settings(tmp_path):
    vault = FakeVault()
    saved = profile(credential_revision=new_id())
    vault.set(saved.credential_revision, 'fixture-backup-key')
    original = json.dumps({'schema_version': 1, 'profiles': [saved.model_dump()]}).encode()
    (tmp_path / 'profiles.json').write_bytes(original)
    return implementation('settings').Settings(tmp_path, vault=vault), saved, original


async def test_failed_backup_flush_does_not_leave_a_false_completed_backup(legacy_settings, tmp_path, monkeypatch):
    settings, saved, original = legacy_settings
    module = implementation('settings')
    def fail(fd):
        raise OSError('Synthetic backup flush failure')
    with monkeypatch.context() as scoped:
        scoped.setattr(module.os, 'fsync', fail)
        with pytest.raises(AgentError):
            await settings.save(saved)
    backup = tmp_path / 'profiles.schema-1.backup.json'
    assert not backup.exists() and settings.path.read_bytes() == original
    await settings.save(saved)
    assert backup.read_bytes() == original


async def test_incomplete_existing_backup_never_allows_schema_replacement(legacy_settings, tmp_path):
    settings, saved, original = legacy_settings
    backup = tmp_path / 'profiles.schema-1.backup.json'
    backup.write_bytes(original[:8])
    with pytest.raises(AgentError):
        await settings.save(saved)
    assert settings.path.read_bytes() == original and backup.read_bytes() == original[:8]
