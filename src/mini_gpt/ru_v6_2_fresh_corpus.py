from __future__ import annotations

from pathlib import Path

from .corpus_builder import build_authored_corpus
from .ru_style import ru_system_prompt_for
from .ru_v6_2_fresh_dialogues import (
    EXPECTED_LANGUAGE_COUNTS,
    EXPECTED_MODE_COUNTS,
    EXPECTED_TOTAL,
    EXPECTED_TURN_BAND_COUNTS,
    FRESH_AUTHORED_ROWS,
    LICENSE,
    SOURCE,
)


def build_ru_v6_2_fresh_corpus(
    output_dir: str | Path,
    *,
    approved_file: str | Path | None = None,
    memory_file: str | Path | None = None,
    seed: int = 62,
    write: bool = True,
) -> dict[str, object]:
    return build_authored_corpus(
        FRESH_AUTHORED_ROWS,
        output_dir=output_dir,
        expected_total=EXPECTED_TOTAL,
        expected_language_counts=EXPECTED_LANGUAGE_COUNTS,
        expected_mode_counts=EXPECTED_MODE_COUNTS,
        expected_turn_band_counts=EXPECTED_TURN_BAND_COUNTS,
        seed=seed,
        write=write,
        report_fields={
            "quality_gates": {
                "safe_non_explicit": True,
                "alternating_history": True,
                "duplicate_final_prompts": True,
                "language_specific_personality_prompts": True,
                "requires_nya_suffix": False,
                "vts_markers_out_of_text": True,
                "style_reference_only": "roleplay_chat.md",
            },
            "dataset_id": "ru_v6_2_fresh",
            "dataset_version": "v6.2_fresh",
            "lineage": "fresh-authored rows; v6.2 rows not carried over",
            "base_adapter": str(Path(__file__).resolve().parents[2] / "checkpoints" / "ru-llama3.1-8b-instruct-v6.2"),
        },
        extra_imports=(
            (Path(approved_file) if approved_file is not None else None),
            (Path(memory_file) if memory_file is not None else None),
        ),
    )
