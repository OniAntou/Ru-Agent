from __future__ import annotations

import json
import subprocess
from pathlib import Path
from typing import Any, cast

import pytest

from tools.maintenance.cleanup_inventory import (
    build_entry,
    build_manifest,
    discover_candidate_paths,
    git_dirty_paths,
    main,
    write_manifest,
)


def test_build_entry_records_metadata_without_contents(tmp_path: Path) -> None:
    candidate = tmp_path / "tmp_verify_fresh"
    candidate.mkdir()
    (candidate / "train.jsonl").write_text("private training row", encoding="utf-8")

    entry = build_entry(
        tmp_path,
        "tmp_verify_fresh",
        tracked=False,
        dirty_paths=frozenset(),
    )

    assert entry["relative_path"] == "tmp_verify_fresh"
    assert entry["classification"] == "rebuildable"
    assert entry["file_count"] == 1
    assert entry["size_bytes"] == len(b"private training row")
    assert "private training row" not in json.dumps(entry)


def test_build_entry_does_not_traverse_filesystem_links(tmp_path: Path) -> None:
    target = tmp_path / "target"
    target.mkdir()
    (target / "private.txt").write_text("do not scan", encoding="utf-8")
    link = tmp_path / "tmp_verify_fresh-link"
    try:
        link.symlink_to(target, target_is_directory=True)
    except OSError as error:
        pytest.skip(f"filesystem links unavailable: {type(error).__name__}")

    entry = build_entry(
        tmp_path,
        link.name,
        tracked=False,
        dirty_paths=frozenset(),
    )

    assert entry["classification"] == "review_required"
    assert entry["reason"] == "filesystem_link"
    assert entry["file_count"] == 0
    assert entry["size_bytes"] == 0


def test_git_dirty_paths_includes_both_sides_of_rename_and_untracked(tmp_path: Path) -> None:
    subprocess.run(["git", "init", "-q"], cwd=tmp_path, check=True, shell=False)
    subprocess.run(
        ["git", "config", "user.email", "cleanup-test@example.invalid"],
        cwd=tmp_path,
        check=True,
        shell=False,
    )
    subprocess.run(
        ["git", "config", "user.name", "Cleanup Test"],
        cwd=tmp_path,
        check=True,
        shell=False,
    )
    old = tmp_path / "old.txt"
    old.write_text("tracked", encoding="utf-8")
    subprocess.run(["git", "add", "old.txt"], cwd=tmp_path, check=True, shell=False)
    subprocess.run(
        ["git", "commit", "-q", "-m", "fixture"],
        cwd=tmp_path,
        check=True,
        shell=False,
    )
    subprocess.run(
        ["git", "mv", "old.txt", "new.txt"],
        cwd=tmp_path,
        check=True,
        shell=False,
    )
    (tmp_path / "untracked.txt").write_text("private", encoding="utf-8")

    assert git_dirty_paths(tmp_path) == frozenset(
        {"old.txt", "new.txt", "untracked.txt"}
    )


def test_discovery_is_bounded_and_collapses_candidate_ancestors(tmp_path: Path) -> None:
    (tmp_path / ".venv311" / "Lib").mkdir(parents=True)
    (tmp_path / "backend-dotnet" / "RuBackend" / "bin" / "nested").mkdir(
        parents=True
    )
    (tmp_path / "gui-winui3" / "pri_staging").mkdir(parents=True)
    (tmp_path / "logs").mkdir()
    (tmp_path / ".tmp").mkdir()
    (tmp_path / ".tmp" / "lfm-smoke.err.log").write_text("private", encoding="utf-8")
    (tmp_path / ".venv" / "Lib" / "__pycache__").mkdir(parents=True)
    (tmp_path / "checkpoints" / "ru-hard-evaluation-v1" / "nested").mkdir(
        parents=True
    )
    tracked = (
        "src/ru_runtime/ru_v6_3_corpus.py",
    )

    candidates = discover_candidate_paths(tmp_path, tracked)

    assert ".venv311" in candidates
    assert "backend-dotnet/RuBackend/bin" in candidates
    assert "backend-dotnet/RuBackend/bin/nested" not in candidates
    assert "gui-winui3/pri_staging" in candidates
    assert "logs" in candidates
    assert ".tmp/lfm-smoke.err.log" in candidates
    assert "checkpoints/ru-hard-evaluation-v1" in candidates
    assert "src/ru_runtime/ru_v6_3_corpus.py" in candidates
    assert not any(value.startswith(".venv/") for value in candidates)


def test_manifest_keeps_dirty_legacy_path_protected(tmp_path: Path, monkeypatch) -> None:
    path = tmp_path / "configs" / "ru_llama3_1_v6_3.json"
    path.parent.mkdir(parents=True)
    path.write_text("changed", encoding="utf-8")

    def fake_git_paths(_root: Path, *arguments: str) -> tuple[str, ...]:
        if arguments == ("rev-parse", "HEAD"):
            return ("fixture-commit",)
        return ("configs/ru_llama3_1_v6_3.json",)

    monkeypatch.setattr("tools.maintenance.cleanup_inventory.git_paths", fake_git_paths)
    monkeypatch.setattr(
        "tools.maintenance.cleanup_inventory.git_dirty_paths",
        lambda _root: frozenset({"configs/ru_llama3_1_v6_3.json"}),
    )

    manifest = cast(dict[str, Any], build_manifest(tmp_path))
    entry = next(
        value
        for value in manifest["entries"]
        if value["relative_path"].endswith("ru_llama3_1_v6_3.json")
    )
    assert entry["classification"] == "protected"
    assert manifest["dirty_path_count"] == 1


def test_manifest_has_stable_schema_and_sorted_entries(tmp_path: Path, monkeypatch) -> None:
    (tmp_path / ".venv311").mkdir()
    (tmp_path / "rustup-init.exe").write_bytes(b"installer")

    def fake_git_paths(_root: Path, *arguments: str) -> tuple[str, ...]:
        return ("fixture-commit",) if arguments == ("rev-parse", "HEAD") else ()

    monkeypatch.setattr("tools.maintenance.cleanup_inventory.git_paths", fake_git_paths)
    monkeypatch.setattr(
        "tools.maintenance.cleanup_inventory.git_dirty_paths",
        lambda _root: frozenset(),
    )

    manifest = cast(dict[str, Any], build_manifest(tmp_path))
    assert manifest["schema_version"] == 1
    assert manifest["workspace_root"] == str(tmp_path.resolve())
    assert manifest["scanned_git_commit"] == "fixture-commit"
    relative = [entry["relative_path"] for entry in manifest["entries"]]
    assert relative == sorted(relative, key=str.casefold)
    assert set(manifest["summary"]) == {
        "entry_count",
        "file_count",
        "size_bytes",
        "by_classification",
    }
    assert manifest["summary"]["entry_count"] == 2


def test_write_manifest_is_atomic_and_json_round_trips(tmp_path: Path) -> None:
    output = tmp_path / "artifacts" / "cleanup" / "manifest.json"
    manifest = {"schema_version": 1, "entries": [], "summary": {}}
    write_manifest(manifest, output)
    assert json.loads(output.read_text(encoding="utf-8")) == manifest
    assert not output.with_suffix(".json.tmp").exists()


def test_inventory_source_exposes_no_delete_capability() -> None:
    root = Path(__file__).resolve().parents[1]
    source = (root / "tools" / "maintenance" / "cleanup_inventory.py").read_text(
        encoding="utf-8"
    )
    forbidden = ("unlink(", "rmtree(", "remove(", "removedirs(", "Delete(", "Remove-Item")
    assert not any(value in source for value in forbidden)


def test_cli_rejects_output_outside_its_checkout(tmp_path: Path) -> None:
    output = tmp_path / "artifacts" / "cleanup" / "manifest.json"
    with pytest.raises(SystemExit) as error:
        main(
            [
                "--workspace-root",
                str(tmp_path.resolve()),
                "--output",
                str(output.resolve()),
            ]
        )
    assert error.value.code == 2
