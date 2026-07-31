from __future__ import annotations

import json
import os
from hashlib import sha256
from pathlib import Path
from typing import Any

from ._base_corpus import canonical_rows, write_jsonl
from ._corpus_quality import casual_rejection_reason
from ._safety_patterns import AI_DISCLAIMER, GRAPHIC_CONTENT, UNSAFE_CONTENT
from ._text_utils import normalize_text
from .ru_style import RU_SYSTEM_PROMPT
from .ru_v6_1_naturalness import normalized_final_prompt


SELECTED_IDS = (
    "5012176f-d8cf-4fd7-bba0-f94bac083b78",
    "0e124abf-c87a-47e6-be7d-4c1b73c02c72",
    "6e7f8d38-3b8d-41e1-b77e-7410c8ab44c4",
)
IMPORTED_SOURCE = "user-approved-local"


def _quality_gate_reason(prompt: str, response: str) -> str | None:
    combined = f"{prompt}\n{response}"
    if AI_DISCLAIMER.search(combined):
        return "ai_disclaimer"
    if GRAPHIC_CONTENT.search(combined) or UNSAFE_CONTENT.search(combined):
        return "explicit_or_unsafe_content"
    return casual_rejection_reason(prompt, response)


def _read_jsonl(path: Path) -> list[dict[str, Any]]:
    try:
        return [
            json.loads(line)
            for line in path.read_text(encoding="utf-8").splitlines()
            if line.strip()
        ]
    except FileNotFoundError as error:
        raise ValueError(f"missing dataset file: {path}") from error


def _approved_training_row(raw: dict[str, Any]) -> dict[str, object]:
    identifier = raw.get("id")
    prompt = raw.get("prompt")
    response = raw.get("response")
    if not all(isinstance(value, str) for value in (identifier, prompt, response)):
        raise ValueError("approved row must contain text id, prompt, and response")
    prompt = normalize_text(prompt)
    response = normalize_text(response)
    if not prompt or not response:
        raise ValueError("approved row prompt and response cannot be empty")
    return {
        "messages": [
            {"role": "system", "content": RU_SYSTEM_PROMPT},
            {"role": "user", "content": prompt},
        ],
        "response": response,
        "language": "vi",
        "mode": "user_approved_short_reply",
        "intensity": "light",
        "turn_band": "single_turn",
        "source": IMPORTED_SOURCE,
        "license": "project-owned",
        "approval_id": identifier,
    }


def select_approved_rows(
    raw_rows: list[dict[str, Any]],
) -> tuple[list[dict[str, object]], list[dict[str, str]]]:
    rows_by_id: dict[str, dict[str, Any]] = {}
    for raw in raw_rows:
        identifier = raw.get("id")
        if not isinstance(identifier, str):
            raise ValueError("approved row is missing its id")
        if identifier in rows_by_id:
            raise ValueError(f"approved source contains duplicate id: {identifier}")
        rows_by_id[identifier] = raw

    missing = [identifier for identifier in SELECTED_IDS if identifier not in rows_by_id]
    if missing:
        raise ValueError(f"approved source is missing selected ids: {', '.join(missing)}")

    selected = [_approved_training_row(rows_by_id[identifier]) for identifier in SELECTED_IDS]
    prompts = [normalized_final_prompt(row) for row in selected]
    if len(prompts) != len(set(prompts)):
        raise ValueError("selected approved rows have a casefold prompt collision")

    selected_prompt_set = set(prompts)
    rejected = [
        {
            "id": identifier,
            "reason": (
                "casefold_prompt_duplicate"
                if normalize_text(str(raw.get("prompt", ""))).casefold() in selected_prompt_set
                else "not_selected"
            ),
        }
        for identifier, raw in rows_by_id.items()
        if identifier not in SELECTED_IDS
    ]
    return selected, sorted(rejected, key=lambda item: item["id"])


def _source_sha256(path: Path) -> str:
    return sha256(path.read_bytes()).hexdigest()


def _write_train_and_manifest_atomically(
    dataset_dir: Path,
    train: list[dict[str, object]],
    manifest: dict[str, object],
) -> None:
    train_path = dataset_dir / "train.jsonl"
    manifest_path = dataset_dir / "manifest.json"
    train_partial = train_path.with_suffix(".jsonl.partial")
    manifest_partial = manifest_path.with_suffix(".json.partial")
    try:
        write_jsonl(train_partial, train)
        manifest_partial.write_text(
            json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        os.replace(train_partial, train_path)
        os.replace(manifest_partial, manifest_path)
    except Exception:
        train_partial.unlink(missing_ok=True)
        manifest_partial.unlink(missing_ok=True)
        raise


def merge_approved_into_v6_1(
    dataset_dir: str | Path,
    approved_file: str | Path,
) -> dict[str, object]:
    dataset_dir = Path(dataset_dir)
    approved_path = Path(approved_file)
    selected, rejected = select_approved_rows(_read_jsonl(approved_path))
    train = _read_jsonl(dataset_dir / "train.jsonl")
    validation = _read_jsonl(dataset_dir / "val.jsonl")
    held_out = _read_jsonl(dataset_dir / "held_out.jsonl")
    manifest = json.loads((dataset_dir / "manifest.json").read_text(encoding="utf-8"))

    base_train = [row for row in train if row.get("source") != IMPORTED_SOURCE]
    existing_prompts = {
        normalized_final_prompt(row)
        for row in [*base_train, *validation, *held_out]
    }
    collisions = sorted(
        normalized_final_prompt(row)
        for row in selected
        if normalized_final_prompt(row) in existing_prompts
    )
    if collisions:
        raise ValueError(f"approved prompt collision: {', '.join(collisions)}")

    merged_train = [*base_train, *selected]
    overrides = [
        {
            "id": str(row["approval_id"]),
            "reason": reason,
            "note": "approved",
        }
        for row in selected
        for reason in [_quality_gate_reason(str(row["messages"][-1]["content"]), str(row["response"]))]
        if reason is not None
    ]
    base_total = int(manifest.get("authored_total_examples", manifest["total_examples"]))
    split_counts = {
        "train": len(merged_train),
        "validation": len(validation),
        "held_out": len(held_out),
    }
    manifest["authored_total_examples"] = base_total
    manifest["total_examples"] = sum(split_counts.values())
    manifest["split_counts"] = split_counts
    manifest["approved_import"] = {
        "source_file": approved_path.name,
        "source_sha256": _source_sha256(approved_path),
        "selected_ids": list(SELECTED_IDS),
        "rejected": rejected,
        "accepted_count": len(selected),
        "quality_overrides": overrides,
        "final_train_count": len(merged_train),
        "final_train_sha256": canonical_rows(merged_train),
    }
    _write_train_and_manifest_atomically(dataset_dir, merged_train, manifest)
    return manifest
