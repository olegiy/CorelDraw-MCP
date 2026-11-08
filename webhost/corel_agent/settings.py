"""One operator profile. Only Windows Credential Manager stores credential values."""

import asyncio
import ipaddress
import os
from pathlib import Path
from threading import RLock
from urllib.parse import urlsplit

from pydantic import BaseModel, ConfigDict, Field, SecretStr, ValidationError

from .assets import assert_no_links
from .contracts import AgentError, ProfileSnapshot, new_id
from .owned_stdio import atomic_json
from .streaming import strict_json

VAULT_SERVICE = "coreldraw_agent"


class FrozenConnection(BaseModel):
    model_config = ConfigDict(frozen=True, extra="forbid")
    profile: ProfileSnapshot
    secret: SecretStr = Field(repr=False, exclude=True)


class WindowsVault:
    def __init__(self):
        from keyring.backends.Windows import WinVaultKeyring
        self.backend = WinVaultKeyring()

    def get(self, revision):
        return self.backend.get_password(VAULT_SERVICE, revision)

    def set(self, revision, value):
        self.backend.set_password(VAULT_SERVICE, revision, value)


def local_endpoint(url):
    hostname = urlsplit(url).hostname
    try:
        return ipaddress.ip_address(hostname).is_loopback
    except ValueError:
        return hostname == "localhost"


class Settings:
    def __init__(self, directory: Path, *, vault=None):
        self.path = Path(directory) / "profiles.json"
        self._vault = vault
        self._lock = RLock()
        self._legacy_metadata = None

    def _credentials(self):
        try:
            if self._vault is None:
                self._vault = WindowsVault()
            return self._vault
        except Exception:
            raise AgentError("VAULT_UNAVAILABLE", "Windows Credential Manager недоступен; fallback запрещён.", 503) from None

    def _read(self):
        try:
            self._legacy_metadata = None
            assert_no_links(self.path)
            if not self.path.exists():
                return []
            if not self.path.is_file() or self.path.stat().st_size > 65536:
                raise ValueError
            original = self.path.read_bytes()
            data = strict_json(original)
            if (not isinstance(data, dict) or set(data) != {"schema_version", "profiles"}
                    or type(data["schema_version"]) is not int or data["schema_version"] not in {1, 2}):
                raise ValueError
            if not isinstance(data["profiles"], list) or len(data["profiles"]) > 1:
                raise ValueError
            profiles = []
            for item in data["profiles"]:
                if data["schema_version"] == 1:
                    item = dict(item)
                    for field in ("context_approved", "vision", "send_preview"):
                        if field in item and type(item[field]) is not bool:
                            raise ValueError
                        item.pop(field, None)
                profiles.append(ProfileSnapshot.model_validate(item))
            if data["schema_version"] == 1:
                self._legacy_metadata = original
            return profiles
        except (ValueError, OSError, ValidationError, TypeError):
            raise AgentError("SETTINGS_UNAVAILABLE", "Профиль повреждён или несовместим; файл сохранён без изменений.", 503) from None

    async def list_profiles(self):
        def read():
            with self._lock:
                return self._read()
        return await asyncio.to_thread(read)

    async def save(self, profile: ProfileSnapshot, secret: SecretStr | None = None):
        def save():
            with self._lock:
                existing = self._read()
                old = next((item for item in existing if item.id == profile.id), None)
                if existing and old is None:
                    raise AgentError("PROFILE_LIMIT", "MVP поддерживает один профиль; измените существующий.")
                if profile.local and not local_endpoint(profile.base_url):
                    raise AgentError("LOCAL_PROVIDER_INVALID", "Локальным может быть только loopback API.", 422)
                revision = old.credential_revision if old else new_id()
                try:
                    vault = self._credentials()
                    if secret is not None:
                        value = secret.get_secret_value()
                        if len(value) > 4096 or any(ord(char) < 32 or ord(char) > 126 for char in value):
                            raise AgentError("CREDENTIAL_INVALID", "Нужен корректный API credential без управляющих символов.", 422)
                        revision = new_id()
                        vault.set(revision, value)
                    elif not old:
                        # Empty credentials for a local API still have an explicit vault entry.
                        vault.set(revision, "")
                    else:
                        if vault.get(revision) is None:
                            raise AgentError("CREDENTIAL_MISSING", "Credential профиля отсутствует в Windows Credential Manager.", 503)
                except AgentError:
                    raise
                except Exception:
                    raise AgentError("VAULT_UNAVAILABLE", "Windows Credential Manager недоступен; профиль не изменён.", 503) from None
                saved = profile.model_copy(update={"credential_revision": revision})
                assert_no_links(self.path.parent)
                self.path.parent.mkdir(parents=True, exist_ok=True)
                if self._legacy_metadata is not None:
                    backup = self.path.with_name("profiles.schema-1.backup.json")
                    assert_no_links(backup)
                    if backup.exists():
                        if (not backup.is_file() or backup.stat().st_size != len(self._legacy_metadata)
                                or backup.read_bytes() != self._legacy_metadata):
                            raise OSError('Existing legacy backup does not match the original metadata')
                    else:
                        created = None
                        try:
                            with backup.open("xb") as stream:
                                info = os.fstat(stream.fileno())
                                created = (info.st_dev, info.st_ino)
                                stream.write(self._legacy_metadata)
                                stream.flush()
                                os.fsync(stream.fileno())
                        except OSError:
                            # Never remove a replacement created by another writer.
                            try:
                                info = backup.stat()
                                if created is not None and (info.st_dev, info.st_ino) == created:
                                    backup.unlink()
                            except OSError:
                                pass
                            raise
                atomic_json(self.path, {"schema_version": 2, "profiles": [saved.model_dump(mode="json")]})
                return saved
        try:
            return await asyncio.to_thread(save)
        except OSError:
            raise AgentError("SETTINGS_UNAVAILABLE", "Не удалось сохранить метаданные профиля.", 503) from None

    async def freeze(self, profile_id):
        def freeze():
            with self._lock:
                profile = next((item for item in self._read() if item.id == profile_id), None)
                if profile is None:
                    raise AgentError("PROFILE_NOT_FOUND", "Выбранный профиль отсутствует.", 404)
                if profile.local and not local_endpoint(profile.base_url):
                    raise AgentError("LOCAL_PROVIDER_INVALID", "Loopback профиль имеет внешний адрес.", 422)
                try:
                    secret = self._credentials().get(profile.credential_revision)
                except Exception:
                    raise AgentError("VAULT_UNAVAILABLE", "Windows Credential Manager недоступен; запуск модели запрещён.", 503) from None
                if secret is None:
                    raise AgentError("CREDENTIAL_MISSING", "Credential профиля отсутствует; env/file fallback запрещён.", 503)
                return FrozenConnection(profile=profile, secret=SecretStr(secret))
        return await asyncio.to_thread(freeze)
