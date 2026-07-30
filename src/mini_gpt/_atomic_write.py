from __future__ import annotations

import os
import tempfile
from pathlib import Path
from typing import Callable


def atomic_write_json(
    path: str | Path,
    data: object,
    *,
    ensure_ascii: bool = False,
    indent: int | None = None,
    encoder: type | None = None,
) -> None:
    """Write JSON data to *path* atomically via a temporary file + os.replace.

    If the operation fails partway through, the temporary file is cleaned up
    and the target *path* is never touched.
    """
    import json

    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp_path = tempfile.mkstemp(
        dir=str(path.parent),
        suffix=".tmp",
        prefix=f".{path.name}_",
    )
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as tmp_f:
            json.dump(data, tmp_f, ensure_ascii=ensure_ascii, indent=indent, cls=encoder)
        os.replace(tmp_path, str(path))
    except Exception:
        _cleanup_tmp(tmp_path)
        raise


def atomic_write_text(path: str | Path, content: str) -> None:
    """Write text content to *path* atomically."""
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp_path = tempfile.mkstemp(
        dir=str(path.parent),
        suffix=".tmp",
        prefix=f".{path.name}_",
    )
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as tmp_f:
            tmp_f.write(content)
        os.replace(tmp_path, str(path))
    except Exception:
        _cleanup_tmp(tmp_path)
        raise


def atomic_append_jsonl(path: str | Path, line: str) -> None:
    """Append a single JSON line to *path* atomically.

    Uses a read-append-write cycle under a best-effort approach:
    reads existing content, writes everything to a temp file, then replaces.
    """
    import json as json_module

    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)

    existing = ""
    if path.is_file():
        try:
            existing = path.read_text(encoding="utf-8")
        except OSError:
            existing = ""

    # Validate the line is valid JSON
    try:
        json_module.loads(line)
    except json_module.JSONDecodeError:
        raise ValueError("line must be valid JSON")

    atomic_write_text(path, existing + line + "\n")


def _cleanup_tmp(tmp_path: str) -> None:
    try:
        Path(tmp_path).unlink(missing_ok=True)
    except OSError:
        pass
