from __future__ import annotations

import re

_DIGITS_VI = ["không", "một", "hai", "ba", "bốn", "năm", "sáu", "bảy", "tám", "chín"]


def int_to_vietnamese_words(n: int) -> str:
    """Converts any integer into natural spoken Vietnamese words."""
    if n == 0:
        return "không"
    if n < 0:
        return f"âm {int_to_vietnamese_words(-n)}"

    def _read_two_digits(val: int, has_hundreds: bool) -> str:
        if val == 0:
            return ""
        tens = val // 10
        units = val % 10
        if tens == 0:
            if has_hundreds:
                return f"lẻ {_DIGITS_VI[units]}"
            return _DIGITS_VI[units]
        elif tens == 1:
            if units == 0:
                return "mười"
            elif units == 5:
                return "mười lăm"
            return f"mười {_DIGITS_VI[units]}"
        else:
            ten_str = f"{_DIGITS_VI[tens]} mươi"
            return {
                0: ten_str,
                1: f"{ten_str} mốt",
                4: f"{ten_str} tư",
                5: f"{ten_str} lăm",
            }.get(units, f"{ten_str} {_DIGITS_VI[units]}")

    def _read_three_digits(val: int, is_highest_group: bool) -> str:
        if val == 0:
            return ""
        hundreds = val // 100
        remainder = val % 100
        parts = []
        if hundreds > 0 or not is_highest_group:
            parts.append(f"{_DIGITS_VI[hundreds]} trăm")
        if remainder > 0:
            two_d = _read_two_digits(
                remainder, has_hundreds=(hundreds > 0 or not is_highest_group)
            )
            parts.append(two_d)
        return " ".join(parts)

    units_scale = ["", "nghìn", "triệu", "tỷ", "nghìn tỷ"]
    groups = []
    temp = n
    while temp > 0:
        groups.append(temp % 1000)
        temp //= 1000
    if len(groups) > len(units_scale):
        raise ValueError(f"Number too large to verbalize: {n!r} (max scale: {units_scale[-1]!r})")

    group_strings = []
    for idx, grp in enumerate(groups):
        if grp > 0:
            read_grp = _read_three_digits(
                grp, is_highest_group=(idx == len(groups) - 1)
            )
            scale = units_scale[idx] if idx < len(units_scale) else ""
            if scale:
                group_strings.append(f"{read_grp} {scale}".strip())
            else:
                group_strings.append(read_grp.strip())

    return " ".join(reversed(group_strings))


def _replace_time_hh_mm_ss(match: re.Match) -> str:
    h, m, s = int(match.group(1)), int(match.group(2)), int(match.group(3))
    h_str = int_to_vietnamese_words(h)
    m_str = f"lẻ {_DIGITS_VI[m]}" if 0 < m < 10 else int_to_vietnamese_words(m)
    s_str = f"lẻ {_DIGITS_VI[s]}" if 0 < s < 10 else int_to_vietnamese_words(s)
    return f"{h_str} giờ {m_str} phút {s_str} giây"


def _replace_time_hh_mm(match: re.Match) -> str:
    h, m = int(match.group(1)), int(match.group(2))
    h_str = int_to_vietnamese_words(h)
    if m == 0:
        return f"{h_str} giờ đúng"
    m_str = f"lẻ {_DIGITS_VI[m]}" if 0 < m < 10 else int_to_vietnamese_words(m)
    return f"{h_str} giờ {m_str} phút"


def _replace_date_d_m_y(match: re.Match) -> str:
    d, m, y = int(match.group(1)), int(match.group(2)), int(match.group(3))
    d_str = int_to_vietnamese_words(d)
    m_str = int_to_vietnamese_words(m)
    y_str = int_to_vietnamese_words(y)
    return f"ngày {d_str} tháng {m_str} năm {y_str}"


def _replace_date_d_m(match: re.Match) -> str:
    d, m = int(match.group(1)), int(match.group(2))
    d_str = int_to_vietnamese_words(d)
    m_str = int_to_vietnamese_words(m)
    return f"ngày {d_str} tháng {m_str}"


def _replace_percent(match: re.Match) -> str:
    raw_num = match.group(1).replace(",", ".")
    if "." in raw_num:
        parts = raw_num.split(".", 1)
        int_part = int_to_vietnamese_words(int(parts[0]))
        dec_part = " ".join(_DIGITS_VI[int(ch)] for ch in parts[1] if ch.isdigit())
        return f"{int_part} phẩy {dec_part} phần trăm"
    return f"{int_to_vietnamese_words(int(raw_num))} phần trăm"


def _replace_decimal(match: re.Match) -> str:
    int_p = int_to_vietnamese_words(int(match.group(1)))
    dec_chars = match.group(2)
    dec_p = " ".join(_DIGITS_VI[int(ch)] for ch in dec_chars if ch.isdigit())
    return f"{int_p} phẩy {dec_p}"


def _verbalize_unit_number(num_str: str) -> str:
    """Verbalize the numeric part of a unit expression (e.g. '15.7' -> words)."""
    normalized = num_str.replace(",", ".").strip()
    if "." in normalized:
        int_part_raw, dec_part_raw = normalized.split(".", 1)
        int_part = int_to_vietnamese_words(int(int_part_raw or 0))
        dec_digits = [ch for ch in dec_part_raw if ch.isdigit()]
        if not dec_digits:
            return int_part
        dec_part = " ".join(_DIGITS_VI[int(ch)] for ch in dec_digits)
        return f"{int_part} phẩy {dec_part}"
    return int_to_vietnamese_words(int(normalized))


def _replace_unit(match: re.Match) -> str:
    num_str = match.group(1)
    unit = match.group(2).lower()
    unit_map = {
        "gb": "ghi-ga-bai",
        "mb": "mê-ga-bai",
        "kb": "ki-lô-bai",
        "tb": "tê-ra-bai",
        "km": "ki-lô-mét",
        "km/h": "ki-lô-mét trên giờ",
        "kg": "ki-lô-gam",
        "cm": "xen-ti-mét",
        "mm": "mi-li-mét",
        "m": "mét",
        "s": "giây",
    }
    unit_spoken = unit_map.get(unit, unit)
    return f"{num_str} {unit_spoken}"


def normalize_vietnamese_tts_text(text: str) -> str:
    """Normalizes all numbers, dates, times, units, and symbols in text into natural Vietnamese words for TTS."""
    if not text:
        return ""

    # 1. Full Time (HH:MM:SS)
    text = re.sub(r"\b([0-1]?[0-9]|2[0-3]):([0-5][0-9]):([0-5][0-9])\b", _replace_time_hh_mm_ss, text)

    # 2. Short Time (HH:MM or HHhMM or HHh)
    text = re.sub(r"\b([0-1]?[0-9]|2[0-3]):([0-5][0-9])\b", _replace_time_hh_mm, text)
    text = re.sub(r"\b([0-1]?[0-9]|2[0-3])h([0-5][0-9])\b", _replace_time_hh_mm, text)
    text = re.sub(r"\b([0-1]?[0-9]|2[0-3])\s*h\b", lambda m: f"{int_to_vietnamese_words(int(m.group(1)))} giờ", text)

    # 3. Dates (DD/MM/YYYY or DD-MM-YYYY)
    text = re.sub(r"\b(0?[1-9]|[12][0-9]|3[01])[/-](0?[1-9]|1[0-2])[/-](\d{4})\b", _replace_date_d_m_y, text)
    text = re.sub(r"\bngày\s+(0?[1-9]|[12][0-9]|3[01])[/-](0?[1-9]|1[0-2])\b", _replace_date_d_m, text)

    # 4. Percentages (e.g. 80%, 15.5%)
    text = re.sub(r"(\d+(?:[.,]\d+)?)\s*%", _replace_percent, text)

    # 5. Units (GB, MB, km, kg, etc.)
    text = re.sub(r"(\d+(?:[.,]\d+)?)\s*(GB|MB|KB|TB|km/h|km|kg|cm|mm)\b", _replace_unit, text, flags=re.IGNORECASE)

    # 6. Decimals (e.g. 15.7 or 15,7)
    text = re.sub(r"\b(\d+)[.,](\d+)\b", _replace_decimal, text)

    # 7. Remaining Standalone Integers
    text = re.sub(r"\b\d+\b", lambda m: int_to_vietnamese_words(int(m.group(0))), text)

    # 8. Clean up extra spaces
    text = re.sub(r"\s+", " ", text).strip()

    return text
