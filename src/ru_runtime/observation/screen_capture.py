from __future__ import annotations

import ctypes
import io
import logging
import platform
from ctypes import wintypes

from PIL import Image

logger = logging.getLogger(__name__)


class _BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wintypes.DWORD),
        ("biWidth", wintypes.LONG),
        ("biHeight", wintypes.LONG),
        ("biPlanes", wintypes.WORD),
        ("biBitCount", wintypes.WORD),
        ("biCompression", wintypes.DWORD),
        ("biSizeImage", wintypes.DWORD),
        ("biXPelsPerMeter", wintypes.LONG),
        ("biYPelsPerMeter", wintypes.LONG),
        ("biClrUsed", wintypes.DWORD),
        ("biClrImportant", wintypes.DWORD),
    ]


def capture_screen_jpeg(max_width: int = 1280, max_height: int = 720, quality: int = 80) -> bytes:
    """Captures the primary monitor using native Win32 GDI and returns compressed JPEG bytes."""
    if platform.system() != "Windows":
        raise NotImplementedError("Screen capture is only supported on Windows.")

    user32 = ctypes.windll.user32
    gdi32 = ctypes.windll.gdi32

    width = user32.GetSystemMetrics(0)  # SM_CXSCREEN
    height = user32.GetSystemMetrics(1)  # SM_CYSCREEN

    hdesktop = user32.GetDesktopWindow()
    desktop_dc = user32.GetDC(hdesktop)
    mem_dc = gdi32.CreateCompatibleDC(desktop_dc)
    bmp = None
    old_bmp = None
    try:
        bmp = gdi32.CreateCompatibleBitmap(desktop_dc, width, height)
        old_bmp = gdi32.SelectObject(mem_dc, bmp)

        # SRCCOPY = 0x00CC0020
        gdi32.BitBlt(mem_dc, 0, 0, width, height, desktop_dc, 0, 0, 0x00CC0020)

        bmi = _BITMAPINFOHEADER()
        bmi.biSize = ctypes.sizeof(_BITMAPINFOHEADER)
        bmi.biWidth = width
        bmi.biHeight = -height  # Top-down DIB
        bmi.biPlanes = 1
        bmi.biBitCount = 32
        bmi.biCompression = 0  # BI_RGB

        buf = (ctypes.c_char * (width * height * 4))()
        gdi32.GetDIBits(mem_dc, bmp, 0, height, buf, ctypes.byref(bmi), 0)
        raw_bytes = bytes(buf)
    finally:
        if old_bmp and mem_dc:
            gdi32.SelectObject(mem_dc, old_bmp)
        if bmp:
            gdi32.DeleteObject(bmp)
        if mem_dc:
            gdi32.DeleteDC(mem_dc)
        if desktop_dc:
            user32.ReleaseDC(hdesktop, desktop_dc)
    img = Image.frombytes("RGBA", (width, height), raw_bytes, "raw", "BGRA")
    rgb_img = img.convert("RGB")

    # Resize to target bounds for fast transmission & lower token usage
    rgb_img.thumbnail((max_width, max_height), Image.Resampling.LANCZOS)

    buffer = io.BytesIO()
    rgb_img.save(buffer, format="JPEG", quality=quality)
    return buffer.getvalue()
