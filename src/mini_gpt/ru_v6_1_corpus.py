from __future__ import annotations

from pathlib import Path

from .corpus_builder import build_standard_corpus
from .ru_v6_1_dialogues import (
    EXPECTED_INTENSITY_COUNTS,
    EXPECTED_LANGUAGE_COUNTS,
    EXPECTED_MODE_COUNTS,
    EXPECTED_TURN_BAND_COUNTS,
    LICENSE,
    SOURCE,
    V6_1_ROWS,
    V6_1_TOTAL_ROWS,
)


def build_ru_v6_1_corpus(
    output_dir: str | Path,
    *,
    seed: int = 42,
    write: bool = True,
) -> dict[str, object]:
    return build_standard_corpus(
        V6_1_ROWS,
        output_dir=output_dir,
        version="v6.1",
        expected_total=V6_1_TOTAL_ROWS,
        expected_split_counts={"train": 1680, "validation": 360, "held_out": 360},
        expected_language_counts=dict(EXPECTED_LANGUAGE_COUNTS),
        expected_mode_counts=dict(EXPECTED_MODE_COUNTS),
        expected_intensity_counts=dict(EXPECTED_INTENSITY_COUNTS),
        expected_turn_band_counts=dict(EXPECTED_TURN_BAND_COUNTS),
        source=SOURCE,
        license_name=LICENSE,
        allow_approved=True,
        approved_source="user-approved-local",
        naturalness_gate=True,
        casefold_prompts=True,
        seed=seed,
        write=write,
    )
