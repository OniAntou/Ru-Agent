from pathlib import Path

import pytest

from mini_gpt import gui
from mini_gpt.vts_actions import VtsActions
from mini_gpt.vts_catalog import build_catalog
from mini_gpt.gui import (
    BACKEND_GGUF,
    BACKEND_OPENCODE,
    BACKEND_TRANSFORMERS,
    MODEL_NAME,
    ModelRunner,
    clean_tts_text,
    default_paths,
    parse_generation_settings,
    request_tts_audio,
)


def test_model_name_is_ru():
    assert MODEL_NAME == "Ru"


def test_submit_from_keyboard_starts_generation_and_consumes_event():
    app = object.__new__(gui.RuApp)
    calls: list[str] = []
    app._start_generation = lambda: calls.append("generate")

    assert app._submit_from_keyboard(object()) == "break"
    assert calls == ["generate"]


def test_default_generation_limit_is_short_conversation_length():
    assert gui.DEFAULT_MAX_NEW_TOKENS == "128"


def test_generation_settings_are_parsed_and_validated():
    assert parse_generation_settings("50", "0.8", "20") == (50, 0.8, 20)
    with pytest.raises(ValueError, match="temperature"):
        parse_generation_settings("50", "0", "20")
    with pytest.raises(ValueError, match="tokens"):
        parse_generation_settings("0", "0.8", "20")


def test_default_paths_point_inside_project():
    checkpoint = default_paths()
    assert str(checkpoint).startswith("D:\\LLM")


def test_default_paths_fall_back_to_last_candidate(tmp_path, monkeypatch):
    monkeypatch.setattr(gui, "PROJECT_ROOT", tmp_path)
    checkpoint = gui.default_paths()
    assert str(checkpoint).endswith("ru-llama3.1-8b-instruct-v5")


def test_runner_reports_missing_checkpoint():
    runner = ModelRunner()
    runner.select_backend(BACKEND_TRANSFORMERS)
    with pytest.raises(FileNotFoundError, match="adapter directory"):
        runner.load("D:\\LLM\\nonexistent")


def test_runner_reports_missing_runtime_dependencies(monkeypatch, tmp_path):
    monkeypatch.setattr(gui, "dependency_status", lambda: ("peft", "transformers"))
    runner = ModelRunner()
    runner.select_backend(BACKEND_TRANSFORMERS)

    with pytest.raises(RuntimeError, match="peft, transformers"):
        runner.load(tmp_path)


def test_model_runner_defaults_to_gguf_backend():
    assert ModelRunner().backend == BACKEND_GGUF


def test_model_runner_uses_gguf_only_when_selected():
    runner = ModelRunner()
    runner.backend = BACKEND_GGUF
    runner.gguf_runtime = type("Runtime", (), {"generate": lambda *_args: "GGUF reply"})()

    assert runner.generate_reply("hi", 32, 0.7, 20) == "GGUF reply"


def test_model_runner_reports_each_opencode_completion_timing():
    received: list[float] = []

    class Runtime:
        def generate(self, _messages, *_args, on_complete_timing=None):
            assert on_complete_timing is not None
            on_complete_timing(1.25)
            on_complete_timing(0.75)
            return "OpenCode reply"

    runner = ModelRunner()
    runner.backend = BACKEND_OPENCODE
    runner.openai_runtime = Runtime()

    assert runner.generate_reply(
        "hi", 32, 0.7, 20, on_complete_timing=received.append
    ) == "OpenCode reply"
    assert received == [1.25, 0.75]


def test_backend_labels_offer_opencode_but_not_openai() -> None:
    assert "OpenCode Zen API" in gui.BACKEND_LABELS
    assert "OpenAI API" not in gui.BACKEND_LABELS


def test_model_runner_adds_current_vtube_catalog_to_private_system_instruction():
    captured_messages = []

    class Runtime:
        def generate(self, messages, *_args):
            captured_messages.extend(messages)
            return "Ru reply"

    runner = ModelRunner()
    runner.backend = BACKEND_GGUF
    runner.gguf_runtime = Runtime()

    assert runner.generate_reply("hi", 32, 0.7, 20, vts_instruction="\nvts_01 = Wave") == "Ru reply"
    assert "vts_01 = Wave" in captured_messages[0][1]


def test_model_runner_does_not_search_without_explicit_opt_in(monkeypatch):
    monkeypatch.setattr(
        gui,
        "generate_llama_response_hf",
        lambda *_args, **_kwargs: "[SEARCH: weather]",
    )
    monkeypatch.setattr(
        gui,
        "perform_web_search",
        lambda _query: pytest.fail("search must not run"),
    )

    runner = ModelRunner()
    runner.select_backend(BACKEND_TRANSFORMERS)
    response = runner.generate_reply(
        "hi",
        32,
        0.7,
        20,
        allow_web_search=False,
    )

    assert response == "[SEARCH: weather]"


def test_clean_tts_text_removes_internal_markup():
    assert clean_tts_text("*Hi* [SEARCH: weather] <think>hidden</think>") == "Hi"


def test_generation_finished_hides_vts_metadata_before_every_output(monkeypatch):
    app = object.__new__(gui.RuApp)
    app.session_turns = []
    app.last_turn = None
    app.last_turn_saved = True
    displayed = []
    spoken = []
    dispatched = []
    catalog_dispatched = []
    app._append_turn = displayed.append
    app._set_controls_enabled = lambda _enabled: None
    app._update_mark_button = lambda: None
    app.status_var = type("Status", (), {"set": lambda _self, _text: None})()
    app.prompt_text = type(
        "Prompt",
        (),
        {"delete": lambda _self, *_args: None, "focus_set": lambda _self: None},
    )()
    app.tts_enabled_var = type("Enabled", (), {"get": lambda _self: True})()
    app.vts_manager = type(
        "VtsManager",
        (),
        {
            "trigger_actions_sync": lambda _self, actions: dispatched.append(actions),
            "trigger_catalog_actions_sync": lambda _self, catalog, codes: catalog_dispatched.append(
                (catalog, codes)
            ),
        },
    )()
    app._play_tts = lambda text, *_args: spoken.append(text)
    catalog = build_catalog(
        model_id="model-a",
        model_name="Model A",
        hotkeys=[{"hotkeyID": "hotkey-wave", "name": "Wave"}],
        expressions=[],
    )

    class ImmediateThread:
        def __init__(self, *, target, args, daemon):
            self.target = target
            self.args = args

        def start(self):
            self.target(*self.args)

    monkeypatch.setattr(gui.threading, "Thread", ImmediateThread)

    app._generation_finished(
        "Em vui qua! [[VTS:vts_01]]",
        cache_hit=True,
        elapsed=0.2,
        original_prompt="Tin vui ne",
        remember_conversations=False,
        vts_catalog=catalog,
    )

    assert app.last_turn.response == "Em vui qua!"
    assert displayed[0].response == "Em vui qua!"
    assert spoken == ["Em vui qua!"]
    assert catalog_dispatched == [(catalog, ("vts_01",))]
    assert dispatched == [VtsActions()]


def test_generation_finished_dispatches_direct_avatar_command_despite_text_only_reply():
    app = object.__new__(gui.RuApp)
    app.session_turns = []
    app.last_turn = None
    app.last_turn_saved = True
    dispatched = []
    app._append_turn = lambda _turn: None
    app._set_controls_enabled = lambda _enabled: None
    app._update_mark_button = lambda: None
    app.status_var = type("Status", (), {"set": lambda _self, _text: None})()
    app.prompt_text = type(
        "Prompt",
        (),
        {"delete": lambda _self, *_args: None, "focus_set": lambda _self: None},
    )()
    app.tts_enabled_var = type("Enabled", (), {"get": lambda _self: False})()
    app.vts_manager = type(
        "VtsManager",
        (),
        {"trigger_actions_sync": lambda _self, actions: dispatched.append(actions)},
    )()

    app._generation_finished(
        "Em khong the gio tay vi em la tro ly ao.",
        cache_hit=True,
        elapsed=0.2,
        original_prompt="Hay gio tay chao anh di",
        remember_conversations=False,
    )

    assert dispatched == [VtsActions(body="raise_hand")]


def test_request_tts_audio_explains_offline_server():
    def offline(*_args, **_kwargs):
        raise OSError("connection refused")

    with pytest.raises(RuntimeError, match="Voice server is offline"):
        request_tts_audio("hello", post=offline)


def test_ensure_gwen_tts_server_starts_local_service_when_offline(monkeypatch, tmp_path):
    availability = iter((False, False, True))
    started: list[tuple[list[str], dict]] = []
    runtime = tmp_path / "python.exe"
    server = tmp_path / "server.py"
    runtime.touch()
    server.touch()

    monkeypatch.setattr(gui, "gwen_tts_server_available", lambda: next(availability))
    monkeypatch.setattr(gui, "GWEN_TTS_PYTHON", runtime)
    monkeypatch.setattr(gui, "GWEN_TTS_SERVER_SCRIPT", server)

    gui.ensure_gwen_tts_server(
        process_factory=lambda command, **kwargs: started.append((command, kwargs)) or object(),
        sleep=lambda _seconds: None,
        monotonic=iter((0.0, 0.1, 0.2)).__next__,
    )

    assert started[0][0] == [str(runtime), str(server)]
    assert started[0][1]["cwd"] == str(gui.PROJECT_ROOT)


def test_ensure_gwen_tts_server_reports_missing_runtime(monkeypatch, tmp_path):
    monkeypatch.setattr(gui, "gwen_tts_server_available", lambda: False)
    monkeypatch.setattr(gui, "GWEN_TTS_PYTHON", tmp_path / "missing-python.exe")
    monkeypatch.setattr(gui, "GWEN_TTS_SERVER_SCRIPT", tmp_path / "missing-server.py")

    with pytest.raises(RuntimeError, match="Gwen runtime is missing"):
        gui.ensure_gwen_tts_server()


def test_request_tts_audio_uses_gwen_endpoint(tmp_path):
    audio = tmp_path / "voice.wav"
    audio.write_bytes(b"RIFF")
    requested_urls: list[str] = []

    class Response:
        status_code = 200

        @staticmethod
        def json():
            return {"audio_path": str(audio)}

    def post(url, **_kwargs):
        requested_urls.append(url)
        return Response()

    assert request_tts_audio("hello", post=post) == str(audio)
    assert requested_urls == [gui.GWEN_TTS_SERVER_URL]


def test_request_tts_audio_sends_selected_voice_profile(tmp_path) -> None:
    audio = tmp_path / "voice.wav"
    audio.write_bytes(b"RIFF")
    captured: dict[str, object] = {}

    class Response:
        status_code = 200

        @staticmethod
        def json():
            return {"audio_path": str(audio)}

    assert gui.request_tts_audio(
        "Xin chao",
        voice_profile="yen_nhi",
        post=lambda _url, **kwargs: captured.update(kwargs) or Response(),
    ) == str(audio)
    assert captured["json"] == {
        "text": "Xin chao",
        "fast_mode": False,
        "voice_profile": "yen_nhi",
    }


def test_gui_offers_only_gwen_voice_profiles() -> None:
    assert gui.VOICE_PROFILE_OPTIONS == {
        "Ru (Tsukuyomi)": "ru",
        "Yến Nhi (Gwen)": "yen_nhi",
    }
    assert "Edge TTS" not in Path(gui.__file__).read_text(encoding="utf-8")


def test_request_tts_audio_requires_a_playable_file(tmp_path):
    class Response:
        status_code = 200

        @staticmethod
        def json():
            return {"audio_path": str(tmp_path / "missing.wav")}

    with pytest.raises(RuntimeError, match="playable audio"):
        request_tts_audio("hello", post=lambda *_args, **_kwargs: Response())


def test_request_tts_audio_surfaces_server_error_detail():
    class Response:
        status_code = 400

        @staticmethod
        def json():
            return {"error": "Reference transcript is empty."}

    with pytest.raises(RuntimeError, match="Reference transcript is empty"):
        request_tts_audio("hello", post=lambda *_args, **_kwargs: Response())


def test_gwen_control_posts_to_warmup() -> None:
    requested: list[str] = []

    class Response:
        status_code = 200

        @staticmethod
        def json():
            return {"ready": True}

    assert gui.request_gwen_control(
        "/warmup",
        post=lambda url, **_kwargs: requested.append(url) or Response(),
    ) == {"ready": True}
    assert requested == ["http://127.0.0.1:5051/warmup"]


def test_play_gwen_chunks_requests_audio_in_order(monkeypatch, tmp_path) -> None:
    app = object.__new__(gui.RuApp)
    app._speech_generation = 1
    app.root = type("Root", (), {"after": lambda _self, _delay, callback: callback()})()
    app.status_var = type("Status", (), {"set": lambda _self, _text: None})()
    app.vts_manager = type("Vts", (), {"is_speaking": False})()
    requested: list[str] = []

    def synthesize(text: str, **_kwargs) -> str:
        requested.append(text)
        return str(tmp_path / f"{len(requested)}.wav")

    monkeypatch.setattr(gui, "request_tts_audio", synthesize)
    monkeypatch.setattr(gui, "play_wav_blocking", lambda *_args, **_kwargs: None)

    app._play_gwen_chunks(["Cau dau.", "Cau sau."], fast_mode=True)

    assert requested == ["Cau dau.", "Cau sau."]
    assert app.vts_manager.is_speaking is False


def test_play_gwen_chunks_reports_first_wav_timing(monkeypatch, tmp_path) -> None:
    app = object.__new__(gui.RuApp)
    app._speech_generation = 1
    app.root = type("Root", (), {"after": lambda _self, _delay, callback: callback()})()
    app.status_var = type("Status", (), {"set": lambda _self, _text: None})()
    app.vts_manager = type("Vts", (), {"is_speaking": False})()
    ticks = iter((20.0, 20.8))
    reported: list[float] = []

    monkeypatch.setattr(gui, "perf_counter", ticks.__next__)
    monkeypatch.setattr(
        gui,
        "request_tts_audio",
        lambda *_args, **_kwargs: str(tmp_path / "voice.wav"),
    )
    monkeypatch.setattr(gui, "play_wav_blocking", lambda *_args, **_kwargs: None)

    app._play_gwen_chunks(
        ["Cau dau."],
        fast_mode=True,
        on_first_audio_timing=reported.append,
    )

    assert reported == pytest.approx([0.8])
