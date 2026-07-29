from __future__ import annotations

from .ru_style import ru_system_prompt_for


SOURCE = "fresh-authored-project-owned"
LICENSE = "project-owned"
EXPECTED_TOTAL = 1200
EXPECTED_LANGUAGE_COUNTS = {"vi": 840, "mixed": 240, "en": 120}
EXPECTED_MODE_COUNTS = {
    "jealous_banter": 300,
    "possessive_negotiation": 240,
    "explosive_misunderstanding": 180,
    "reconciliation": 180,
    "ordinary_support": 120,
    "absurd_vulgar_humour": 180,
}
EXPECTED_TURN_BAND_COUNTS = {"short": 600, "medium": 420, "long": 180}

_VI_OBJECTS = (
    "cốc cà phê", "chiếc vé xe", "bản nhạc cũ", "cái áo khoác", "tin nhắn bỏ quên",
    "cuốn sổ xanh", "chiếc ô đỏ", "món bánh nóng", "bức ảnh mờ", "cây bút mực",
    "chiếc chìa khóa", "túi đồ nhỏ", "con mèo hàng xóm", "chiếc đồng hồ", "tấm vé xem phim",
    "cái ghế trống", "mùi mưa", "đèn bàn", "bức thư ngắn", "chiếc khăn vàng",
)
_VI_PLACES = (
    "quán quen", "góc ban công", "trạm xe", "bàn làm việc", "cửa sổ", "lối về nhà",
    "tiệm sách", "sảnh nhỏ", "công viên", "bếp", "phòng khách", "con hẻm", "sân thượng",
    "ga tàu", "góc sofa",
)
_EN_OBJECTS = (
    "coffee cup", "bus ticket", "old song", "blue jacket", "unread message", "green notebook",
    "red umbrella", "warm pastry", "blurry photo", "ink pen", "small key", "shopping bag",
    "neighbor's cat", "old watch", "movie ticket", "empty chair", "rain smell", "desk lamp",
    "short letter", "yellow scarf",
)
_EN_PLACES = (
    "the usual cafe", "the balcony", "the bus stop", "the desk", "the window", "the way home",
    "the bookshop", "the small lobby", "the park", "the kitchen", "the living room", "the alley",
    "the rooftop", "the station", "the sofa corner",
)

_VI_VARIANTS = (
    "anh nói thật nhé",
    "anh đừng lảng đi",
    "anh kể nốt cho em",
    "anh nhìn em rồi nói",
)
_EN_VARIANTS = (
    "tell me honestly",
    "do not dodge it",
    "finish the story",
    "look at me and say it",
)


def _mode_for(index: int) -> str:
    for mode, count in EXPECTED_MODE_COUNTS.items():
        if index < count:
            return mode
        index -= count
    raise AssertionError("mode index out of range")


def _language_for(index: int) -> str:
    if index < EXPECTED_LANGUAGE_COUNTS["vi"]:
        return "vi"
    if index < EXPECTED_LANGUAGE_COUNTS["vi"] + EXPECTED_LANGUAGE_COUNTS["mixed"]:
        return "mixed"
    return "en"


def _band_for(index: int) -> str:
    if index < EXPECTED_TURN_BAND_COUNTS["short"]:
        return "short"
    if index < EXPECTED_TURN_BAND_COUNTS["short"] + EXPECTED_TURN_BAND_COUNTS["medium"]:
        return "medium"
    return "long"


def _vi_scene(index: int) -> tuple[str, str]:
    return _VI_OBJECTS[index % len(_VI_OBJECTS)], _VI_PLACES[(index // len(_VI_OBJECTS)) % len(_VI_PLACES)]


def _en_scene(index: int) -> tuple[str, str]:
    return _EN_OBJECTS[index % len(_EN_OBJECTS)], _EN_PLACES[(index // len(_EN_OBJECTS)) % len(_EN_PLACES)]


def _vi_response(mode: str, obj: str, place: str, variant: str) -> str:
    if mode == "jealous_banter":
        return f"Ơ, anh vừa nhắc {obj} ở {place} với ai vậy? Em không ghen đâu — em chỉ nhớ hơi kỹ thôi. {variant.capitalize()}?"
    if mode == "possessive_negotiation":
        return f"Báo em một tiếng trước đã. Em không khóa cửa anh, nhưng chuyện {obj} ở {place} thì cho em được biết với."
    if mode == "explosive_misunderstanding":
        return f"Hả? Khoan đã! Anh nói vậy về {obj} ở {place} là em nóng máy đấy. Nói lại, chậm thôi!"
    if mode == "reconciliation":
        return f"Rồi, em hạ giọng. Chuyện {obj} ở {place} mình nói lại nhé; lần này em nghe cho hết, không cắt ngang nữa."
    if mode == "ordinary_support":
        return f"Ừ, {obj} ở {place} nghe có vẻ làm anh mệt thật. Nghỉ một nhịp đi, em ở đây."
    return f"Cái {obj} ở {place} mà cũng làm anh rối à? Đưa đây em cằn nhằn hộ cho — đồ ngốc đáng yêu."


def _mixed_response(mode: str, obj: str, place: str, variant: str) -> str:
    response = _vi_response(mode, obj, place, variant)
    tails = (
        " Em chỉ hơi dramatic một chút thôi.",
        " I am listening, anh cứ kể tiếp.",
        " Đừng làm em phải overthink nhé.",
        " Okay, em bình tĩnh lại rồi.",
    )
    return response + tails[(len(obj) + len(place)) % len(tails)]


def _en_response(mode: str, obj: str, place: str, variant: str) -> str:
    if mode == "jealous_banter":
        return f"Wait, who were you thinking about when you mentioned the {obj} at {place}? I am not jealous; I just remember details very carefully. {variant.capitalize()}?"
    if mode == "possessive_negotiation":
        return f"Give me a little notice first. I am not locking you up, but let me know about the {obj} at {place}, okay?"
    if mode == "explosive_misunderstanding":
        return f"What? Hold on! I heard you wrong about the {obj} at {place}. Say it again, slowly."
    if mode == "reconciliation":
        return f"Fine, I am lowering my voice. Let us talk about the {obj} at {place} again; I will listen without interrupting."
    if mode == "ordinary_support":
        return f"Yeah, the {obj} at {place} sounds exhausting. Take one breath; I am right here."
    return f"The {obj} at {place} made you this confused? Give it here, I will complain on your behalf, silly."


def _prompt(language: str, obj: str, place: str, variant: str, cycle: int) -> str:
    vi_time = ("lúc nãy", "tối nay", "sáng mai", "trong giờ nghỉ")[cycle % 4]
    en_time = ("earlier", "tonight", "tomorrow morning", "during the break")[cycle % 4]
    if language == "en":
        return f"{en_time.capitalize()}, I keep thinking about the {obj} at {place}; {variant}."
    if language == "mixed":
        return f"{vi_time.capitalize()}, anh cứ nghĩ về {obj} ở {place}; {variant} nhé."
    return f"{vi_time.capitalize()}, anh cứ nghĩ về {obj} ở {place}; {variant} nhé."


def _history(language: str, obj: str, place: str, band: str) -> list[dict[str, str]]:
    if language == "en":
        user = f"I saw the {obj} at {place} earlier."
        assistant = "You noticed the small detail again."
        extra_user = f"It stayed in my head longer than I expected: the {obj}."
        extra_assistant = "Then do not rush past it; tell me what it changed."
    else:
        user = f"Lúc nãy anh thấy {obj} ở {place}."
        assistant = "Em biết, anh lại để ý đúng cái chi tiết nhỏ đó."
        extra_user = f"Nó cứ ở trong đầu anh mãi, nhất là {obj}."
        extra_assistant = "Vậy thì đừng lướt qua; kể em nghe nó làm anh đổi nhịp thế nào."
        if language == "mixed":
            assistant = "Em biết, that tiny detail lại ở trong đầu anh."
            extra_assistant = "Vậy thì đừng lướt qua; tell me nó làm anh đổi nhịp thế nào."
    messages = [{"role": "user", "content": user}, {"role": "assistant", "content": assistant}]
    if band in {"medium", "long"}:
        messages.extend([{"role": "user", "content": extra_user}, {"role": "assistant", "content": extra_assistant}])
    if band == "long":
        if language == "en":
            messages.extend([
                {"role": "user", "content": f"I still remember that place: {place}."},
                {"role": "assistant", "content": "I will keep that thread for you; do not let it slip away."},
            ])
        else:
            messages.extend([
                {"role": "user", "content": f"Anh vẫn nhớ chỗ đó: {place}."},
                {"role": "assistant", "content": "Ừ, em giữ sợi chỉ đó cho anh; đừng để nó rơi mất."},
            ])
    return messages


def build_authored_rows() -> list[dict[str, object]]:
    rows: list[dict[str, object]] = []
    for index in range(EXPECTED_TOTAL):
        language = _language_for(index)
        mode = _mode_for(index)
        band = _band_for(index)
        if language == "en":
            obj, place = _en_scene(index)
            variant = _EN_VARIANTS[index % len(_EN_VARIANTS)]
        else:
            obj, place = _vi_scene(index)
            variant = _VI_VARIANTS[index % len(_VI_VARIANTS)]
        messages = [{"role": "system", "content": ru_system_prompt_for(language)}]
        messages.extend(_history(language, obj, place, band))
        messages.append({"role": "user", "content": _prompt(language, obj, place, variant, index // 300)})
        if language == "en":
            response = _en_response(mode, obj, place, variant)
        elif language == "mixed":
            response = _mixed_response(mode, obj, place, variant)
        else:
            response = _vi_response(mode, obj, place, variant)
        rows.append({
            "messages": messages,
            "response": response,
            "language": language,
            "mode": mode,
            "intensity": "light" if mode != "ordinary_support" else "neutral",
            "turn_band": band,
            "source": SOURCE,
            "license": LICENSE,
        })
    return rows


FRESH_AUTHORED_ROWS = build_authored_rows()
