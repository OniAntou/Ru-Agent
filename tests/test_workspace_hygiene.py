from __future__ import annotations

from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]

OBSOLETE_TRACKED_PATHS = (
    "scripts/test_inject.py",
    "scripts/test_keys.py",
    "scripts/test_keys_raw.py",
    "scripts/test_post_settings.py",
    "scripts/test_rest_key.py",
    "scripts/test_rest_key2.py",
    "scripts/test_settings.py",
    "scripts/test_tts.py",
    "src/ru_runtime/reference_text.txt",
)


def test_reviewed_one_off_probes_and_duplicate_reference_are_absent() -> None:
    remaining = [relative for relative in OBSOLETE_TRACKED_PATHS if (ROOT / relative).exists()]
    assert remaining == []


def test_canonical_voice_reference_remains_at_repository_root() -> None:
    reference = ROOT / "reference_text.txt"
    assert reference.is_file()
    assert reference.read_text(encoding="utf-8").strip()
