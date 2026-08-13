"""Control the RuDesktopOverlay.exe subprocess (Spout receiver).

Mô hình: GUI bật/tắt ``RuDesktopOverlay.exe`` (native app nhận Spout feed
từ VTS qua sender ``VTubeStudioSpout``) để render avatar lên desktop.
Mọi hàm trả bool/``overlay_error()`` thay vì raise, để trình điều khiển
HTTP có thể báo lỗi thân thiện mà không crash.
"""

from __future__ import annotations

import logging
import subprocess
import time
from pathlib import Path

logger = logging.getLogger("ru_runtime.overlay_control")

PROJECT_ROOT = Path(__file__).resolve().parent.parent.parent
OVERLAY_EXE_PATH = (
    PROJECT_ROOT
    / "tools"
    / "ru_desktop_overlay"
    / "out"
    / "build"
    / "Release"
    / "RuDesktopOverlay.exe"
)
OVERLAY_SENDER = "VTubeStudioSpout"
OVERLAY_IMAGE_NAME = "RuDesktopOverlay.exe"

_overlay_error: str | None = None


def overlay_process_ids(*, run=subprocess.run) -> tuple[int, ...]:
    """Trả tuple PID của các tiến trình overlay đang chạy (rỗng nếu không có)."""
    try:
        result = run(
            ["tasklist", "/FI", f"IMAGENAME eq {OVERLAY_IMAGE_NAME}", "/FO", "CSV", "/NH"],
            capture_output=True,
            text=True,
        )
    except OSError:
        return ()
    ids: list[int] = []
    for row in result.stdout.splitlines():
        cells = [c.strip().strip('"') for c in row.split(",")]
        if not cells or cells[0].casefold() != OVERLAY_IMAGE_NAME.casefold():
            continue
        try:
            ids.append(int(cells[1]))
        except (IndexError, ValueError):
            continue
    return tuple(ids)


def start_overlay() -> bool:
    global _overlay_error
    if not OVERLAY_EXE_PATH.is_file():
        _overlay_error = f"Overlay unavailable: {OVERLAY_EXE_PATH} not found"
        logger.warning(_overlay_error)
        return False
    try:
        subprocess.Popen(
            [str(OVERLAY_EXE_PATH), "--sender", OVERLAY_SENDER],
            cwd=str(PROJECT_ROOT),
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except OSError as error:
        _overlay_error = f"Overlay unavailable: {error}"
        logger.warning(_overlay_error)
        return False
    _overlay_error = None
    logger.info("Overlay started")
    return True


def stop_overlay() -> bool:
    global _overlay_error
    ids = overlay_process_ids()
    if not ids:
        _overlay_error = None
        return False
    try:
        kill = subprocess.run(
            ["taskkill", "/PID", *(str(pid) for pid in ids), "/T", "/F"],
            capture_output=True,
            text=True,
        )
        if kill.returncode != 0:
            _overlay_error = f"Overlay stop failed: taskkill rc={kill.returncode}: {kill.stderr or kill.stdout}"
            logger.warning(_overlay_error)
            return False
        time.sleep(0.3)
        remaining = overlay_process_ids()
        if remaining:
            _overlay_error = f"Overlay still running after kill: pids={remaining}"
            logger.warning(_overlay_error)
            return False
    except OSError as error:
        _overlay_error = f"Overlay stop failed: {error}"
        logger.warning(_overlay_error)
        return False
    _overlay_error = None
    logger.info("Overlay stopped (pids=%s)", ids)
    return True


def overlay_error() -> str | None:
    return _overlay_error
