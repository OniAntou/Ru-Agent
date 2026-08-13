from __future__ import annotations

import re
from dataclasses import dataclass
from enum import Enum
from pathlib import PurePosixPath


class CleanupCategory(str, Enum):
    PROTECTED = "protected"
    REBUILDABLE = "rebuildable"
    LEGACY_LLAMA = "legacy_llama"
    REVIEW_REQUIRED = "review_required"


@dataclass(frozen=True, slots=True)
class CleanupDecision:
    category: CleanupCategory
    reason: str
    recoverability: str


_PROTECTED_ROOTS = (
    ".git",
    ".worktrees",
    ".venv",
    "omnivoice_env",
    ".minigpt",
    "models/lfm2.5-2.6b-hf",
    "models/gguf",
    "models/omnivoice",
    "models/vision",
    "checkpoints/ru-lfm2.5-2.6b-v1",
    "data/conversations/ru_v1",
    "data/conversations/approved.jsonl",
    "data/conversations/memory.db",
    "data/conversations/memory.db-wal",
    "data/conversations/memory.db-shm",
    "data/conversations/memory_texts.json",
    "data/gui_settings.json",
    "data/voices",
    "eval_results/lfm2_5_v1.json",
    "eval_results/omnivoice",
    "eval_results/omnivoice-pronunciation",
    "reference_text.txt",
    "tyc_talk.wav",
    "vts_token.txt",
    "docs/superpowers/specs/2026-08-13-ru-architecture-v2-refactor-design.md",
    "docs/superpowers/plans/2026-08-13-ru-architecture-spine-cleanup-preflight.md",
)

_REBUILDABLE_ROOTS = (
    ".venv311",
    "gwen_env",
    "pytest_v.log",
    "resources.pri.xml",
    "rustup-init.exe",
)

_REVIEW_REASONS = {
    "gui-winui3.bak": "contains_non_git_backup",
    "eval_results/judge": "mixed_evaluation_outputs",
    "scripts/test_inject.py": "one_off_runtime_probe",
    "scripts/test_keys.py": "one_off_runtime_probe",
    "scripts/test_keys_raw.py": "one_off_runtime_probe",
    "scripts/test_post_settings.py": "one_off_runtime_probe",
    "scripts/test_rest_key.py": "one_off_runtime_probe",
    "scripts/test_rest_key2.py": "one_off_runtime_probe",
    "scripts/test_settings.py": "one_off_runtime_probe",
    "scripts/test_tts.py": "one_off_runtime_probe",
    "src/mini_gpt/reference_text.txt": "duplicate_reference_text",
    "tyc_calm.wav": "unused_voice_reference_candidate",
}

_LEGACY_NAME = re.compile(
    r"(?:llama[_-]?3(?:[._-]?1)?|ru[_-]?v6(?:[._-]?[123])?|v6[_-]?[123])",
    re.IGNORECASE,
)


def normalize_relative_path(value: str) -> str:
    raw = value.replace("\\", "/")
    if raw.startswith("/") or re.match(r"^[A-Za-z]:", raw):
        raise ValueError("cleanup path must not be absolute or include a drive")
    normalized = raw.strip("/")
    path = PurePosixPath(normalized)
    if not normalized or normalized == "." or path.is_absolute() or ".." in path.parts:
        raise ValueError("cleanup path must be a narrow repository-relative path")
    return path.as_posix()


def _at_or_below(value: str, root: str) -> bool:
    return value == root or value.startswith(f"{root}/")


def _overlaps(left: str, right: str) -> bool:
    return _at_or_below(left, right) or _at_or_below(right, left)


def classify_path(
    relative_path: str,
    *,
    tracked: bool,
    dirty_paths: frozenset[str],
) -> CleanupDecision:
    relative = normalize_relative_path(relative_path)
    normalized_dirty = tuple(normalize_relative_path(path) for path in dirty_paths)

    if any(_overlaps(relative, dirty) for dirty in normalized_dirty):
        return CleanupDecision(
            CleanupCategory.PROTECTED,
            "git_dirty_or_untracked",
            "preserve",
        )

    if any(_at_or_below(relative, root) for root in _PROTECTED_ROOTS):
        return CleanupDecision(
            CleanupCategory.PROTECTED,
            "active_runtime_or_user_data",
            "preserve",
        )

    if _at_or_below(relative, ".tmp/vcpkg"):
        return CleanupDecision(
            CleanupCategory.REVIEW_REQUIRED,
            "active_overlay_build_reference",
            "review",
        )

    for root, reason in _REVIEW_REASONS.items():
        if _at_or_below(relative, root):
            return CleanupDecision(CleanupCategory.REVIEW_REQUIRED, reason, "review")

    if relative.startswith(("docs/superpowers/plans/", "docs/superpowers/specs/")):
        return CleanupDecision(
            CleanupCategory.REVIEW_REQUIRED,
            "dated_implementation_record",
            "review",
        )

    parts = PurePosixPath(relative).parts
    if (
        any(_at_or_below(relative, root) for root in _REBUILDABLE_ROOTS)
        or parts[0].startswith("tmp_verify_fresh")
        or any(part in {"bin", "obj", "__pycache__", ".pytest_cache"} for part in parts)
        or any(part.endswith(".egg-info") for part in parts)
    ):
        return CleanupDecision(CleanupCategory.REBUILDABLE, "generated_output", "rebuild")

    if _LEGACY_NAME.search(relative):
        recoverability = "git" if tracked else "permanent"
        return CleanupDecision(
            CleanupCategory.LEGACY_LLAMA,
            "legacy_llama_name",
            recoverability,
        )

    return CleanupDecision(CleanupCategory.REVIEW_REQUIRED, "unclassified", "review")
