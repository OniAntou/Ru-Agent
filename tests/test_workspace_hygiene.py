from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

DEAD_CODE_TRACKED_PATHS = (
    "src/ru_runtime/policy/safety_patterns.py",
    "run_tests.py",
    "scripts/probe_gemini_vision.py",
)

OBSOLETE_TRACKED_PATHS = (
    "scripts/test_inject.py",
    "scripts/test_keys.py",
    "scripts/test_keys_raw.py",
    "scripts/test_post_settings.py",
    "scripts/test_rest_key.py",
    "scripts/test_rest_key2.py",
    "scripts/test_settings.py",
    "scripts/test_tts.py",
)


def test_disconnected_tracked_entrypoints_are_absent() -> None:
    remaining = [path for path in DEAD_CODE_TRACKED_PATHS if (ROOT / path).exists()]
    assert remaining == []


def test_reviewed_one_off_probes_and_duplicate_reference_are_absent() -> None:
    remaining = [relative for relative in OBSOLETE_TRACKED_PATHS if (ROOT / relative).exists()]
    assert remaining == []


def test_canonical_voice_reference_remains_at_repository_root() -> None:
    reference = ROOT / "reference_text.txt"
    assert reference.is_file()
    assert reference.read_text(encoding="utf-8").strip()


def test_runtime_voice_reference_exists_in_package() -> None:
    reference = ROOT / "src" / "ru_runtime" / "reference_text.txt"
    assert reference.is_file()
    assert reference.read_text(encoding="utf-8").strip()


def test_package_and_root_voice_references_are_identical() -> None:
    root_reference = (ROOT / "reference_text.txt").read_text(encoding="utf-8")
    package_reference = (ROOT / "src" / "ru_runtime" / "reference_text.txt").read_text(
        encoding="utf-8"
    )
    assert package_reference == root_reference


def test_runtime_voice_sample_audio_is_tracked_and_present() -> None:
    sample = ROOT / "src" / "ru_runtime" / "tyc_talk.wav"
    assert sample.is_file()
    assert sample.stat().st_size > 10_000


def test_package_manifests_voice_assets() -> None:
    manifest = (ROOT / "pyproject.toml").read_text(encoding="utf-8")
    assert '[tool.setuptools.package-data]' in manifest
    assert 'ru_runtime = ["reference_text.txt", "tyc_talk.wav"]' in manifest
