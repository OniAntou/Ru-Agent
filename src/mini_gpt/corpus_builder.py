from __future__ import annotations

import json
from collections import Counter
from pathlib import Path
from random import Random
from typing import Any

from ._base_corpus import canonical_rows, write_splits_atomic
from .corpus_validate import build_corpus


def build_standard_corpus(
    rows: list[dict[str, object]],
    output_dir: str | Path,
    *,
    version: str,
    expected_total: int,
    expected_split_counts: dict[str, int],
    expected_language_counts: dict[str, int],
    expected_mode_counts: dict[str, int],
    expected_intensity_counts: dict[str, int],
    expected_turn_band_counts: dict[str, int],
    source: str,
    license_name: str,
    system_prompt: str | None = None,
    allow_approved: bool = False,
    approved_source: str | None = None,
    naturalness_gate: bool = False,
    casefold_prompts: bool = False,
    seed: int = 42,
    write: bool = True,
) -> dict[str, object]:
    valid_languages = frozenset(expected_language_counts)
    valid_modes = frozenset(expected_mode_counts) | ({"user_approved_short_reply"} if allow_approved else set())
    valid_intensities = frozenset(expected_intensity_counts)
    valid_turn_bands = frozenset(expected_turn_band_counts) | ({"single_turn"} if allow_approved else set())
    return build_corpus(
        rows,
        output_dir=output_dir,
        version=version,
        expected_total=expected_total,
        expected_split_counts=expected_split_counts,
        expected_language_counts=expected_language_counts,
        expected_mode_counts=expected_mode_counts,
        expected_intensity_counts=expected_intensity_counts,
        expected_turn_band_counts=expected_turn_band_counts,
        valid_languages=valid_languages,
        valid_modes=valid_modes,
        valid_intensities=valid_intensities,
        valid_turn_bands=valid_turn_bands,
        source=source,
        license_name=license_name,
        system_prompt=system_prompt,
        allow_approved=allow_approved,
        approved_source=approved_source,
        naturalness_gate=naturalness_gate,
        casefold_prompts=casefold_prompts,
        seed=seed,
        write=write,
    )


def _split_rows(rows: list[dict[str, object]], seed: int) -> tuple[list[dict[str, object]], list[dict[str, object]], list[dict[str, object]]]:
    indices = list(range(len(rows)))
    Random(seed).shuffle(indices)
    held_end = len(rows) // 5
    val_end = held_end * 2
    held = [rows[index] for index in indices[:held_end]]
    validation = [rows[index] for index in indices[held_end:val_end]]
    train = [rows[index] for index in indices[val_end:]]
    return train, validation, held


def _check_duplicate_prompts(rows: list[dict[str, object]], *, casefold: bool = False) -> None:
    if casefold:
        prompts = [str(row["messages"][-1]["content"]).casefold() for row in rows]
    else:
        prompts = [str(row["messages"][-1]["content"]) for row in rows]
    if len(prompts) != len(set(prompts)):
        raise ValueError("authored core contains duplicate final prompts")


def _check_duplicate_pairs(rows: list[dict[str, object]]) -> None:
    pairs = [(str(row["messages"][-1]["content"]).casefold(), str(row["response"]).casefold()) for row in rows]
    if len(pairs) != len(set(pairs)):
        raise ValueError("authored core contains duplicate prompt/response pairs")


def build_authored_corpus(
    rows: list[dict[str, object]],
    output_dir: str | Path,
    *,
    expected_total: int,
    expected_language_counts: dict[str, int],
    expected_mode_counts: dict[str, int],
    expected_turn_band_counts: dict[str, int],
    seed: int,
    report_fields: dict[str, Any],
    write: bool = True,
    extra_imports: tuple[Path | None, Path | None] | None = None,
) -> dict[str, object]:
    if len(rows) != expected_total:
        raise ValueError(f"authored core must contain {expected_total} rows")
    if Counter(str(row["language"]) for row in rows) != expected_language_counts:
        raise ValueError("language quotas are invalid")
    if Counter(str(row["mode"]) for row in rows) != expected_mode_counts:
        raise ValueError("mode quotas are invalid")
    if Counter(str(row["turn_band"]) for row in rows) != expected_turn_band_counts:
        raise ValueError("turn-band quotas are invalid")
    _check_duplicate_prompts(rows)
    _check_duplicate_pairs(rows)
    train, validation, held_out = _split_rows(rows, seed)
    report: dict[str, object] = {
        "authored_total_examples": len(rows),
        "total_examples": len(rows),
        "split_counts": {"train": len(train), "validation": len(validation), "held_out": len(held_out)},
        "language_counts": dict(Counter(str(row["language"]) for row in rows)),
        "mode_counts": dict(Counter(str(row["mode"]) for row in rows)),
        "turn_band_counts": dict(Counter(str(row["turn_band"]) for row in rows)),
        "source_counts": dict(Counter(str(row["source"]) for row in rows)),
        "authored_sha256": canonical_rows(rows),
        "seed": seed,
        **report_fields,
    }
    if not write:
        return report
    destination = Path(output_dir)
    split_paths = (
        destination / "train.jsonl",
        destination / "val.jsonl",
        destination / "held_out.jsonl",
        destination / "manifest.json",
    )
    snapshots = [(path, path.read_bytes() if path.exists() else None) for path in split_paths]
    write_splits_atomic(destination, train, validation, held_out, report)
    if extra_imports is not None:
        approved_file, memory_file = extra_imports
        if approved_file is not None or memory_file is not None:
            from .ru_v6_2_fresh_import import append_imports_to_train, select_import_rows
            try:
                selection = select_import_rows(
                    approved_file or destination / "missing-approved.jsonl",
                    memory_file or destination / "missing-memory.json",
                )
                append_imports_to_train(destination, selection)
            except Exception:
                for path, content in snapshots:
                    if content is None:
                        path.unlink(missing_ok=True)
                    else:
                        path.write_bytes(content)
                raise
    report = json.loads((destination / "manifest.json").read_text(encoding="utf-8"))
    return report
