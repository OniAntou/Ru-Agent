from __future__ import annotations

import argparse
import json
import os
import subprocess
from datetime import datetime, timezone
from pathlib import Path, PurePosixPath

from .cleanup_policy import (
    CleanupCategory,
    CleanupDecision,
    classify_path,
    normalize_relative_path,
)


_EXACT_CANDIDATES = (
    ".venv311",
    "gwen_env",
    "gui-winui3.bak",
    "gui-winui3/pri_staging",
    "tools/ru_desktop_overlay/out",
    "logs",
    ".pytest_cache",
    "pytest_v.log",
    "rustup-init.exe",
    "resources.pri.xml",
    ".tmp/vcpkg",
    "tmp_verify_fresh",
    "tmp_verify_fresh2",
    "tmp_verify_fresh3",
    "eval_results/judge",
    "tyc_calm.wav",
    "src/mini_gpt/reference_text.txt",
    "scripts/test_inject.py",
    "scripts/test_keys.py",
    "scripts/test_keys_raw.py",
    "scripts/test_post_settings.py",
    "scripts/test_rest_key.py",
    "scripts/test_rest_key2.py",
    "scripts/test_settings.py",
    "scripts/test_tts.py",
)

_TRAVERSAL_PRUNE_ROOTS = (
    ".git",
    ".worktrees",
    ".venv",
    "omnivoice_env",
    ".venv311",
    "gwen_env",
    "gui-winui3.bak",
    "gui-winui3/pri_staging",
    "tools/ru_desktop_overlay/out",
    "logs",
    ".tmp",
    "checkpoints",
    "models",
    "data/conversations",
    "eval_results",
)

_LEGACY_SEARCH_ROOTS = (
    "checkpoints",
    "models",
    "data/conversations",
    "eval_results",
)

_GENERATED_DIRECTORY_NAMES = frozenset({"bin", "obj", "__pycache__", ".pytest_cache"})


def _git_output(root: Path, *arguments: str) -> str:
    completed = subprocess.run(
        ["git", *arguments],
        cwd=root,
        shell=False,
        check=True,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    return completed.stdout


def git_paths(root: Path, *arguments: str) -> tuple[str, ...]:
    output = _git_output(root, *arguments)
    fields = output.split("\0") if "\0" in output else output.splitlines()
    normalized = {
        normalize_relative_path(field)
        for field in fields
        if field
    }
    return tuple(sorted(normalized, key=str.casefold))


def git_dirty_paths(root: Path) -> frozenset[str]:
    output = _git_output(root, "status", "--porcelain=v1", "-z", "--untracked-files=all")
    fields = output.split("\0")
    dirty: set[str] = set()
    index = 0
    while index < len(fields):
        record = fields[index]
        index += 1
        if not record:
            continue
        if len(record) < 4 or record[2] != " ":
            raise ValueError("unexpected Git porcelain record")
        status = record[:2]
        dirty.add(normalize_relative_path(record[3:]))
        if ("R" in status or "C" in status) and index < len(fields) and fields[index]:
            dirty.add(normalize_relative_path(fields[index]))
            index += 1
    return frozenset(dirty)


def _at_or_below(value: str, root: str) -> bool:
    return value == root or value.startswith(f"{root}/")


def _overlaps(left: str, right: str) -> bool:
    return _at_or_below(left, right) or _at_or_below(right, left)


def _is_filesystem_link(path: Path) -> bool:
    try:
        if path.is_symlink():
            return True
        is_junction = getattr(path, "is_junction", None)
        return bool(is_junction and is_junction())
    except OSError:
        return True


def _is_generated_directory(relative_path: str) -> bool:
    name = PurePosixPath(relative_path).name
    return name in _GENERATED_DIRECTORY_NAMES or name.endswith(".egg-info")


def _is_pruned(relative_path: str) -> bool:
    return any(_at_or_below(relative_path, root) for root in _TRAVERSAL_PRUNE_ROOTS)


def _discover_generated_directories(workspace_root: Path) -> set[str]:
    discovered: set[str] = set()
    stack = [workspace_root]
    while stack:
        directory = stack.pop()
        try:
            entries = list(os.scandir(directory))
        except OSError:
            continue
        for entry in entries:
            relative = Path(entry.path).relative_to(workspace_root).as_posix()
            path = Path(entry.path)
            if _is_filesystem_link(path):
                if _is_generated_directory(relative):
                    discovered.add(relative)
                continue
            try:
                is_directory = entry.is_dir(follow_symlinks=False)
            except OSError:
                continue
            if not is_directory:
                continue
            if _is_generated_directory(relative):
                discovered.add(relative)
                continue
            if not _is_pruned(relative):
                stack.append(path)
    return discovered


def _discover_legacy_roots(workspace_root: Path) -> set[str]:
    discovered: set[str] = set()
    for relative_root in _LEGACY_SEARCH_ROOTS:
        root = workspace_root.joinpath(*PurePosixPath(relative_root).parts)
        if not root.is_dir() or _is_filesystem_link(root):
            continue
        try:
            children = list(os.scandir(root))
        except OSError:
            continue
        for child in children:
            relative = Path(child.path).relative_to(workspace_root).as_posix()
            decision = classify_path(relative, tracked=False, dirty_paths=frozenset())
            if decision.category is not CleanupCategory.PROTECTED:
                discovered.add(relative)
    return discovered


def _collapse_candidates(candidates: set[str]) -> tuple[str, ...]:
    ordered = sorted(
        candidates,
        key=lambda value: (len(PurePosixPath(value).parts), value.casefold()),
    )
    collapsed: list[str] = []
    for candidate in ordered:
        if any(_at_or_below(candidate, ancestor) for ancestor in collapsed):
            continue
        collapsed.append(candidate)
    return tuple(sorted(collapsed, key=str.casefold))


def discover_candidate_paths(
    workspace_root: Path,
    tracked_paths: tuple[str, ...],
) -> tuple[str, ...]:
    root = workspace_root.resolve()
    candidates: set[str] = set()

    for tracked_path in tracked_paths:
        relative = normalize_relative_path(tracked_path)
        decision = classify_path(relative, tracked=True, dirty_paths=frozenset())
        if (
            decision.category is CleanupCategory.LEGACY_LLAMA
            or decision.reason in {
                "dated_implementation_record",
                "one_off_runtime_probe",
                "duplicate_reference_text",
            }
        ):
            candidates.add(relative)

    for relative in _EXACT_CANDIDATES:
        path = root.joinpath(*PurePosixPath(relative).parts)
        if os.path.lexists(path):
            candidates.add(relative)

    candidates.update(_discover_generated_directories(root))
    candidates.update(_discover_legacy_roots(root))
    return _collapse_candidates(candidates)


def _scan_path(path: Path) -> tuple[int, int, str | None]:
    if path.is_file():
        try:
            return 1, path.stat().st_size, None
        except OSError as error:
            return 0, 0, type(error).__name__

    file_count = 0
    size_bytes = 0
    errors: set[str] = set()
    stack = [path]
    while stack:
        directory = stack.pop()
        try:
            entries = list(os.scandir(directory))
        except OSError as error:
            errors.add(type(error).__name__)
            continue
        for entry in entries:
            entry_path = Path(entry.path)
            if _is_filesystem_link(entry_path):
                continue
            try:
                if entry.is_file(follow_symlinks=False):
                    file_count += 1
                    size_bytes += entry.stat(follow_symlinks=False).st_size
                elif entry.is_dir(follow_symlinks=False):
                    stack.append(entry_path)
            except OSError as error:
                errors.add(type(error).__name__)
    scan_error = ",".join(sorted(errors)) if errors else None
    return file_count, size_bytes, scan_error


def _modified_utc(path: Path) -> str | None:
    try:
        modified = path.lstat().st_mtime
    except OSError:
        return None
    return datetime.fromtimestamp(modified, tz=timezone.utc).isoformat().replace("+00:00", "Z")


def build_entry(
    workspace_root: Path,
    relative_path: str,
    *,
    tracked: bool,
    dirty_paths: frozenset[str],
) -> dict[str, object]:
    root = workspace_root.resolve()
    relative = normalize_relative_path(relative_path)
    path = root.joinpath(*PurePosixPath(relative).parts)
    decision = classify_path(relative, tracked=tracked, dirty_paths=dirty_paths)
    is_link = os.path.lexists(path) and _is_filesystem_link(path)
    if is_link:
        decision = CleanupDecision(
            CleanupCategory.REVIEW_REQUIRED,
            "filesystem_link",
            "review",
        )

    exists = os.path.lexists(path)
    file_count, size_bytes, scan_error = (0, 0, None)
    if exists and not is_link:
        file_count, size_bytes, scan_error = _scan_path(path)

    normalized_dirty = tuple(normalize_relative_path(value) for value in dirty_paths)
    dirty_or_untracked = any(_overlaps(relative, dirty) for dirty in normalized_dirty)
    if decision.reason == "git_dirty_or_untracked":
        active_reference = "git_dirty_or_untracked"
    elif decision.category is CleanupCategory.PROTECTED:
        active_reference = "protected"
    elif decision.reason == "active_overlay_build_reference":
        active_reference = "active_build_reference"
    elif tracked and decision.category is CleanupCategory.LEGACY_LLAMA:
        active_reference = "tracked_legacy_name"
    else:
        active_reference = "none"

    entry: dict[str, object] = {
        "absolute_path": str(path.absolute()),
        "relative_path": relative,
        "classification": decision.category.value,
        "reason": decision.reason,
        "recoverability": decision.recoverability,
        "exists": exists,
        "tracked": tracked,
        "dirty_or_untracked": dirty_or_untracked,
        "file_count": file_count,
        "size_bytes": size_bytes,
        "modified_utc": _modified_utc(path) if exists else None,
        "active_reference": active_reference,
    }
    if scan_error:
        entry["scan_error"] = scan_error
    return entry


def _is_tracked_candidate(relative: str, tracked_paths: tuple[str, ...]) -> bool:
    return any(_at_or_below(tracked_path, relative) for tracked_path in tracked_paths)


def _build_summary(entries: list[dict[str, object]]) -> dict[str, object]:
    by_classification: dict[str, dict[str, int]] = {
        category.value: {"entry_count": 0, "file_count": 0, "size_bytes": 0}
        for category in CleanupCategory
    }
    for entry in entries:
        totals = by_classification[str(entry["classification"])]
        totals["entry_count"] += 1
        totals["file_count"] += int(entry["file_count"])
        totals["size_bytes"] += int(entry["size_bytes"])
    return {
        "entry_count": len(entries),
        "file_count": sum(int(entry["file_count"]) for entry in entries),
        "size_bytes": sum(int(entry["size_bytes"]) for entry in entries),
        "by_classification": by_classification,
    }


def build_manifest(workspace_root: Path) -> dict[str, object]:
    root = workspace_root.resolve()
    tracked_paths = git_paths(root, "ls-files", "-z")
    dirty_paths = git_dirty_paths(root)
    commit_values = git_paths(root, "rev-parse", "HEAD")
    candidates = discover_candidate_paths(root, tracked_paths)
    entries = [
        build_entry(
            root,
            relative,
            tracked=_is_tracked_candidate(relative, tracked_paths),
            dirty_paths=dirty_paths,
        )
        for relative in candidates
    ]
    entries.sort(key=lambda entry: str(entry["relative_path"]).casefold())
    return {
        "schema_version": 1,
        "workspace_root": str(root),
        "scanned_git_commit": commit_values[0] if commit_values else None,
        "dirty_path_count": len(dirty_paths),
        "entries": entries,
        "summary": _build_summary(entries),
    }


def write_manifest(manifest: dict[str, object], output: Path) -> None:
    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix(".json.tmp")
    temporary.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    temporary.replace(output)


def _validated_paths(parser: argparse.ArgumentParser, workspace: Path, output: Path) -> tuple[Path, Path]:
    if not workspace.is_absolute():
        parser.error("--workspace-root must be absolute")
    root = workspace.resolve()
    if root == Path(root.anchor):
        parser.error("--workspace-root must not be a drive root")
    if not root.is_dir():
        parser.error("--workspace-root must be an existing directory")

    if not output.is_absolute():
        parser.error("--output must be absolute")
    resolved_output = output.resolve(strict=False)
    if resolved_output.suffix.casefold() != ".json":
        parser.error("--output must be a JSON file")
    checkout_root = Path(__file__).resolve().parents[2]
    allowed_root = (checkout_root / "artifacts" / "cleanup").resolve(strict=False)
    try:
        resolved_output.relative_to(allowed_root)
    except ValueError:
        parser.error("--output must stay under this checkout's artifacts/cleanup directory")
    return root, resolved_output


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Build a read-only Ru cleanup inventory")
    parser.add_argument("--workspace-root", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args(argv)
    workspace_root, output = _validated_paths(parser, args.workspace_root, args.output)
    manifest = build_manifest(workspace_root)
    write_manifest(manifest, output)
    print(json.dumps(manifest["summary"], ensure_ascii=False, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
