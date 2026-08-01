from __future__ import annotations

import json
import queue
import threading
import time

import pytest
from fastapi.testclient import TestClient

import mini_gpt.headless_runner as headless_runner
import mini_gpt.server as server


@pytest.fixture(autouse=True)
def isolated_state(monkeypatch, tmp_path):
    monkeypatch.setattr(server, "SETTINGS_FILE", tmp_path / "settings.json")
    monkeypatch.setattr(server, "HISTORY_FILE", tmp_path / "history.json")
    monkeypatch.setattr(server, "SESSION_DIR", tmp_path / "sessions")
    server._SESSION_TURNS.clear()
    server.core.runner.backend = headless_runner.BACKEND_GGUF
    monkeypatch.setattr(server.core, "remember", lambda prompt, reply: None)
    monkeypatch.setattr(server._MEMORY_QUEUE, "put_nowait", lambda item: None)
    yield
    server._SESSION_TURNS.clear()
    if server._TURN_LOCK.locked():
        server._TURN_LOCK.release()
    if server._MEMORY_TURN_LOCK.locked():
        server._MEMORY_TURN_LOCK.release()


def test_status_endpoint() -> None:
    with TestClient(server.app) as client:
        response = client.get("/status")
    assert response.status_code == 200
    assert response.json()["status"] == "ok"


def test_settings_round_trip_and_no_api_key(monkeypatch, isolated_state) -> None:
    monkeypatch.setattr(server.core.runner, "load", lambda *a, **k: "Ready")
    with TestClient(server.app) as client:
        response = client.post(
            "/settings",
            json={
                "backend": "llama.cpp",
                "model": "local-model-gguf",
                "api_key": "",
                "api_variant": "Default",
                "max_tokens": 256,
                "temperature": 0.5,
                "allow_web_search": False,
            },
        )
        assert response.status_code == 200
        loaded = client.get("/settings")
    assert loaded.status_code == 200
    body = loaded.json()
    assert body["backend"] == "llama.cpp"
    assert body["max_tokens"] == 256
    assert body["temperature"] == 0.5
    assert body["allow_web_search"] is False
    assert "api_key" not in body
    assert "apiKey" not in body


def test_settings_saves_kilo_api_key(monkeypatch, isolated_state) -> None:
    saved = []
    class FakeCred:
        def set_key(self, key: str) -> None:
            saved.append(key)

    monkeypatch.setattr(server.core, "kilo_cred", FakeCred())
    monkeypatch.setattr(server.core.runner, "load", lambda *a, **k: "Ready")
    with TestClient(server.app) as client:
        response = client.post(
            "/settings",
            json={
                "backend": "Kilo Gateway",
                "model": "deepseek-v3",
                "api_key": "kilo-test-key",
                "api_variant": "Default",
            },
        )
        assert response.status_code == 200
        assert response.json()["status"] == "success"
    assert saved == ["kilo-test-key"]


def test_models_endpoint_rejects_local_backend(monkeypatch, isolated_state) -> None:
    with TestClient(server.app) as client:
        response = client.get("/models", params={"backend": "llama.cpp"})
    assert response.json() == {"status": "error", "message": "unsupported backend: 'llama.cpp'"}


def test_models_endpoint_requires_api_key(monkeypatch, isolated_state) -> None:
    class FakeCred:
        def get_key(self) -> str | None:
            return None

    monkeypatch.setattr(server.core, "kilo_cred", FakeCred())
    with TestClient(server.app) as client:
        response = client.get("/models", params={"backend": "Kilo Gateway"})
    assert response.json()["status"] == "error"


def test_models_endpoint_returns_sorted_models(monkeypatch, isolated_state) -> None:
    class FakeCred:
        def get_key(self) -> str | None:
            return "kilo-test-key"

    fetched = []

    def fake_fetch_models(api_key: str, base_url: str) -> list[dict]:
        fetched.append((api_key, base_url))
        return [{"id": "gpt-4o"}, {"id": "claude-3-5-sonnet"}, {"id": "deepseek-v3"}]

    monkeypatch.setattr(server.core, "kilo_cred", FakeCred())
    monkeypatch.setattr(
        "mini_gpt.openai_runtime.OpenAIRuntime.fetch_models",
        staticmethod(fake_fetch_models),
    )
    with TestClient(server.app) as client:
        response = client.get("/models", params={"backend": "Kilo Gateway"})
    body = response.json()
    assert body["status"] == "ok"
    assert set(body["models"]) == {"gpt-4o", "claude-3-5-sonnet", "deepseek-v3"}
    assert fetched[0][0] == "kilo-test-key"


def test_settings_empty_api_key_does_not_crash(monkeypatch, isolated_state) -> None:
    monkeypatch.setattr(server.core.runner, "load", lambda *a, **k: "Ready")
    with TestClient(server.app) as client:
        response = client.post(
            "/settings",
            json={
                "backend": "OpenCode Zen",
                "model": "deepseek-v4-flash-free",
                "api_key": "",
                "api_variant": "Default",
                "max_tokens": 64,
                "temperature": 0.7,
            },
        )
    assert response.status_code == 200


def test_settings_reuses_saved_key_when_payload_key_empty(monkeypatch, isolated_state) -> None:
    loaded = []

    class FakeCred:
        def get_key(self) -> str | None:
            return "kilo-saved-key"

    def fake_load(*args, **kwargs):
        loaded.append(kwargs.get("api_key"))
        return "Ready (Kilo Gateway API)"

    monkeypatch.setattr(server.core, "kilo_cred", FakeCred())
    monkeypatch.setattr(server.core.runner, "load", fake_load)
    with TestClient(server.app) as client:
        response = client.post(
            "/settings",
            json={
                "backend": "Kilo Gateway",
                "model": "deepseek-v3",
                "api_key": "",
                "api_variant": "Default",
            },
        )
        assert response.status_code == 200
        assert response.json()["status"] == "success"
    assert loaded == ["kilo-saved-key"]


def test_settings_load_failure_returns_error_not_crash(monkeypatch, isolated_state) -> None:
    def boom(*a, **k):
        raise RuntimeError("missing GGUF file")

    monkeypatch.setattr(server.core.runner, "load", boom)
    with TestClient(server.app) as client:
        response = client.post(
            "/settings",
            json={
                "backend": "llama.cpp",
                "model": "local-model-gguf",
                "max_tokens": 64,
                "temperature": 0.7,
            },
        )
    assert response.status_code == 200
    body = response.json()
    assert body["status"] == "error"
    assert "missing GGUF file" in body["message"]


def test_settings_checkpoint_path_round_trip(monkeypatch, isolated_state) -> None:
    monkeypatch.setattr(server.core.runner, "load", lambda *a, **k: "Ready")
    with TestClient(server.app) as client:
        response = client.post(
            "/settings",
            json={
                "backend": "Transformers",
                "model": "ru-llama3.1-8b-instruct-v6.2-fresh",
                "max_tokens": 64,
                "temperature": 0.7,
                "checkpoint_path": "checkpoints/ru-llama3.1-8b-instruct-v6.2-fresh",
            },
        )
        assert response.status_code == 200
        body = client.get("/settings").json()
    assert body["checkpoint_path"] == "checkpoints/ru-llama3.1-8b-instruct-v6.2-fresh"


def test_settings_voice_fields_round_trip(monkeypatch, isolated_state) -> None:
    monkeypatch.setattr(server.core.runner, "load", lambda *a, **k: "Ready")
    with TestClient(server.app) as client:
        response = client.post(
            "/settings",
            json={
                "backend": "llama.cpp",
                "model": "local-model-gguf",
                "max_tokens": 64,
                "temperature": 0.7,
                "voice_label": "Ái Vy (OmniVoice)",
                "voice_speed": 1.05,
            },
        )
        assert response.status_code == 200
        body = client.get("/settings").json()
    assert body["voice_label"] == "Ái Vy (OmniVoice)"
    assert body["voice_speed"] == 1.05


def test_settings_partial_update_keeps_backend(monkeypatch, isolated_state) -> None:
    load_calls = {"n": 0}

    def fake_load(*a, **k):
        load_calls["n"] += 1
        return "Ready"

    monkeypatch.setattr(server.core.runner, "load", fake_load)
    with TestClient(server.app) as client:
        client.post(
            "/settings",
            json={"backend": "llama.cpp", "model": "local-model-gguf", "max_tokens": 128},
        )
        voice_only = client.post("/settings", json={"voice_speed": 1.1})
        body = client.get("/settings").json()
    assert voice_only.status_code == 200
    assert voice_only.json()["status"] == "success"
    assert body["backend"] == "llama.cpp"
    assert body["voice_speed"] == 1.1
    assert load_calls["n"] == 1, "partial update must not reload the backend"


def test_voice_status_reports_availability(monkeypatch, isolated_state) -> None:
    monkeypatch.setattr(server, "voice_available", lambda: False)
    with TestClient(server.app) as client:
        response = client.get("/voice/status")
    assert response.status_code == 200
    assert response.json() == {"status": "ok", "available": False}


def test_voice_tts_returns_wav_bytes(monkeypatch, isolated_state) -> None:
    monkeypatch.setattr(server, "ensure_voice_service", lambda: None)
    monkeypatch.setattr(server, "request_voice_audio", lambda text, label, speed: b"RIFFWAVE-data")
    with TestClient(server.app) as client:
        response = client.post("/voice/tts", json={"text": "xin chào"})
    assert response.status_code == 200
    assert response.headers["content-type"] == "audio/wav"
    assert response.content == b"RIFFWAVE-data"


def test_voice_tts_rejects_empty_text(monkeypatch, isolated_state) -> None:
    monkeypatch.setattr(server, "ensure_voice_service", lambda: None)
    with TestClient(server.app) as client:
        response = client.post("/voice/tts", json={"text": "   "})
    assert response.status_code == 200
    assert response.json()["status"] == "error"


def test_voice_tts_surfaces_service_failure(monkeypatch, isolated_state) -> None:
    def boom(text, label, speed):
        raise RuntimeError("voice server offline")

    monkeypatch.setattr(server, "ensure_voice_service", lambda: None)
    monkeypatch.setattr(server, "request_voice_audio", boom)
    with TestClient(server.app) as client:
        response = client.post("/voice/tts", json={"text": "hi"})
    assert response.status_code == 200
    body = response.json()
    assert body["status"] == "error"
    assert "voice server offline" in body["message"]


def test_voice_warmup_and_release(monkeypatch, isolated_state) -> None:
    monkeypatch.setattr(server, "ensure_voice_service", lambda: None)
    monkeypatch.setattr(server, "request_voice_control", lambda path, label: {"ok": True})
    with TestClient(server.app) as client:
        warmup = client.post("/voice/warmup")
        release = client.post("/voice/release")
    assert warmup.json() == {"status": "ok", "result": {"ok": True}}
    assert release.json() == {"status": "ok", "result": {"ok": True}}


def test_voice_transcribe_returns_text(monkeypatch, isolated_state) -> None:
    monkeypatch.setattr(
        server,
        "build_speech_recognizer",
        lambda config: type("Recognizer", (), {"recognize": lambda self, audio: "xin chào"})(),
    )
    pcm = b"\x00\x00" * 16000
    with TestClient(server.app) as client:
        response = client.post(
            "/voice/transcribe",
            json={"audio_b64": __import__("base64").b64encode(pcm).decode(), "sample_rate": 16000},
        )
    assert response.status_code == 200
    assert response.json() == {"status": "ok", "text": "xin chào"}


def test_voice_transcribe_rejects_bad_audio(monkeypatch, isolated_state) -> None:
    with TestClient(server.app) as client:
        empty = client.post("/voice/transcribe", json={"audio_b64": ""})
        invalid = client.post("/voice/transcribe", json={"audio_b64": "!!!not-base64!!!"})
        bad_rate = client.post("/voice/transcribe", json={"audio_b64": "AA==", "sample_rate": 0})
    assert empty.json()["status"] == "error"
    assert invalid.json()["status"] == "error"
    assert bad_rate.json()["status"] == "error"


def test_voice_transcribe_surfaces_recognizer_error(monkeypatch, isolated_state) -> None:
    class NoMatchRecognizer:
        def recognize(self, audio):
            raise server.SpeechRecognitionError("Could not understand audio.")

    monkeypatch.setattr(
        server, "build_speech_recognizer", lambda config: NoMatchRecognizer()
    )
    with TestClient(server.app) as client:
        response = client.post(
            "/voice/transcribe",
            json={"audio_b64": __import__("base64").b64encode(b"\x00\x00" * 8000).decode()},
        )
    assert response.json() == {"status": "error", "message": "Could not understand audio."}


def test_vts_status_and_connect_flows(monkeypatch, isolated_state) -> None:
    class FakeVts:
        is_connected = False

    fake = FakeVts()
    monkeypatch.setattr(server, "_VTS_MANAGER", fake)
    monkeypatch.setattr(server, "_vts_manager", lambda: fake)
    with TestClient(server.app) as client:
        status = client.get("/vts/status")
        connect = client.post("/vts/connect")
    assert status.json() == {"status": "ok", "connected": False}
    assert connect.json()["status"] == "error"


def test_vts_connected_status_and_action(monkeypatch, isolated_state) -> None:
    calls: list[dict] = []

    class FakeVts:
        is_connected = True

        def trigger_actions_sync(self, actions):
            calls.append({"kind": "semantic", "facial": actions.facial, "body": actions.body})

    fake = FakeVts()
    monkeypatch.setattr(server, "_VTS_MANAGER", fake)
    with TestClient(server.app) as client:
        status = client.get("/vts/status")
        action = client.post("/vts/action", json={"facial": "joy"})
        empty = client.post("/vts/action", json={})
    assert status.json() == {"status": "ok", "connected": True}
    assert action.json() == {"status": "ok"}
    assert calls == [{"kind": "semantic", "facial": "joy", "body": None}]
    assert empty.json()["status"] == "error"


def test_chat_websocket_streams_chunks_then_done(monkeypatch, isolated_state) -> None:
    def fake_generate(prompt, max_new_tokens, temperature, top_k, allow_web_search, turns_history, on_stream_delta, **kwargs):
        on_stream_delta("Ch")
        on_stream_delta("ào")
        return "Chào anh"

    monkeypatch.setattr(server.core, "generate_reply_with_memory", fake_generate)
    with TestClient(server.app) as client:
        session_id = client.post("/sessions/new").json()["session_id"]
        with client.websocket_connect("/chat") as ws:
            ws.send_text(json.dumps({"prompt": "xin chào", "session_id": session_id}))
            first = ws.receive_json()
            second = ws.receive_json()
            done = ws.receive_json()
    assert first == {"type": "chunk", "content": "Ch"}
    assert second == {"type": "chunk", "content": "ào"}
    assert done["type"] == "done"


def test_chat_error_sends_error_then_done(monkeypatch, isolated_state) -> None:
    def boom(prompt, max_new_tokens, temperature, top_k, allow_web_search, turns_history, on_stream_delta, **kwargs):
        raise RuntimeError("model exploded")

    monkeypatch.setattr(server.core, "generate_reply_with_memory", boom)
    with TestClient(server.app) as client:
        session_id = client.post("/sessions/new").json()["session_id"]
        with client.websocket_connect("/chat") as ws:
            ws.send_text(json.dumps({"prompt": "boom", "session_id": session_id}))
            error = ws.receive_json()
    assert error["type"] == "error"
    assert "model exploded" in error["content"]


def test_prompts_are_processed_serially(monkeypatch, isolated_state) -> None:
    started = threading.Event()
    release = threading.Event()
    call_count = {"n": 0}

    def slow_generate(prompt, max_new_tokens, temperature, top_k, allow_web_search, turns_history, on_stream_delta, **kwargs):
        call_count["n"] += 1
        if call_count["n"] == 1:
            started.set()
            assert release.wait(timeout=5), "previous turn never finished"
        on_stream_delta("ok")
        return "ok"

    monkeypatch.setattr(server.core, "generate_reply_with_memory", slow_generate)
    with TestClient(server.app) as client:
        with client.websocket_connect("/chat") as ws:
            ws.send_text(json.dumps({"prompt": "first"}))
            assert started.wait(timeout=3), "first turn did not start"
            ws.send_text(json.dumps({"prompt": "second"}))
            time.sleep(0.5)
            assert call_count["n"] == 1, "second turn started before the first finished"
            release.set()
            while ws.receive_json()["type"] != "done":
                pass
            while ws.receive_json()["type"] != "done":
                pass
    assert call_count["n"] == 2


def test_chat_persists_turns_into_session(monkeypatch, isolated_state) -> None:
    def fake_generate(prompt, max_new_tokens, temperature, top_k, allow_web_search, turns_history, on_stream_delta, **kwargs):
        on_stream_delta("reply")
        return "reply"

    monkeypatch.setattr(server.core, "generate_reply_with_memory", fake_generate)
    with TestClient(server.app) as client:
        created = client.post("/sessions/new")
        session_id = created.json()["session_id"]
        with client.websocket_connect("/chat") as ws:
            ws.send_text(json.dumps({"prompt": "hello", "session_id": session_id}))
            while ws.receive_json()["type"] != "done":
                pass
        loaded = client.post(f"/sessions/{session_id}/activate")
    body = loaded.json()
    assert body["status"] == "ok"
    messages = body["session"]["messages"]
    assert [m["role"] for m in messages] == ["user", "ai"]
    assert messages[0]["content"] == "hello"
    assert messages[1]["content"] == "reply"
    assert body["session"]["title"] == "hello"


def test_web_search_flag_passed_through(monkeypatch, isolated_state) -> None:
    captured: dict[str, object] = {}

    def fake_generate(prompt, max_new_tokens, temperature, top_k, allow_web_search, turns_history, on_stream_delta, **kwargs):
        captured["search"] = allow_web_search
        return "ok"

    monkeypatch.setattr(server.core, "generate_reply_with_memory", fake_generate)
    with TestClient(server.app) as client:
        client.post(
            "/settings",
            json={
                "backend": "llama.cpp",
                "model": "local-model-gguf",
                "max_tokens": 64,
                "temperature": 0.7,
                "allow_web_search": False,
            },
        )
        with client.websocket_connect("/chat") as ws:
            ws.send_text(json.dumps({"prompt": "hi"}))
            while ws.receive_json()["type"] != "done":
                pass
    assert captured["search"] is False


def test_conversation_context_passed_between_turns(monkeypatch, isolated_state) -> None:
    histories: list[list] = []

    def fake_generate(prompt, max_new_tokens, temperature, top_k, allow_web_search, turns_history, on_stream_delta, **kwargs):
        histories.append(list(turns_history))
        on_stream_delta("reply")
        return "reply"

    monkeypatch.setattr(server.core, "generate_reply_with_memory", fake_generate)
    with TestClient(server.app) as client:
        with client.websocket_connect("/chat") as ws:
            ws.send_text(json.dumps({"prompt": "turn one"}))
            while ws.receive_json()["type"] != "done":
                pass
            ws.send_text(json.dumps({"prompt": "turn two"}))
            while ws.receive_json()["type"] != "done":
                pass
    assert len(histories[0]) == 0
    assert len(histories[1]) == 1
    assert histories[1][0].prompt == "turn one"


def test_startup_applies_saved_settings(monkeypatch, isolated_state) -> None:
    server.save_settings_to_file(
        {
            "backend": "OpenCode Zen",
            "model": "deepseek-v4-flash-free",
            "api_variant": "Default",
            "max_tokens": 128,
            "temperature": 0.3,
            "allow_web_search": True,
        }
    )
    monkeypatch.setattr(server.core.opencode_cred, "get_key", lambda: None)
    monkeypatch.setattr(server.core.runner, "load", lambda *a, **k: "Ready")
    result = server.apply_saved_settings()
    assert result == "Ready"
    assert server.core.runner.backend == headless_runner.BACKEND_OPENCODE


def test_startup_auto_starts_voice_service(monkeypatch, isolated_state) -> None:
    started = []
    monkeypatch.setattr(server, "ensure_voice_service", lambda: started.append(True))
    server._start_voice_service()
    assert started == [True]


def test_sessions_list_new_and_delete(monkeypatch, isolated_state) -> None:
    with TestClient(server.app) as client:
        empty = client.get("/sessions")
        created = client.post("/sessions/new")
        listed = client.get("/sessions")
        deleted = client.delete(f"/sessions/{created.json()['session_id']}")
        gone = client.delete(f"/sessions/{created.json()['session_id']}")
        final = client.get("/sessions")
    assert empty.json() == {"status": "ok", "sessions": []}
    session_id = created.json()["session_id"]
    assert created.json()["status"] == "ok"
    assert [item["id"] for item in listed.json()["sessions"]] == [session_id]
    assert listed.json()["sessions"][0]["message_count"] == 0
    assert deleted.json() == {"status": "ok"}
    assert gone.json()["status"] == "error"
    assert final.json()["sessions"] == []


def test_session_activate_rebuilds_turn_context(monkeypatch, isolated_state) -> None:
    session_id = server._create_session_file()
    assert session_id is not None
    assert server._session_append_messages(
        session_id,
        [
            {"role": "user", "content": "  Chào   anh hôm nay  "},
            {"role": "ai", "content": "Em chào anh nè!"},
        ],
    )
    with TestClient(server.app) as client:
        loaded = client.post(f"/sessions/{session_id}/activate")
    body = loaded.json()
    assert body["status"] == "ok"
    assert body["session"]["title"] == "Chào anh hôm nay"
    assert [m["role"] for m in body["session"]["messages"]] == ["user", "ai"]
    assert [turn.prompt for turn in server._SESSION_TURNS] == ["Chào   anh hôm nay"]


def test_session_activate_ignores_orphan_user_turn(monkeypatch, isolated_state) -> None:
    session_id = server._create_session_file()
    assert session_id is not None
    assert server._session_append_messages(
        session_id,
        [
            {"role": "user", "content": "Câu hỏi đầu"},
            {"role": "ai", "content": "Trả lời đầu"},
            {"role": "user", "content": "Câu hỏi chưa có trả lời"},
        ],
    )
    with TestClient(server.app) as client:
        loaded = client.post(f"/sessions/{session_id}/activate")
    body = loaded.json()
    assert body["status"] == "ok"
    assert [turn.prompt for turn in server._SESSION_TURNS] == ["Câu hỏi đầu"]


def test_ws_creates_session_when_id_missing(monkeypatch, isolated_state) -> None:
    def fake_generate(prompt, max_new_tokens, temperature, top_k, allow_web_search, turns_history, on_stream_delta, **kwargs):
        on_stream_delta("hi")
        return "hi"

    monkeypatch.setattr(server.core, "generate_reply_with_memory", fake_generate)
    with TestClient(server.app) as client:
        with client.websocket_connect("/chat") as ws:
            ws.send_text(json.dumps({"prompt": "xin chào"}))
            session_msg = ws.receive_json()
            while ws.receive_json()["type"] != "done":
                pass
    assert session_msg["type"] == "session"
    session_id = session_msg["session_id"]
    assert server._load_session_file(session_id) is not None


def test_ws_rejects_unknown_session_id(monkeypatch, isolated_state) -> None:
    def fake_generate(prompt, max_new_tokens, temperature, top_k, allow_web_search, turns_history, on_stream_delta, **kwargs):
        return "ok"

    monkeypatch.setattr(server.core, "generate_reply_with_memory", fake_generate)
    with TestClient(server.app) as client:
        with client.websocket_connect("/chat") as ws:
            ws.send_text(json.dumps({"prompt": "hi", "session_id": "missing-session-123"}))
            session_msg = ws.receive_json()
            while ws.receive_json()["type"] != "done":
                pass
    assert session_msg["type"] == "session"
    assert session_msg["session_id"] != "missing-session-123"


def test_session_endpoints_reject_path_traversal(monkeypatch, isolated_state) -> None:
    with TestClient(server.app) as client:
        activate = client.post("/sessions/..%2Fevil/activate")
        delete = client.delete("/sessions/..%2Fevil")
    assert activate.status_code == 404
    assert delete.status_code == 404


def test_migrate_legacy_history_creates_session_and_removes_file(monkeypatch, isolated_state) -> None:
    server.HISTORY_FILE.parent.mkdir(parents=True, exist_ok=True)
    server.HISTORY_FILE.write_text(
        json.dumps([{"role": "user", "content": "Cũ lắm rồi"}, {"role": "ai", "content": "Em còn đây"}]),
        encoding="utf-8",
    )
    server.migrate_legacy_history()
    sessions = server.load_sessions()
    assert len(sessions) == 1
    assert sessions[0]["title"] == "Cũ lắm rồi"
    assert sessions[0]["message_count"] == 2
    assert not server.HISTORY_FILE.exists()


def test_migrate_legacy_history_skips_when_sessions_exist(monkeypatch, isolated_state) -> None:
    server._create_session_file()
    server.HISTORY_FILE.parent.mkdir(parents=True, exist_ok=True)
    server.HISTORY_FILE.write_text(json.dumps([{"role": "user", "content": "x"}]), encoding="utf-8")
    server.migrate_legacy_history()
    assert len(server.load_sessions()) == 1
    assert server.HISTORY_FILE.exists()


def test_self_learning_setting_default_true_and_round_trip(monkeypatch, isolated_state) -> None:
    monkeypatch.setattr(server.core.runner, "load", lambda *a, **k: "Ready")
    with TestClient(server.app) as client:
        body = client.get("/settings").json()
        assert body["enable_self_learning"] is True
        saved = client.post(
            "/settings", json={"backend": "llama.cpp", "enable_self_learning": False}
        )
        assert saved.status_code == 200
        loaded = client.get("/settings").json()
    assert loaded["enable_self_learning"] is False


def test_learn_from_turn_disabled_setting_skips_queue(monkeypatch, isolated_state) -> None:
    calls = []
    monkeypatch.setattr(server._MEMORY_QUEUE, "put_nowait", lambda item: calls.append(item))
    server.save_settings_to_file({"enable_self_learning": False})
    server._learn_from_turn("prompt", "reply")
    assert calls == []


def test_learn_from_turn_enqueues_when_enabled(monkeypatch, isolated_state) -> None:
    calls = []
    monkeypatch.setattr(server._MEMORY_QUEUE, "put_nowait", lambda item: calls.append(item))
    server.save_settings_to_file({"enable_self_learning": True})
    server._learn_from_turn("prompt", "reply")
    assert calls == [("prompt", "reply")]


def test_process_memory_turn_skips_when_model_busy(monkeypatch, isolated_state) -> None:
    extracted = []
    monkeypatch.setattr(server, "extract_facts_with_llm", lambda p, r, g: extracted.append((p, r)) or ())
    server._MEMORY_TURN_LOCK.acquire()
    try:
        server._process_memory_turn("prompt", "reply")
    finally:
        server._MEMORY_TURN_LOCK.release()
    assert extracted == []


def test_process_memory_turn_stores_facts_and_dedups(monkeypatch, isolated_state, tmp_path) -> None:
    monkeypatch.setattr(server.core, "memory_db_path", tmp_path / "memory.db")
    monkeypatch.setattr(server.core, "memory_index_path", tmp_path / "memory_index.faiss")
    monkeypatch.setattr(server.core, "memory_texts_path", tmp_path / "memory_texts.json")
    server.core.memory_store = None
    fact = server.ExtractFact(
        kind=server.MemoryKind.PREFERENCE,
        content="Anh thích cà phê đen",
        confidence=0.9,
        importance=0.6,
    )
    monkeypatch.setattr(
        server, "extract_facts_with_llm", lambda p, r, g: (fact,)
    )
    store = server.core.get_memory_store()
    server._process_memory_turn("anh thích cà phê", "Ru: Được ạ.")
    assert store.count_active() == 1
    server._process_memory_turn("anh thích cà phê", "Ru: Được ạ.")
    assert store.count_active() == 1


def test_process_memory_turn_recovers_from_extraction_error(monkeypatch, isolated_state, tmp_path) -> None:
    monkeypatch.setattr(server.core, "memory_db_path", tmp_path / "memory.db")
    monkeypatch.setattr(server.core, "memory_index_path", tmp_path / "memory_index.faiss")
    monkeypatch.setattr(server.core, "memory_texts_path", tmp_path / "memory_texts.json")
    server.core.memory_store = None
    def boom(p, r, g):
        raise RuntimeError("boom")

    monkeypatch.setattr(server, "extract_facts_with_llm", boom)
    server._process_memory_turn("prompt", "reply")
    assert server.core.get_memory_store().count_active() == 0


def test_chat_enqueues_learning_turn(monkeypatch, isolated_state) -> None:
    enqueued = []
    monkeypatch.setattr(server, "_learn_from_turn", lambda p, r: enqueued.append((p, r)))
    monkeypatch.setattr(server, "extract_facts_with_llm", lambda p, r, g: ())
    monkeypatch.setattr(server.core, "generate_reply_with_memory", lambda **kw: "reply")
    with TestClient(server.app) as client:
        with client.websocket_connect("/chat") as ws:
            ws.send_text(json.dumps({"prompt": "hello"}))
            while ws.receive_json()["type"] != "done":
                pass
    deadline = time.monotonic() + 5
    while not enqueued and time.monotonic() < deadline:
        time.sleep(0.01)
    assert enqueued == [("hello", "reply")]


def test_learn_from_turn_queue_full_drops_oldest(monkeypatch, isolated_state) -> None:
    fresh = queue.Queue(maxsize=2)
    monkeypatch.setattr(server, "_MEMORY_QUEUE", fresh)
    server.save_settings_to_file({"enable_self_learning": True})
    server._learn_from_turn("prompt 1", "reply 1")
    server._learn_from_turn("prompt 2", "reply 2")
    server._learn_from_turn("prompt 3", "reply 3")
    assert list(fresh.queue) == [("prompt 2", "reply 2"), ("prompt 3", "reply 3")]
    fresh.queue.clear()


def test_process_memory_turn_skips_sensitive_turn(monkeypatch, isolated_state, tmp_path) -> None:
    monkeypatch.setattr(server.core, "memory_db_path", tmp_path / "memory.db")
    monkeypatch.setattr(server.core, "memory_index_path", tmp_path / "memory_index.faiss")
    monkeypatch.setattr(server.core, "memory_texts_path", tmp_path / "memory_texts.json")
    server.core.memory_store = None
    called = []
    monkeypatch.setattr(server, "extract_facts_with_llm", lambda p, r, g: called.append((p, r)) or ())
    server._process_memory_turn("api_key = sk-abc123", "Ru: Được ạ.")
    assert called == []
    assert server.core.get_memory_store().count_active() == 0
