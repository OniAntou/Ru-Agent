from __future__ import annotations

import ctypes
import datetime
import logging
import platform

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
        ("ACLineStatus", ctypes.c_ubyte),
        ("BatteryFlag", ctypes.c_ubyte),
        ("BatteryLifePercent", ctypes.c_ubyte),
        ("SystemStatusFlag", ctypes.c_ubyte),
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
