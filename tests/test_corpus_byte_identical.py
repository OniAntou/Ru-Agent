from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

import pytest

from verify_corpus_byte_identical import _sha256_manifest

ROOT = Path(__file__).resolve().parents[1]

VERSIONS = ["v6_1", "v6_2", "v6_2_fresh", "v6_3"]


def _write_manifest(path: Path, base_adapter: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(
            {
                "dataset_id": "fixture",
                "base_adapter": base_adapter,
                "total_examples": 1,
            },
            ensure_ascii=False,
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )


def test_manifest_hash_is_independent_of_checkout_root(tmp_path: Path) -> None:
    first = tmp_path / "first" / "manifest.json"
    second = tmp_path / "second" / "manifest.json"
    _write_manifest(first, r"C:\first-checkout\checkpoints\same-adapter")
    _write_manifest(second, r"D:\second-checkout\checkpoints\same-adapter")

    assert _sha256_manifest(first) == _sha256_manifest(second)


def test_manifest_hash_still_detects_a_different_adapter(tmp_path: Path) -> None:
    first = tmp_path / "first" / "manifest.json"
    second = tmp_path / "second" / "manifest.json"
    _write_manifest(first, r"C:\checkout\checkpoints\adapter-a")
    _write_manifest(second, r"C:\checkout\checkpoints\adapter-b")

    assert _sha256_manifest(first) != _sha256_manifest(second)


@pytest.mark.parametrize("base_adapter", (42, ""))
def test_manifest_hash_rejects_invalid_adapter_identity(
    tmp_path: Path,
    base_adapter: object,
) -> None:
    manifest = tmp_path / "manifest.json"
    manifest.write_text(
        json.dumps({"base_adapter": base_adapter}),
        encoding="utf-8",
    )

    with pytest.raises(ValueError, match="base_adapter"):
        _sha256_manifest(manifest)


@pytest.mark.parametrize("version", VERSIONS)
def test_corpus_rebuilds_refactor_neutral(version: str) -> None:
    baseline = ROOT / "scripts" / "corpus_baseline_hashes.json"
    if not baseline.is_file():
        pytest.skip("corpus baseline hashes file missing")
    result = subprocess.run(
        [sys.executable, "scripts/verify_corpus_byte_identical.py", "--version", version],
        cwd=ROOT,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    assert result.returncode == 0, f"stdout:\n{result.stdout}\nstderr:\n{result.stderr}"
