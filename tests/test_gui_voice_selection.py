from __future__ import annotations

from pathlib import Path

from mini_gpt import gui
from mini_gpt.voice_runtime import VOICE_SELECTIONS, voice_selection_by_label, voice_selection_from_saved


class FakeResponse:
    status_code = 200

    def __init__(self, audio_path: str) -> None:
        self.audio_path = audio_path

    def json(self) -> dict[str, str]:
        return {"audio_path": self.audio_path}


def test_gui_voice_labels_include_all_choices_and_migrate_legacy_label() -> None:
    assert [selection.label for selection in VOICE_SELECTIONS] == [
        "Ru (Gwen)",
        "Yến Nhi (Gwen)",
        "Ru (OmniVoice — test)",
        "Yến Nhi (OmniVoice — test)",
    ]
    assert voice_selection_from_saved("Ru (Tsukuyomi)").label == "Ru (Gwen)"


def test_omnivoice_request_uses_its_service_and_minimal_payload(tmp_path) -> None:
    audio = tmp_path / "speech.wav"
    audio.write_bytes(b"RIFF")
    requests: list[tuple[str, dict[str, str]]] = []
    selection = voice_selection_by_label("Ru (OmniVoice — test)")

    result = gui.request_tts_audio(
        "xin chào",
        selection,
        post=lambda url, *, json, timeout: requests.append((url, json)) or FakeResponse(str(audio)),
    )

    assert result == str(audio)
    assert requests == [
        ("http://127.0.0.1:5052/tts", {"text": "xin chào", "voice_profile": "ru"})
    ]


def test_selected_omnivoice_failure_never_calls_gwen() -> None:
    selection = voice_selection_by_label("Ru (OmniVoice — test)")
    requests: list[str] = []

    try:
        gui.request_tts_audio(
            "xin chào",
            selection,
            post=lambda url, **kwargs: requests.append(url) or (_ for _ in ()).throw(OSError("offline")),
        )
    except RuntimeError as error:
        assert "Voice server is offline" in str(error)
    else:
        raise AssertionError("Selected OmniVoice error should be surfaced")

    assert requests == ["http://127.0.0.1:5052/tts"]


def test_omnivoice_service_starts_with_d_runtime_and_root_script(monkeypatch) -> None:
    selection = voice_selection_by_label("Yến Nhi (OmniVoice — test)")
    commands: list[dict[str, object]] = []
    availability = iter((False, True))
    monkeypatch.setattr(gui, "voice_service_available", lambda value: next(availability))
    monkeypatch.setattr(gui, "sleep", lambda seconds: None)

    gui.ensure_voice_service(
        selection,
        process_factory=lambda command, **kwargs: commands.append({"command": command, **kwargs}),
        monotonic=iter((0.0, 0.1)).__next__,
    )

    assert commands[0]["command"] == [
        str(Path("D:/LLM/omnivoice_env/Scripts/python.exe")),
        str(Path("D:/LLM/omnivoice_tts_server.py")),
    ]
