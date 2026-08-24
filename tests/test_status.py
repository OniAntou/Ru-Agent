from __future__ import annotations

import logging

import pytest

from ru_runtime.generation.openai import OpenAIRuntime
from ru_runtime.worker.v1 import runtime_services as shared
from ru_runtime.worker.v1 import status


def test_get_status_reports_load_error(monkeypatch) -> None:
    class FakeRunner:
        backend = "OpenCode Zen"
        load_error = "https://user:password@provider.example/?api_key=credential"

    monkeypatch.setattr(
        "ru_runtime.worker.v1.runtime_services.core.runner",
        FakeRunner(),
    )

    result = status.get_status()

    assert result["status"] == "error"
    assert result["message"] == "Model runtime failed to load."
    assert "password" not in str(result)
    assert "credential" not in str(result)


def test_get_models_rejects_unknown_backend_without_echoing_input() -> None:
    backend = "https://user:password@provider.example/?api_key=credential\nforged"

    result = status.get_models(backend)

    assert result == {"status": "error", "message": "Unsupported model backend."}
    assert "password" not in str(result)
    assert "credential" not in str(result)


@pytest.mark.parametrize(
    ("backend", "credential_attribute"),
    (
        ("OpenCode Zen", "opencode_cred"),
        ("Kilo Gateway", "kilo_cred"),
        ("Custom OpenAI", "custom_cred"),
    ),
)
def test_get_models_never_logs_credential_exception_text(
    monkeypatch,
    caplog,
    backend: str,
    credential_attribute: str,
) -> None:
    secret = "https://user:password@provider.example/?api_key=credential"

    class ExplodingCredential:
        def get_key(self) -> str:
            raise RuntimeError(secret)

    monkeypatch.setattr(shared.core, credential_attribute, ExplodingCredential())
    monkeypatch.setattr(OpenAIRuntime, "fetch_models", staticmethod(lambda *_args: []))

    with caplog.at_level(logging.WARNING, logger="ru_runtime.worker.v1.runtime_services"):
        status.get_models(backend)

    assert "password" not in caplog.text
    assert "api_key" not in caplog.text
    assert "provider.example" not in caplog.text


def test_get_status_ok_when_loaded(monkeypatch) -> None:
    class FakeRunner:
        backend = "OpenCode Zen"
        load_error = None

    monkeypatch.setattr(
        "ru_runtime.worker.v1.runtime_services.core.runner",
        FakeRunner(),
    )

    result = status.get_status()

    assert result["status"] == "ok"
    assert result["backend"] == "OpenCode Zen"
