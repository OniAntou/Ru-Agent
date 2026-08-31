"""Voice reference assets are part of the retained provider contract."""

from __future__ import annotations

import tomllib
import wave
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def test_voice_reference_text_is_nonempty_and_consistent_in_the_package() -> None:
    reference = (ROOT / "reference_text.txt").read_text(encoding="utf-8")
    assert reference.strip()
    assert (ROOT / "src/ru_runtime/reference_text.txt").read_text(encoding="utf-8") == reference


def test_voice_assets_are_packaged_and_the_audio_is_readable() -> None:
    project = tomllib.loads((ROOT / "pyproject.toml").read_text(encoding="utf-8"))
    assets = set(project["tool"]["setuptools"]["package-data"]["ru_runtime"])
    assert {"reference_text.txt", "tyc_talk.wav"} <= assets
    with wave.open(str(ROOT / "src/ru_runtime/tyc_talk.wav"), "rb") as audio:
        assert audio.getnframes() > 0
        assert audio.getframerate() > 0
