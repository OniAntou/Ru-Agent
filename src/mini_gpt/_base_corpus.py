from __future__ import annotations

import json
import os
from hashlib import sha256
from pathlib import Path
from typing import Any


def canonical_rows(rows: list[dict[str, object]]) -> str:
    serialized = "\n".join(
        json.dumps(row, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
        for row in rows
    )
    return sha256(serialized.encode("utf-8")).hexdigest()


def is_contextual(row: dict[str, object], check_history_only: bool = False) -> bool:
    messages = row["messages"]
    assert isinstance(messages, list)
    if check_history_only:
        return any(message["role"] == "assistant" for message in messages[1:-1])
    return any(message["role"] == "assistant" for message in messages[:-1])


def pair_key(row: dict[str, object]) -> str:
    return json.dumps(
        {"messages": row["messages"], "response": row["response"]},
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    )


def write_jsonl(path: Path, rows: list[dict[str, object]]) -> None:
    path.write_text(
        "\n".join(json.dumps(row, ensure_ascii=False) for row in rows) + "\n",
        encoding="utf-8",
    )


def write_splits_atomic(
    destination: Path,
    train: list[dict[str, object]],
    validation: list[dict[str, object]],
    held_out: list[dict[str, object]],
    report: dict[str, object],
) -> None:
    destination.mkdir(parents=True, exist_ok=True)
    paths = {
        "train": destination / "train.jsonl",
        "validation": destination / "val.jsonl",
        "held_out": destination / "held_out.jsonl",
    }
    partial_paths = {name: path.with_suffix(path.suffix + ".partial") for name, path in paths.items()}
    manifest_path = destination / "manifest.json"
    manifest_partial = manifest_path.with_suffix(".json.partial")
    try:
        write_jsonl(partial_paths["train"], train)
        write_jsonl(partial_paths["validation"], validation)
        write_jsonl(partial_paths["held_out"], held_out)
        manifest_partial.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        for name, path in paths.items():
            os.replace(partial_paths[name], path)
        os.replace(manifest_partial, manifest_path)
    except Exception:
        for path in (*partial_paths.values(), manifest_partial):
            path.unlink(missing_ok=True)
        raise


def validate_alternating_roles(messages: list[dict[str, str]], version: str) -> None:
    if len(messages) < 2:
        raise ValueError(f"{version} conversation must have at least 2 messages")
    turn_roles = [m["role"] for m in messages[1:]]
    if turn_roles[0] != "user" or turn_roles[-1] != "user":
        raise ValueError(f"{version} conversation must start and end with user")
    if any(left == right for left, right in zip(turn_roles, turn_roles[1:])):
        raise ValueError(f"{version} conversation roles must alternate")
