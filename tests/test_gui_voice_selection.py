from __future__ import annotations

from pathlib import Path

from mini_gpt import gui
from mini_gpt.voice_runtime import VOICE_SELECTIONS, voice_selection_by_label, voice_selection_from_saved, voice_service_config

PROJECT_ROOT = Path(__file__).resolve().parents[1]


class FakeResponse:
    status_code = 200

    def __init__(self, content: bytes) -> None:
        self.content = content

    def json(self) -> dict[str, str]:
        return {}


def test_gui_voice_labels_include_all_choices_and_migrate_legacy_label() -> None:
    assert [selection.label for selection in VOICE_SELECTIONS] == [
        "Ru (OmniVoice)",
        "Ái Vy (OmniVoice)",
    ]
    assert voice_selection_from_saved("Ru (Gwen)").label == "Ru (OmniVoice)"


def test_ai_vy_omnivoice_request_uses_its_service_and_minimal_payload(tmp_path) -> None:
    audio = tmp_path / "speech.wav"
    audio.write_bytes(b"RIFF")
    requests: list[tuple[str, dict[str, str]]] = []
    selection = voice_selection_by_label("Ái Vy (OmniVoice)")
    base_url = voice_service_config(selection, PROJECT_ROOT).base_url

    result = gui.request_tts_audio(
        "xin chào",
        selection,
        speed=0.82,
        post=lambda url, *, json, timeout: requests.append((url, json)) or FakeResponse(audio.read_bytes()),
    )

    assert result == b"RIFF"
    assert requests == [
        (f"{base_url}/tts", {"text": "xin chào", "voice_profile": "ai_vy", "speed": 0.82})
    ]


def test_selected_omnivoice_failure_never_calls_gwen() -> None:
    selection = voice_selection_by_label("Ru (OmniVoice)")
    base_url = voice_service_config(selection, PROJECT_ROOT).base_url
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

    assert requests == [f"{base_url}/tts"]


def test_omnivoice_service_starts_with_d_runtime_and_root_script(monkeypatch) -> None:
    selection = voice_selection_by_label("Ái Vy (OmniVoice)")
    config = voice_service_config(selection, PROJECT_ROOT)
    commands: list[dict[str, object]] = []
    availability = iter((False, True))
    monkeypatch.setattr(gui, "voice_service_available", lambda value: next(availability))
    monkeypatch.setattr(gui, "sleep", lambda seconds: None)

    gui.ensure_voice_service(
        selection,
        process_factory=lambda command, **kwargs: commands.append({"command": command, **kwargs}),
        monotonic=iter((0.0, 0.1)).__next__,
    )

    assert commands[0]["command"] == [str(config.python_path), str(config.server_script)]
