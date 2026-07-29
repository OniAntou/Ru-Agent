from __future__ import annotations

from typing import Protocol


class KeyringBackend(Protocol):
    def get_password(self, service_name: str, username: str) -> str | None: ...

    def set_password(self, service_name: str, username: str, password: str) -> None: ...

    def delete_password(self, service_name: str, username: str) -> None: ...


class CredentialStoreError(RuntimeError):
    pass


class CredentialStore:
    def __init__(self, service_name: str, account_name: str, backend: KeyringBackend | None = None):
        self.service_name = service_name
        self.account_name = account_name
        if backend is None:
            try:
                import keyring
            except ImportError as error:
                raise CredentialStoreError("Windows Credential Manager support is unavailable") from error
            backend = keyring
        self._backend = backend

    def get_key(self) -> str | None:
        try:
            return self._backend.get_password(self.service_name, self.account_name)
        except Exception as error:
            raise CredentialStoreError("Could not read the saved API key") from error

    def set_key(self, key: str) -> None:
        key = key.strip()
        if not key:
            raise ValueError("API key cannot be empty")
        try:
            self._backend.set_password(self.service_name, self.account_name, key)
        except Exception as error:
            raise CredentialStoreError("Could not save the API key") from error

    def delete_key(self) -> None:
        try:
            self._backend.delete_password(self.service_name, self.account_name)
        except Exception as error:
            raise CredentialStoreError("Could not remove the saved API key") from error

    def migrate_legacy(self, settings: dict[str, object]) -> tuple[dict[str, object], bool]:
        cleaned = dict(settings)
        legacy_key = cleaned.pop("api_key", None)
        if isinstance(legacy_key, str) and legacy_key.strip() and self.get_key() is None:
            self.set_key(legacy_key)
        return cleaned, isinstance(legacy_key, str)
