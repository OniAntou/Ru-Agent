"""Canonical local data location for the active Ru product.

The canonical Windows business-data root is ``%LOCALAPPDATA%\\Ru``.
``RU_DATA_ROOT`` remains the complete test/isolation override. Structured
memory stays project-owned under ``data/conversations``.
"""

from __future__ import annotations

import os
from pathlib import Path

_MAX_DATA_ROOT_CHARS = 1_024


def _is_safe_data_root(value: str) -> bool:
    return bool(value) and len(value) <= _MAX_DATA_ROOT_CHARS and "\x00" not in value


def canonical_data_root() -> Path:
    configured = os.environ.get("RU_DATA_ROOT", "").strip()
    if configured:
        if not _is_safe_data_root(configured):
            raise ValueError("Invalid RU_DATA_ROOT.")
        candidate = Path(configured)
        if not candidate.is_absolute():
            raise ValueError("RU_DATA_ROOT must be an absolute path.")
        return candidate
    local_app_data = os.environ.get("LOCALAPPDATA", "").strip()
    if local_app_data:
        return Path(local_app_data) / "Ru"
    return Path.home() / "Ru"
