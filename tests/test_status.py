from __future__ import annotations

from ru_runtime.worker.v1 import status


def test_get_status_reports_load_error(monkeypatch) -> None:
    class FakeRunner:
        backend = "OpenCode Zen"
        load_error = "model failed to load"

    monkeypatch.setattr(
        "ru_runtime.worker.v1.runtime_services.core.runner",
        FakeRunner(),
    )

    result = status.get_status()

    assert result["status"] == "error"
    assert result["message"] == "model failed to load"


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
