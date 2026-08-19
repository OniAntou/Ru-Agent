from __future__ import annotations

import io
import json
from unittest.mock import patch, MagicMock
from PIL import Image

from ru_runtime.observation.screen_reaction import VisionObservation
from ru_runtime.observation.screen_capture import capture_screen_jpeg
from ru_runtime.observation.gemini_vision import analyze_screenshot_with_gemini
from ru_runtime.observation.observer_loop import ScreenObserverLoop


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
