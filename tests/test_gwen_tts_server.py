from __future__ import annotations

import os

import gwen_tts_server


def test_status_reports_the_owned_service_without_loading_gwen(monkeypatch) -> None:
    monkeypatch.setattr(gwen_tts_server, "_model", None)

    def fail_if_loaded():
        raise AssertionError("status must not load the model")

    monkeypatch.setattr(gwen_tts_server, "get_model", fail_if_loaded)
    response = gwen_tts_server.create_app().test_client().get("/status")

    assert response.status_code == 200
    assert response.get_json() == {"pid": os.getpid(), "model_loaded": False}
