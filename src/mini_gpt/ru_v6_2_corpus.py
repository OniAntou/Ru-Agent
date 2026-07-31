from __future__ import annotations

from pathlib import Path

from .corpus_validate import build_corpus
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


EXPECTED_TOTAL = V6_2_TOTAL_ROWS
EXPECTED_SPLIT_COUNTS = {"train": 1680, "validation": 360, "held_out": 360}
EXPECTED_LANGUAGE_COUNTS = dict(EXPECTED_LANGUAGE_COUNTS)
EXPECTED_MODE_COUNTS = dict(EXPECTED_MODE_COUNTS)
EXPECTED_INTENSITY_COUNTS = dict(EXPECTED_INTENSITY_COUNTS)
EXPECTED_TURN_BAND_COUNTS = dict(EXPECTED_TURN_BAND_COUNTS)

VALID_LANGUAGES = frozenset(EXPECTED_LANGUAGE_COUNTS)
VALID_MODES = frozenset(EXPECTED_MODE_COUNTS)
VALID_INTENSITIES = frozenset(EXPECTED_INTENSITY_COUNTS)
VALID_TURN_BANDS = frozenset(EXPECTED_TURN_BAND_COUNTS)


def build_ru_v6_2_corpus(
    output_dir: str | Path,
    *,
    seed: int = 42,
    write: bool = True,
) -> dict[str, object]:
    return build_corpus(
        V6_2_ROWS,
        output_dir=output_dir,
        version="v6.2",
        expected_total=EXPECTED_TOTAL,
        expected_split_counts=EXPECTED_SPLIT_COUNTS,
        expected_language_counts=EXPECTED_LANGUAGE_COUNTS,
        expected_mode_counts=EXPECTED_MODE_COUNTS,
        expected_intensity_counts=EXPECTED_INTENSITY_COUNTS,
        expected_turn_band_counts=EXPECTED_TURN_BAND_COUNTS,
        valid_languages=VALID_LANGUAGES,
        valid_modes=VALID_MODES,
        valid_intensities=VALID_INTENSITIES,
        valid_turn_bands=VALID_TURN_BANDS,
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
