"""Canonical local data location for the active Ru product.

The canonical Windows business-data root is ``%LOCALAPPDATA%\\Ru``.
``RU_DATA_ROOT`` remains the complete test/isolation override. Structured
memory stays project-owned under ``data/conversations``.
"""

from __future__ import annotations

import os
from pathlib import Path


def canonical_data_root() -> Path:
    configured = os.environ.get("RU_DATA_ROOT", "").strip()
    if configured:
        return Path(configured)
    local_app_data = os.environ.get("LOCALAPPDATA", "").strip()
    if local_app_data:
        return Path(local_app_data) / "Ru"
    return Path.home() / "Ru"