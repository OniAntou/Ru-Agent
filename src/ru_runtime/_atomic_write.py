from __future__ import annotations

import json
import logging
import os
import stat
import tempfile
import threading
from pathlib import Path

logger = logging.getLogger(__name__)

_WRITE_LOCK = threading.RLock()


def _existing_mode(path: Path) -> int | None:
    try:
        return stat.S_IMODE(path.stat().st_mode)
    except OSError:
        return None


def _restore_mode(path: Path, mode: int | None) -> None:
    try:
        os.chmod(path, mode if mode is not None else 0o644)
    except OSError as exc:
        logger.warning("Could not set permissions on %s: %s", path, exc)


def atomic_write_json(
    path: str | Path,
    data: object,
    *,
    ensure_ascii: bool = False,
    indent: int | None = None,
    encoder: type[json.JSONEncoder] | None = None,
) -> None:
    """Write JSON data to *path* atomically via a temporary file + os.replace.

    If the operation fails partway through, the temporary file is cleaned up
    and the target *path* is never touched.

    Note: the lock only serializes writers within this process. Concurrent
    writers from other processes are not protected.
    """
    with _WRITE_LOCK:
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        existing_mode = _existing_mode(path)
        fd, tmp_path = tempfile.mkstemp(
            dir=str(path.parent),
            suffix=".tmp",
            prefix=f".{path.name}_",
        )
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as tmp_f:
                json.dump(data, tmp_f, ensure_ascii=ensure_ascii, indent=indent, cls=encoder)
                tmp_f.flush()
                os.fsync(tmp_f.fileno())
            os.replace(tmp_path, str(path))
            _restore_mode(path, existing_mode)
        except Exception:
            try:
                os.close(fd)
            except OSError:
                pass
            _cleanup_tmp(tmp_path)
            raise


def atomic_write_text(path: str | Path, content: str) -> None:
    """Write text content to *path* atomically."""
    with _WRITE_LOCK:
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        existing_mode = _existing_mode(path)
        fd, tmp_path = tempfile.mkstemp(
            dir=str(path.parent),
            suffix=".tmp",
            prefix=f".{path.name}_",
        )
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as tmp_f:
                tmp_f.write(content)
                tmp_f.flush()
                os.fsync(tmp_f.fileno())
            os.replace(tmp_path, str(path))
            _restore_mode(path, existing_mode)
        except Exception:
            try:
                os.close(fd)
            except OSError:
                pass
            _cleanup_tmp(tmp_path)
            raise


def atomic_append_jsonl(path: str | Path, line: str) -> None:
    """Append a single JSON line to *path* atomically.

    Uses a read-append-write cycle under an in-process lock: reads existing
    content, writes everything to a temp file, then replaces. Writers from
    other processes are not protected.
    """
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)

    try:
        json.loads(line)
    except json.JSONDecodeError:
        raise ValueError("line must be valid JSON")

    with _WRITE_LOCK:
        existing = ""
        if path.is_file():
            try:
                existing = path.read_text(encoding="utf-8")
            except OSError as exc:
                raise OSError(f"Cannot read existing JSONL file {path} before appending: {exc}") from exc
        atomic_write_text(path, existing + line + "\n")


def _cleanup_tmp(tmp_path: str) -> None:
    try:
        Path(tmp_path).unlink(missing_ok=True)
    except OSError:
        pass
