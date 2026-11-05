"""Outbound masking, not a VBA sandbox or a source-code restoration mechanism."""

import re

from .contracts import AgentError

PATH_MARKER = "<локальный путь>"
SECRET_MARKER = "<секрет скрыт>"
PATH_KEYS = frozenset({"path", "run_directory", "report_path", "log_path", "source_root", "module_paths"})
SECRET_KEYS = frozenset({"api_key", "authorization", "password", "access_token", "refresh_token"})
WINDOWS_PATH = re.compile(r"(?:[A-Za-z]:[\\/]|\\\\)[^\r\n\"<>|?*]+")
ALIASES = re.compile(r"@(?:output|file|path)[/\\]|<локальный путь>|<path[^>]*>", re.IGNORECASE)


class Privacy:
    def __init__(self, secrets=()):
        self._secrets = set(value for value in secrets if value)

    def register_secret(self, value):
        if value:
            self._secrets.add(value)

    def contains_secret(self, value):
        if isinstance(value, str):
            return any(secret in value for secret in self._secrets)
        if isinstance(value, dict):
            return any(self.contains_secret(item) for item in value.values())
        if isinstance(value, list | tuple):
            return any(self.contains_secret(item) for item in value)
        return False

    def validate_vba(self, code):
        if self.contains_secret(code):
            raise AgentError("SECRET_IN_OPERATION", "Операция содержит credential value; отправка запрещена.", 403)
        if ALIASES.search(code):
            raise AgentError("VBA_ALIAS_UNRESOLVED", "Path aliases в VBA не раскрываются и не исполняются. Используйте структурный file_ref/path аргумент.", 422)
        return code

    def sanitize_local(self, value):
        if isinstance(value, str):
            for secret in sorted(self._secrets, key=len, reverse=True):
                value = value.replace(secret, SECRET_MARKER)
            return value
        if isinstance(value, dict):
            return {key: SECRET_MARKER if key.lower() in SECRET_KEYS else self.sanitize_local(item)
                    for key, item in value.items()}
        if isinstance(value, list | tuple):
            return [self.sanitize_local(item) for item in value]
        return value

    def for_model(self, value):
        value = self.sanitize_local(value)
        if isinstance(value, str):
            return WINDOWS_PATH.sub(PATH_MARKER, value)
        if isinstance(value, dict):
            return {key: PATH_MARKER if key.lower() in PATH_KEYS else self.for_model(item)
                    for key, item in value.items()}
        if isinstance(value, list | tuple):
            return [self.for_model(item) for item in value]
        return value
