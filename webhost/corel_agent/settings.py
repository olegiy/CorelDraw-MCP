"""One operator profile. Only Windows Credential Manager stores credential values."""

import asyncio
import ipaddress
import json
from pathlib import Path
from threading import RLock
from urllib.parse import urlsplit

from pydantic import BaseModel, ConfigDict, Field, SecretStr, ValidationError

from .assets import assert_no_links
from .contracts import AgentError, ProfileSnapshot, new_id
from .owned_stdio import atomic_json

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

    def _credentials(self):
        try:
            if self._vault is None:
                self._vault = WindowsVault()
            return self._vault
        except Exception:
            raise AgentError("VAULT_UNAVAILABLE", "Windows Credential Manager недоступен; fallback запрещён.", 503) from None

    def _read(self):
        try:
            assert_no_links(self.path)
            if not self.path.exists():
                return []
            if not self.path.is_file() or self.path.stat().st_size > 65536:
                raise ValueError
            data = json.loads(self.path.read_text(encoding="utf-8"))
            if set(data) != {"schema_version", "profiles"} or data["schema_version"] != 1:
                raise ValueError
            if not isinstance(data["profiles"], list) or len(data["profiles"]) > 1:
                raise ValueError
            return [ProfileSnapshot.model_validate(item) for item in data["profiles"]]
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
                if profile.send_preview and not profile.vision:
                    raise AgentError("VISION_REQUIRED", "Передача превью требует включённого Vision.", 422)
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
                atomic_json(self.path, {"schema_version": 1, "profiles": [saved.model_dump(mode="json")]})
                return saved
        try:
            return await asyncio.to_thread(save)
        except OSError:
            raise AgentError("SETTINGS_UNAVAILABLE", "Не удалось сохранить метаданные профиля.", 503) from None

    async def freeze(self, profile_id, *, require_consent=True):
        def freeze():
            with self._lock:
                profile = next((item for item in self._read() if item.id == profile_id), None)
                if profile is None:
                    raise AgentError("PROFILE_NOT_FOUND", "Выбранный профиль отсутствует.", 404)
                if profile.local and not local_endpoint(profile.base_url):
                    raise AgentError("LOCAL_PROVIDER_INVALID", "Loopback профиль имеет внешний адрес.", 422)
                if require_consent and not profile.context_approved:
                    raise AgentError("CONTEXT_CONSENT_REQUIRED", "Разрешите передачу текстового контекста и кода выбранной модели.", 403)
                try:
                    secret = self._credentials().get(profile.credential_revision)
                except Exception:
                    raise AgentError("VAULT_UNAVAILABLE", "Windows Credential Manager недоступен; запуск модели запрещён.", 503) from None
                if secret is None:
                    raise AgentError("CREDENTIAL_MISSING", "Credential профиля отсутствует; env/file fallback запрещён.", 503)
                return FrozenConnection(profile=profile, secret=SecretStr(secret))
        return await asyncio.to_thread(freeze)
