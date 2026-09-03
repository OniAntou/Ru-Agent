from __future__ import annotations

import io
import json
from unittest.mock import MagicMock, patch

from PIL import Image

from ru_runtime.observation.gemini_vision import analyze_screenshot_with_gemini
from ru_runtime.observation.observer_loop import ScreenObserverLoop
from ru_runtime.observation.screen_capture import capture_screen_jpeg
from ru_runtime.observation.screen_reaction import VisionObservation


def test_screen_capture_jpeg():
    # Test that capture_screen_jpeg produces valid JPEG bytes
    jpeg_bytes = capture_screen_jpeg(max_width=320, max_height=240, quality=50)
    assert isinstance(jpeg_bytes, bytes)
    assert len(jpeg_bytes) > 0
    # Verify PIL can open the returned JPEG bytes
    img = Image.open(io.BytesIO(jpeg_bytes))
    assert img.format == "JPEG"
    assert img.width <= 320
    assert img.height <= 240


def test_analyze_screenshot_with_gemini_success():
    fake_gemini_response = {
        "candidates": [
            {
                "content": {
                    "parts": [
                        {
                            "text": json.dumps({
                                "scene": "Visual Studio Code - Python project",
                                "event": "User writing code and testing units",
                                "confidence": 0.95,
                                "novelty": 0.8,
                                "comment_worthiness": 0.75,
                                "visible_text": ["test_screen.py", "pytest"],
                            })
                        }
                    ]
                }
            }
        ]
    }

    mock_resp = MagicMock()
    mock_resp.read.return_value = json.dumps(fake_gemini_response).encode("utf-8")
    mock_resp.__enter__.return_value = mock_resp

    with patch("urllib.request.urlopen", return_value=mock_resp):
        obs = analyze_screenshot_with_gemini(
            image_bytes=b"fake_jpeg",
            api_key="fake_key_123",
        )

    assert obs is not None
    assert isinstance(obs, VisionObservation)
    assert obs.scene == "Visual Studio Code - Python project"
    assert obs.event == "User writing code and testing units"
    assert obs.confidence == 0.95
    assert obs.comment_worthiness == 0.75
    assert "test_screen.py" in obs.visible_text


def test_analyze_screenshot_missing_key():
    obs = analyze_screenshot_with_gemini(image_bytes=b"fake_jpeg", api_key="")
    assert obs is None


def test_gemini_vision_rejects_an_oversized_response_before_json_parsing():
    mock_resp = MagicMock()
    mock_resp.read.return_value = b"x" * (4 * 1024 * 1024 + 1)
    mock_resp.__enter__.return_value = mock_resp

    with patch("urllib.request.urlopen", return_value=mock_resp):
        assert analyze_screenshot_with_gemini(b"fake_jpeg", "fake_key", model_name="gemini-3.1-flash-lite") is None


def test_gemini_37_request_uses_header_auth_and_omits_deprecated_sampling_fields():
    fake_gemini_response = {
        "candidates": [
            {"content": {"parts": [{"text": json.dumps({"scene": "Desktop"})}]}}
        ]
    }
    captured: dict[str, object] = {}

    mock_resp = MagicMock()
    mock_resp.read.return_value = json.dumps(fake_gemini_response).encode("utf-8")
    mock_resp.__enter__.return_value = mock_resp

    def capture_request(request, timeout):
        captured["url"] = request.full_url
        captured["api_key"] = next(
            value
            for key, value in request.header_items()
            if key.casefold() == "x-goog-api-key"
        )
        captured["payload"] = json.loads(request.data.decode("utf-8"))
        return mock_resp

    with patch("urllib.request.urlopen", side_effect=capture_request):
        obs = analyze_screenshot_with_gemini(
            image_bytes=b"fake_jpeg",
            api_key="fake_key_123",
            model_name="gemini-3.7-flash",
        )

    assert obs is not None
    assert captured["url"] == (
        "https://generativelanguage.googleapis.com/v1beta/models/"
        "gemini-3.7-flash:generateContent"
    )
    assert captured["api_key"] == "fake_key_123"
    payload = captured["payload"]
    assert isinstance(payload, dict)
    assert "temperature" not in payload["generationConfig"]


def test_gemini_31_request_retains_supported_temperature():
    mock_resp = MagicMock()
    mock_resp.read.return_value = json.dumps(
        {"candidates": [{"content": {"parts": [{"text": '{"scene":"Desktop"}'}]}}]}
    ).encode("utf-8")
    mock_resp.__enter__.return_value = mock_resp
    captured: dict[str, object] = {}

    def capture_request(request, timeout):
        captured["payload"] = json.loads(request.data.decode("utf-8"))
        return mock_resp

    with patch("urllib.request.urlopen", side_effect=capture_request):
        analyze_screenshot_with_gemini(
            image_bytes=b"fake_jpeg",
            api_key="fake_key_123",
            model_name="gemini-3.1-flash-lite",
        )

    assert captured["payload"]["generationConfig"]["temperature"] == 0.7


def test_screen_observer_loop_start_stop():
    mock_shared = MagicMock()
    mock_shared._TURN_LOCK.locked.return_value = False
    mock_shared.core.gemini_vision_cred.get_key.return_value = "fake_key"

    loop = ScreenObserverLoop(mock_shared, interval_seconds=10.0)
    assert loop.is_running is False

    loop.start()
    assert loop.is_running is True

    status = loop.status()
    assert status["running"] is True
    assert status["interval_seconds"] == 10.0

    loop.stop()
    assert loop.is_running is False


def test_observer_publishes_reaction_without_playing_audio_in_worker(monkeypatch):
    import ru_runtime.observation.observer_loop as observer_module

    observation = VisionObservation(
        scene="Editor",
        event="Writing tests",
        confidence=0.95,
        novelty=0.8,
        comment_worthiness=0.9,
        reaction="Anh đang test đấy à.",
    )
    shared = MagicMock()
    shared._TURN_LOCK.locked.return_value = False
    shared.core.gemini_vision_cred.get_key.return_value = "fake_key"
    shared._strip_voice_markers.side_effect = lambda text: text

    monkeypatch.setattr(observer_module, "capture_screen_jpeg", lambda **kwargs: b"jpeg")
    monkeypatch.setattr(
        observer_module,
        "analyze_screenshot_with_gemini",
        lambda **kwargs: observation,
    )

    loop = observer_module.ScreenObserverLoop(shared, interval_seconds=1.0)
    loop._observe_once()

    status = loop.status()
    assert status["frame_count"] == 1
    assert status["vision_count"] == 1
    assert status["emitted_count"] == 1
    assert status["last_reaction_id"]
    assert status["last_reaction"] == "Anh đang test đấy à."
    shared.request_voice_audio.assert_not_called()
