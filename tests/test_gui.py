import pytest

from mini_gpt import gui
from mini_gpt.gui import (
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
    with pytest.raises(FileNotFoundError, match="adapter directory"):
        runner.load("D:\\LLM\\nonexistent")


def test_runner_reports_missing_runtime_dependencies(monkeypatch, tmp_path):
    monkeypatch.setattr(gui, "dependency_status", lambda: ("peft", "transformers"))

    with pytest.raises(RuntimeError, match="peft, transformers"):
        ModelRunner().load(tmp_path)


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

    response = ModelRunner().generate_reply(
        "hi",
        32,
        0.7,
        20,
        allow_web_search=False,
    )

    assert response == "[SEARCH: weather]"


def test_clean_tts_text_removes_internal_markup():
    assert clean_tts_text("*Hi* [SEARCH: weather] <think>hidden</think>") == "Hi"


def test_request_tts_audio_explains_offline_server():
    def offline(*_args, **_kwargs):
        raise OSError("connection refused")

    with pytest.raises(RuntimeError, match="Voice server is offline"):
        request_tts_audio("hello", post=offline)


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
