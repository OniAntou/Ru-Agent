# Ru API Key Security and Status Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Move OpenCode keys to Windows Credential Manager and give Ru safe verification, rate-limit/quota status, and actionable API failures.

**Architecture:** A credential adapter is the only component that reads/writes secrets. A status module turns HTTP data into redacted typed results; the OpenCode runtime returns those results to Tkinter, which persists only non-sensitive state.

**Tech Stack:** Python 3.13, keyring, Windows Credential Manager, urllib, Tkinter, pytest.

## Global Constraints

- Store no OpenCode key in gui_settings.json, logs, memory, approved examples, or status history.
- Verify exclusively through GET OpenCode /models; never create an additional completion.
- Do not estimate quota; show it only when an API response explicitly provides it.
- Preserve local GGUF and Transformers paths.

---

## File structure

- Create: src/mini_gpt/credential_store.py — keyring adapter and legacy migration.
- Create: src/mini_gpt/api_key_status.py — redaction, error categories, HTTP header parsing, safe status persistence.
- Modify: src/mini_gpt/openai_runtime.py — typed verification and safe runtime errors.
- Modify: src/mini_gpt/gui.py — migration, key controls, safe persisted settings/status.
- Modify: pyproject.toml — add keyring to runtime dependencies.
- Create: tests/test_credential_store.py and tests/test_api_key_status.py.
- Modify: tests/test_openai_runtime.py and tests/test_gui.py.
- Modify: README.md and docs/ANTIGRAVITY_HANDOFF.md after delivery.

### Task 1: Secure credential storage and one-time migration

**Files:**
- Create: src/mini_gpt/credential_store.py
- Create: tests/test_credential_store.py
- Modify: pyproject.toml

**Interfaces:**
- Produces: CredentialStore(service_name: str, account_name: str, backend: KeyringBackend), get_key() -> str | None, set_key(key: str) -> None, delete_key() -> None, migrate_legacy(settings: dict[str, object]) -> tuple[dict[str, object], bool].

- [ ] **Step 1: Write failing credential tests**

~~~python
def test_migration_moves_plaintext_key_to_backend_and_removes_setting():
    backend = FakeBackend()
    store = CredentialStore("Ru.OpenCode", "default", backend)

    settings, migrated = store.migrate_legacy({"api_key": "sk-live", "backend": "OpenCode Zen API"})

    assert migrated is True
    assert settings == {"backend": "OpenCode Zen API"}
    assert backend.values[("Ru.OpenCode", "default")] == "sk-live"


def test_delete_removes_key_without_returning_it():
    backend = FakeBackend(values={("Ru.OpenCode", "default"): "sk-live"})
    store = CredentialStore("Ru.OpenCode", "default", backend)

    assert store.delete_key() is None
    assert store.get_key() is None
~~~

- [ ] **Step 2: Verify RED**

Run:

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_credential_store.py -q
~~~

Expected: module import failure.

- [ ] **Step 3: Implement the narrow keyring adapter**

Implement a protocol with get_password, set_password, and delete_password. CredentialStore strips input, rejects empty values, maps backend exceptions to CredentialStoreError without embedding the secret, and uses this migration rule:

~~~python
legacy_key = settings.pop("api_key", None)
if isinstance(legacy_key, str) and legacy_key.strip() and self.get_key() is None:
    self.set_key(legacy_key)
return settings, isinstance(legacy_key, str)
~~~

Add keyring>=25 to runtime dependencies. Do not add a plaintext fallback.

- [ ] **Step 4: Verify GREEN**

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_credential_store.py -q
~~~

Expected: all credential and migration tests pass.

- [ ] **Step 5: Commit**

~~~powershell
git add pyproject.toml src\mini_gpt\credential_store.py tests\test_credential_store.py
git commit -m "feat: store Ru API key in credential manager"
~~~

### Task 2: Build redacted API status classification

**Files:**
- Create: src/mini_gpt/api_key_status.py
- Create: tests/test_api_key_status.py

**Interfaces:**
- Produces: ApiKeyStatus(category: ApiKeyStatusCategory, message: str, retry_at_utc: datetime | None, quota_available: bool, validated_at_utc: datetime | None, model_count: int | None), classify_http_error(status_code: int, body: str, headers: Mapping[str, str], secret: str | None) -> ApiKeyStatus.

- [ ] **Step 1: Write failing status tests**

~~~python
def test_rate_limit_uses_retry_after_and_redacts_the_key():
    status = classify_http_error(
        429,
        "Key sk-live is rate limited",
        {"Retry-After": "60"},
        "sk-live",
    )

    assert status.category is ApiKeyStatusCategory.RATE_LIMITED
    assert status.retry_at_utc is not None
    assert "sk-live" not in status.message


def test_provider_without_quota_header_reports_unavailable_quota():
    status = status_from_model_response(["model-a"], {})

    assert status.category is ApiKeyStatusCategory.VALID
    assert status.quota_available is False
    assert status.model_count == 1
~~~

- [ ] **Step 2: Verify RED**

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_api_key_status.py -q
~~~

Expected: module import failure.

- [ ] **Step 3: Implement safe categories and parser**

Define MISSING, CREDENTIAL_UNAVAILABLE, INVALID, RATE_LIMITED, QUOTA_EXHAUSTED, NETWORK, PROVIDER, and VALID. Redact the supplied secret plus sk- and gho_-style token fragments. Parse Retry-After as seconds or HTTP date. Treat 401/403 as INVALID and 429 as RATE_LIMITED unless the provider body explicitly includes quota/credit exhaustion wording. Set quota_available only when quota/credit headers or explicit body data exists.

- [ ] **Step 4: Verify GREEN**

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_api_key_status.py -q
~~~

Expected: authentication, limit, quota, network, and redaction cases pass.

- [ ] **Step 5: Commit**

~~~powershell
git add src\mini_gpt\api_key_status.py tests\test_api_key_status.py
git commit -m "feat: classify safe OpenCode key status"
~~~

### Task 3: Make verification and generation return safe typed results

**Files:**
- Modify: src/mini_gpt/openai_runtime.py
- Modify: tests/test_openai_runtime.py

**Interfaces:**
- Consumes: ApiKeyStatus and classify_http_error from Task 2.
- Produces: OpenCodeVerification(models: tuple[str, ...], status: ApiKeyStatus), OpenCodeRuntimeError(status: ApiKeyStatus), verify_key(api_key: str) -> OpenCodeVerification.

- [ ] **Step 1: Write failing runtime tests**

~~~python
def test_verify_key_calls_models_once_without_chat_completion(monkeypatch):
    requests = []
    monkeypatch.setattr(runtime_module, "urlopen", fake_models_response(requests))

    verification = OpenAIRuntime.verify_key("sk-live")

    assert verification.models == ("model-a",)
    assert requests[0].full_url.endswith("/models")
    assert len(requests) == 1


def test_generation_raises_redacted_rate_limit_error(monkeypatch):
    monkeypatch.setattr(runtime_module, "urlopen", raise_http_error(429, "sk-live exhausted", {"Retry-After": "30"}))

    with pytest.raises(OpenCodeRuntimeError, match="rate"):
        OpenAIRuntime("sk-live").generate((("user", "hello"),), 16, 0.7, 20)
~~~

- [ ] **Step 2: Verify RED**

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_openai_runtime.py -q
~~~

Expected: verification and typed exception symbols are absent.

- [ ] **Step 3: Implement verification and typed failures**

Replace fetch_models internals with verify_key, retaining fetch_models as a compatibility wrapper returning list(verify_key(api_key).models). Catch HTTPError in verification and generation, parse its headers/body through classify_http_error, and raise OpenCodeRuntimeError. Catch URLError as NETWORK. No exception message may contain Authorization or the key. Read headers before closing an HTTPError response.

- [ ] **Step 4: Verify GREEN**

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_openai_runtime.py tests\test_api_key_status.py -q
~~~

Expected: /models verification has no completion request and every error is safe.

- [ ] **Step 5: Commit**

~~~powershell
git add src\mini_gpt\openai_runtime.py tests\test_openai_runtime.py
git commit -m "feat: verify OpenCode key and redact failures"
~~~

### Task 4: Replace plaintext GUI persistence and expose key controls

**Files:**
- Modify: src/mini_gpt/gui.py
- Modify: tests/test_gui.py
- Modify: README.md
- Modify: docs/ANTIGRAVITY_HANDOFF.md

**Interfaces:**
- Consumes: CredentialStore from Task 1 and OpenCodeVerification/OpenCodeRuntimeError from Task 3.
- Produces: RuApp._save_api_key(), _remove_api_key(), _verify_api_key(), and safe gui_settings.json serialization.

- [ ] **Step 1: Write failing GUI tests**

~~~python
def test_settings_migration_removes_api_key_from_json_and_uses_credential_store(tmp_path, monkeypatch):
    settings_path = tmp_path / "gui_settings.json"
    settings_path.write_text('{"api_key":"sk-live","backend":"OpenCode Zen API"}', encoding="utf-8")
    store = FakeCredentialStore()

    app = build_app_without_tk(settings_path, store)

    assert store.saved == ["sk-live"]
    assert json.loads(settings_path.read_text(encoding="utf-8")) == {"backend": "OpenCode Zen API"}


def test_verify_key_persists_only_safe_status():
    app = build_app_without_tk()
    app.credential_store = FakeCredentialStore("sk-live")
    app._verify_api_key()

    assert "sk-live" not in app.saved_settings_text
    assert app.api_key_status.category is ApiKeyStatusCategory.VALID
~~~

- [ ] **Step 2: Verify RED**

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_gui.py -q
~~~

Expected: credential store and key actions are absent.

- [ ] **Step 3: Implement GUI migration and actions**

At startup, instantiate CredentialStore, call migrate_legacy after reading settings, and rewrite migrated settings immediately. Remove api_key from the settings serializer and remove its trace callback. Keep an empty masked entry for Set/Replace only. Add Set/Replace, Remove, and Verify buttons plus a status label. Set saves entry content to Credential Manager then clears it. Remove deletes the credential and safe status. Verify runs OpenAIRuntime.verify_key on a worker thread and updates model choices/status through root.after. Before a cloud load/generation, read credential_store.get_key; missing key creates a MISSING status and avoids the request.

Persist a serializable api_key_status object containing only category, safe message, retry time, validation time, model count, and quota_available. Do not persist the key entry value.

- [ ] **Step 4: Update active docs and verify**

Update README and handoff with Credential Manager storage, one-time legacy migration, no quota estimation, key actions, and no automatic validation request. Run:

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_credential_store.py tests\test_api_key_status.py tests\test_openai_runtime.py tests\test_gui.py -q
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
git diff --check
~~~

Expected: all suites pass and no key appears in generated settings fixtures.

- [ ] **Step 5: Commit**

~~~powershell
git add src\mini_gpt\gui.py tests\test_gui.py README.md docs\ANTIGRAVITY_HANDOFF.md
git commit -m "feat: secure Ru API key settings"
~~~

## Plan self-review

- **Spec coverage:** Task 1 prevents plaintext persistence and migrates old keys. Task 2 supplies redaction, quota/rate-limit parsing, and safe persisted status. Task 3 uses existing /models verification without extra completion calls. Task 4 wires UI controls, cloud preflight, docs, and full verification.
- **Placeholder scan:** Every task contains files, interfaces, red/green commands, concrete implementation behavior, and a commit.
- **Type consistency:** CredentialStore, ApiKeyStatus, OpenCodeVerification, OpenCodeRuntimeError, and RuApp key action names are defined before use.
