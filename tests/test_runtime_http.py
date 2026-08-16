from __future__ import annotations

import json
from pathlib import Path

from ru_runtime.worker.v1.http import (
    RUNTIME_ROUTE_KEYS,
    RuntimeHttpResponse,
    dispatch_runtime_request,
)


ROOT = Path(__file__).resolve().parents[1]


def test_runtime_route_table_matches_the_frozen_contract() -> None:
    contract = json.loads(
        (ROOT / "tests" / "fixtures" / "runtime-route-contract.json").read_text(
            encoding="utf-8"
        )
    )

    expected = {(route["method"], route["path"]) for route in contract["routes"]}

    assert RUNTIME_ROUTE_KEYS == expected
    assert len(expected) == 27


def test_private_runtime_dispatch_has_no_fastapi_or_router_dependency() -> None:
    source = (ROOT / "src" / "ru_runtime" / "worker" / "v1" / "http.py").read_text(
        encoding="utf-8"
    ).casefold()

    assert "ru_runtime.routers" not in source
    assert "fastapi" not in source
    assert "starlette" not in source
    assert "pydantic" not in source


def test_status_dispatch_preserves_the_public_json_shape() -> None:
    response = dispatch_runtime_request("GET", "/status", None, "")

    assert response.status_code == 200
    assert response.content_type == "application/json"
    assert json.loads(response.body) == {
        "status": "ok",
        "message": "Backend is running and ready for the WinUI3 UI.",
    }


def test_tts_dispatch_preserves_binary_without_embedding_it_in_json(monkeypatch) -> None:
    from ru_runtime.worker.v1 import voice

    monkeypatch.setattr(
        voice,
        "voice_tts",
        lambda _payload: RuntimeHttpResponse(200, "audio/wav", b"RIFF-wave", {}),
    )

    response = dispatch_runtime_request(
        "POST",
        "/voice/tts",
        "application/json",
        '{"text":"synthetic","voice_label":"Ru (OmniVoice)","speed":1.0}',
    )

    assert response.status_code == 200
    assert response.content_type == "audio/wav"
    assert response.body == b"RIFF-wave"


def test_unknown_runtime_route_fails_closed_without_echoing_the_body() -> None:
    response = dispatch_runtime_request(
        "POST",
        "/private/unknown",
        "application/json",
        '{"secret":"must-not-return"}',
    )

    assert response.status_code == 404
    assert b"must-not-return" not in response.body
