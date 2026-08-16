import pytest

from ru_runtime.persistence.credential_store import CredentialStore, CredentialStoreError


class FakeBackend:
    def __init__(self, values=None):
        self.values = values or {}

    def get_password(self, service, account):
        return self.values.get((service, account))

    def set_password(self, service, account, value):
        self.values[(service, account)] = value

    def delete_password(self, service, account):
        self.values.pop((service, account), None)


class FailingBackend:
    def get_password(self, service, account):
        raise RuntimeError("keyring unreachable")

    def set_password(self, service, account, value):
        raise RuntimeError("keyring unreachable")

    def delete_password(self, service, account):
        raise RuntimeError("keyring unreachable")


def test_migration_moves_plaintext_key_to_backend_and_removes_setting():
    backend = FakeBackend()
    store = CredentialStore("Ru.OpenCode", "default", backend)

    settings, migrated = store.migrate_legacy({"api_key": "sk-live", "backend": "OpenCode Zen API"})

    assert migrated is True
    assert settings == {"backend": "OpenCode Zen API"}
    assert backend.values[("Ru.OpenCode", "default")] == "sk-live"


def test_delete_removes_key_without_returning_it():
    backend = FakeBackend({("Ru.OpenCode", "default"): "sk-live"})
    store = CredentialStore("Ru.OpenCode", "default", backend)

    assert store.delete_key() is None
    assert store.get_key() is None


def test_set_key_strips_and_get_key_round_trips():
    backend = FakeBackend()
    store = CredentialStore("Ru.OpenCode", "default", backend)

    store.set_key("  sk-live  ")

    assert store.get_key() == "sk-live"
    assert backend.values[("Ru.OpenCode", "default")] == "sk-live"


def test_set_key_rejects_blank_values():
    store = CredentialStore("Ru.OpenCode", "default", FakeBackend())

    with pytest.raises(ValueError, match="cannot be empty"):
        store.set_key("   ")


def test_backend_failures_become_user_safe_errors():
    store = CredentialStore("Ru.OpenCode", "default", FailingBackend())

    with pytest.raises(CredentialStoreError, match="read the saved API key"):
        store.get_key()
    with pytest.raises(CredentialStoreError, match="save the API key"):
        store.set_key("sk-live")
    with pytest.raises(CredentialStoreError, match="remove the saved API key"):
        store.delete_key()


def test_migration_keeps_an_existing_backend_key():
    backend = FakeBackend({("Ru.OpenCode", "default"): "sk-existing"})
    store = CredentialStore("Ru.OpenCode", "default", backend)

    settings, migrated = store.migrate_legacy({"api_key": "sk-legacy", "backend": "OpenCode Zen API"})

    assert migrated is False
    assert settings == {"backend": "OpenCode Zen API"}
    assert backend.values[("Ru.OpenCode", "default")] == "sk-existing"


def test_migration_without_a_string_key_does_not_touch_the_backend():
    backend = FakeBackend({("Ru.OpenCode", "default"): "sk-existing"})
    store = CredentialStore("Ru.OpenCode", "default", backend)

    settings, migrated = store.migrate_legacy({"backend": "OpenCode Zen API"})

    assert migrated is False
    assert settings == {"backend": "OpenCode Zen API"}
    assert backend.values[("Ru.OpenCode", "default")] == "sk-existing"
