"""Mask known credentials in reports and outbound model context."""


SECRET_MARKER = "<секрет скрыт>"
SECRET_KEYS = frozenset({"api_key", "authorization", "password", "access_token", "refresh_token"})


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
        return self.sanitize_local(value)
