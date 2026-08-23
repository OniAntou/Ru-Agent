from __future__ import annotations

import ru_runtime.worker.v1.runtime_services as server_shared


def test_desktop_token_reads_environment_lazily(monkeypatch) -> None:
    # Simulates the WinUI3 host: RU_DESKTOP_TOKEN is only known at request time,
    # after the module has already been imported. The secret set late in the
    # environment must still take effect.
    monkeypatch.setenv("RU_DESKTOP_TOKEN", "from-env-secret")
    assert server_shared._valid_desktop_token("from-env-secret") is True
    assert server_shared._valid_desktop_token("different") is False
    assert server_shared._valid_desktop_token("") is False


def test_desktop_token_fails_closed_when_unset(monkeypatch) -> None:
    monkeypatch.delenv("RU_DESKTOP_TOKEN", raising=False)
    assert server_shared._valid_desktop_token("anything") is False
