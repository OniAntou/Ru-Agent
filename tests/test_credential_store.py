from mini_gpt.credential_store import CredentialStore


class FakeBackend:
    def __init__(self, values=None):
        self.values = values or {}

    def get_password(self, service, account):
        return self.values.get((service, account))

    def set_password(self, service, account, value):
        self.values[(service, account)] = value

    def delete_password(self, service, account):
        self.values.pop((service, account), None)


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
