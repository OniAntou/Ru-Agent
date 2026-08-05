from __future__ import annotations

from pathlib import Path

from .corpus_builder import build_standard_corpus
from .ru_style import RU_SYSTEM_PROMPT
from .ru_v6_2_dialogues import (
    EXPECTED_INTENSITY_COUNTS,
    EXPECTED_LANGUAGE_COUNTS,
    EXPECTED_MODE_COUNTS,
    EXPECTED_TURN_BAND_COUNTS,
    LICENSE,
    SOURCE,
    V6_2_ROWS,
    V6_2_TOTAL_ROWS,
)


def build_ru_v6_2_corpus(
    output_dir: str | Path,
    *,
    seed: int = 42,
    write: bool = True,
) -> dict[str, object]:
    return build_standard_corpus(
        V6_2_ROWS,
        output_dir=output_dir,
        version="v6.2",
        expected_total=V6_2_TOTAL_ROWS,
        expected_split_counts={"train": 1680, "validation": 360, "held_out": 360},
        expected_language_counts=dict(EXPECTED_LANGUAGE_COUNTS),
        expected_mode_counts=dict(EXPECTED_MODE_COUNTS),
        expected_intensity_counts=dict(EXPECTED_INTENSITY_COUNTS),
        expected_turn_band_counts=dict(EXPECTED_TURN_BAND_COUNTS),
        source=SOURCE,
        license_name=LICENSE,
        system_prompt=RU_SYSTEM_PROMPT,
        allow_approved=False,
        approved_source=None,
        naturalness_gate=False,
        casefold_prompts=False,
        seed=seed,
        write=write,
    )
