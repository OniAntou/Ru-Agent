from __future__ import annotations

import ctypes
import datetime
import logging
import os
import platform
import subprocess
import webbrowser

logger = logging.getLogger(__name__)

# Vietnamese Day of Week mapping
_WEEKDAYS_VI = {
    0: "Thứ Hai",
    1: "Thứ Ba",
    2: "Thứ Tư",
    3: "Thứ Năm",
    4: "Thứ Sáu",
    5: "Thứ Bảy",
    6: "Chủ Nhật",
}


def get_realtime_clock_context(now: datetime.datetime | None = None) -> str:
    """Returns a rich, formatted Vietnamese real-time clock context string."""
    if now is None:
        now = datetime.datetime.now()

    weekday_name = _WEEKDAYS_VI.get(now.weekday(), f"Thứ {now.weekday() + 2}")
    time_str = now.strftime("%H:%M:%S")
    date_str = now.strftime("%d/%m/%Y")
    
    # Calculate local UTC offset (e.g. GMT+7)
    utc_offset = now.astimezone().strftime("%z")
    tz_label = f"GMT{utc_offset[:3]}:{utc_offset[3:]}" if utc_offset else "Giờ địa phương"

    return f"Thời gian thực tế trên máy tính của anh: {time_str}, {weekday_name}, ngày {date_str} (Múi giờ: {tz_label})"


class _SYSTEM_POWER_STATUS(ctypes.Structure):
    _fields_ = [
        ("ACLineStatus", ctypes.c_byte),
        ("BatteryFlag", ctypes.c_byte),
        ("BatteryLifePercent", ctypes.c_byte),
        ("SystemStatusFlag", ctypes.c_byte),
        ("BatteryLifeTime", ctypes.c_ulong),
        ("BatteryFullLifeTime", ctypes.c_ulong),
    ]


class _MEMORYSTATUSEX(ctypes.Structure):
    _fields_ = [
        ("dwLength", ctypes.c_ulong),
        ("dwMemoryLoad", ctypes.c_ulong),
        ("ullTotalPhys", ctypes.c_ulonglong),
        ("ullAvailPhys", ctypes.c_ulonglong),
        ("ullTotalPageFile", ctypes.c_ulonglong),
        ("ullAvailPageFile", ctypes.c_ulonglong),
        ("ullTotalVirtual", ctypes.c_ulonglong),
        ("ullAvailVirtual", ctypes.c_ulonglong),
        ("ullAvailExtendedVirtual", ctypes.c_ulonglong),
    ]


def get_pc_hardware_status() -> dict[str, str | int | float | bool]:
    """Returns live hardware telemetry: battery, RAM, OS info."""
    status: dict[str, str | int | float | bool] = {
        "os": f"{platform.system()} {platform.release()}",
    }

    if platform.system() == "Windows":
        # 1. Battery / Power Status
        try:
            power = _SYSTEM_POWER_STATUS()
            if ctypes.windll.kernel32.GetSystemPowerStatus(ctypes.byref(power)):
                if power.BatteryLifePercent != 255:
                    status["battery_percent"] = int(power.BatteryLifePercent)
                    status["is_charging"] = power.ACLineStatus == 1
                else:
                    status["battery_percent"] = "Không có pin (Máy bàn / PC Desktop)"
                    status["is_charging"] = True
        except Exception as exc:
            logger.debug("Failed to query power status: %s", exc)

        # 2. Memory / RAM Status
        try:
            mem = _MEMORYSTATUSEX()
            mem.dwLength = ctypes.sizeof(_MEMORYSTATUSEX)
            if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(mem)):
                total_gb = round(mem.ullTotalPhys / (1024**3), 1)
                avail_gb = round(mem.ullAvailPhys / (1024**3), 1)
                used_gb = round((mem.ullTotalPhys - mem.ullAvailPhys) / (1024**3), 1)
                status["ram_used_percent"] = int(mem.dwMemoryLoad)
                status["ram_total_gb"] = total_gb
                status["ram_used_gb"] = used_gb
                status["ram_avail_gb"] = avail_gb
        except Exception as exc:
            logger.debug("Failed to query memory status: %s", exc)

    return status


def describe_pc_hardware_for_prompt() -> str:
    """Formats hardware telemetry into a concise context line for the LLM."""
    telemetry = get_pc_hardware_status()
    parts = [f"Hệ điều hành: {telemetry.get('os', 'Windows')}"]

    if "battery_percent" in telemetry:
        bat = telemetry["battery_percent"]
        if isinstance(bat, int):
            charging_str = "đang cắm sạc" if telemetry.get("is_charging") else "đang dùng pin"
            parts.append(f"Pin laptop: {bat}% ({charging_str})")
        else:
            parts.append(f"Nguồn điện: {bat}")

    if "ram_used_percent" in telemetry:
        parts.append(
            f"RAM: đã dùng {telemetry['ram_used_gb']}/{telemetry['ram_total_gb']} GB ({telemetry['ram_used_percent']}%)"
        )

    return "Trạng thái máy tính của anh: " + " | ".join(parts)


_KNOWN_APP_MAP: dict[str, str] = {
    "notepad": "notepad.exe",
    "calc": "calc.exe",
    "calculator": "calc.exe",
    "máy tính": "calc.exe",
    "góp ý": "notepad.exe",
    "soạn thảo": "notepad.exe",
    "ghi chú": "notepad.exe",
    "paint": "mspaint.exe",
    "mspaint": "mspaint.exe",
    "vẽ": "mspaint.exe",
    "explorer": "explorer.exe",
    "file explorer": "explorer.exe",
    "quản lý file": "explorer.exe",
    "cmd": "cmd.exe",
    "terminal": "wt.exe",
    "powershell": "powershell.exe",
    "task manager": "taskmgr.exe",
    "taskmgr": "taskmgr.exe",
    "chrome": "chrome.exe",
    "edge": "msedge.exe",
    "msedge": "msedge.exe",
    "firefox": "firefox.exe",
    "vscode": "code.cmd",
    "code": "code.cmd",
    "spotify": "spotify.exe",
    "discord": "discord.exe",
}


def open_application_or_url(target: str) -> tuple[bool, str]:
    """Safely opens an allowed Windows application or a web URL."""
    target_clean = target.strip().lower()

    if target.startswith("http://") or target.startswith("https://") or target.startswith("www."):
        url = target if (target.startswith("http://") or target.startswith("https://")) else f"https://{target}"
        try:
            webbrowser.open(url)
            return True, f"Đã mở liên kết: {url}"
        except Exception as exc:
            return False, f"Không thể mở liên kết web: {exc}"

    matched_exe = _KNOWN_APP_MAP.get(target_clean)
    if not matched_exe:
        for alias, exe in _KNOWN_APP_MAP.items():
            if alias in target_clean or target_clean in alias:
                matched_exe = exe
                break

    if not matched_exe:
        return False, f"Không tìm thấy ứng dụng hoặc không được phép mở: '{target}'"

    try:
        if platform.system() == "Windows":
            os.startfile(matched_exe)
            return True, f"Đã mở ứng dụng '{matched_exe}' thành công."
        else:
            subprocess.Popen([matched_exe])
            return True, f"Đã mở ứng dụng '{matched_exe}'."
    except Exception as exc:
        logger.warning("Failed to open application '%s': %s", matched_exe, exc)
        return False, f"Lỗi khi mở '{matched_exe}': {exc}"


def adjust_system_volume(action: str, steps: int = 2) -> tuple[bool, str]:
    """Adjusts Windows system volume: 'up', 'down', 'mute', 'unmute'."""
    action_clean = action.strip().lower()
    if platform.system() != "Windows":
        return False, "Tính năng chỉnh âm lượng chỉ khả dụng trên Windows."

    VK_VOLUME_MUTE = 0xAD
    VK_VOLUME_DOWN = 0xAE
    VK_VOLUME_UP = 0xAF

    try:
        user32 = ctypes.windll.user32
        steps = max(1, min(steps, 25))

        if action_clean in ["up", "tăng", "tang", "tăng âm lượng", "louder"]:
            for _ in range(steps):
                user32.keybd_event(VK_VOLUME_UP, 0, 0, 0)
                user32.keybd_event(VK_VOLUME_UP, 0, 2, 0)
            return True, f"Đã tăng âm lượng máy tính lên {steps * 2}%."

        elif action_clean in ["down", "giảm", "giam", "giảm âm lượng", "quieter"]:
            for _ in range(steps):
                user32.keybd_event(VK_VOLUME_DOWN, 0, 0, 0)
                user32.keybd_event(VK_VOLUME_DOWN, 0, 2, 0)
            return True, f"Đã giảm âm lượng máy tính xuống {steps * 2}%."

        elif action_clean in ["mute", "tắt tiếng", "tat tieng", "im lặng"]:
            user32.keybd_event(VK_VOLUME_MUTE, 0, 0, 0)
            user32.keybd_event(VK_VOLUME_MUTE, 0, 2, 0)
            return True, "Đã tắt/bật tiếng âm lượng máy tính."

        else:
            return False, f"Hành động âm lượng không hợp lệ: '{action}'"
    except Exception as exc:
        logger.warning("Failed to adjust volume: %s", exc)
        return False, f"Lỗi điều chỉnh âm lượng: {exc}"
