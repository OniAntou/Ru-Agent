from __future__ import annotations

import hashlib
import json
from pathlib import Path


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def training_dataset_fingerprint(dataset_dir: str | Path) -> dict[str, object]:
    directory = Path(dataset_dir)
    names = ("train.jsonl", "val.jsonl", "manifest.json")
    files = {}
    for name in names:
        path = directory / name
        if not path.is_file():
            raise FileNotFoundError(f"Training dataset input is missing: {path}")
        files[name] = _sha256_file(path)
    canonical = json.dumps(files, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return {"files": files, "aggregate_sha256": hashlib.sha256(canonical).hexdigest()}


def directory_fingerprint(directory: str | Path) -> str:
    root = Path(directory)
    if not root.is_dir():
        raise FileNotFoundError(f"Fingerprint directory is missing: {root}")
    digest = hashlib.sha256()
    for path in sorted(item for item in root.rglob("*") if item.is_file()):
        digest.update(path.relative_to(root).as_posix().encode("utf-8"))
        digest.update(b"\0")
        digest.update(_sha256_file(path).encode("ascii"))
        digest.update(b"\n")
    return digest.hexdigest()