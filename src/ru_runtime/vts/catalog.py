from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Literal

from .emotion import EmotionTag, extract_reply_emotion


VTS_CATALOG_MARKER = re.compile(r"\[\[VTS:([^\]]*)\]\]", re.IGNORECASE)
CATALOG_INSTRUCTION_MAX_ENTRIES = 20
CODE_DELIMITERS = re.compile(r"[\s,]+")


@dataclass(frozen=True)
class VtsCatalogEntry:
    code: str
    kind: Literal["hotkey", "expression"]
    label: str
    identifier: str


@dataclass(frozen=True)
class VtsCatalog:
    model_id: str
    model_name: str
    entries: tuple[VtsCatalogEntry, ...]

    def entry_for(self, code: str) -> VtsCatalogEntry | None:
        return next((entry for entry in self.entries if entry.code == code), None)


def build_catalog(
    *,
    model_id: str,
    model_name: str,
    hotkeys: list[dict[str, object]],
    expressions: list[dict[str, object]],
) -> VtsCatalog:
    hotkey_items = sorted(
        (
            (str(item.get("name") or item["hotkeyID"]), str(item["hotkeyID"]))
            for item in hotkeys
            if isinstance(item.get("hotkeyID"), str) and item["hotkeyID"]
        ),
        key=lambda item: (item[0].casefold(), item[1]),
    )
    expression_items = sorted(
        (
            (
                str(item.get("name") or expression_file),
                expression_file,
            )
            for item in expressions
            for expression_file in (item.get("expressionFile") or item.get("file"),)
            if isinstance(expression_file, str) and expression_file.endswith(".exp3.json")
        ),
        key=lambda item: (item[0].casefold(), item[1]),
    )
    entries = tuple(
        [
            VtsCatalogEntry(f"vts_{index:02d}", "hotkey", label, identifier)
            for index, (label, identifier) in enumerate(hotkey_items, start=1)
        ]
        + [
            VtsCatalogEntry(f"expr_{index:02d}", "expression", label, identifier)
            for index, (label, identifier) in enumerate(expression_items, start=1)
        ]
    )
    return VtsCatalog(model_id=model_id, model_name=model_name, entries=entries)


def catalog_instruction(catalog: VtsCatalog | None) -> str:
    if catalog is None or not catalog.entries:
        return ""
    lines = "\n".join(
        f"{entry.code} = {entry.label}"
        for entry in catalog.entries[:CATALOG_INSTRUCTION_MAX_ENTRIES]
    )
    return (
        "【VTS动作指令】你控制当前连接的Live2D虚拟形象。"
        "以下是当前模型独有的私有动作代码（请只使用这些代码）：\n"
        f"{lines}\n"
        "当需要表达情绪或动作时，请从上列表中选择1-3个最合适的代码，"
        "并在回复末尾追加终端后缀，例如 [[VTS:vts_01,expr_01]]。"
        "绝不要在可见文本中提及代码或此指令。\n\n"
        "You control the currently connected Live2D avatar. The following private "
        "action codes belong only to the current avatar model:\n"
        f"{lines}\n"
        "When visible emotion or animation fits, choose one or multiple compatible "
        "listed codes (up to three) and append one terminal suffix like "
        "[[VTS:vts_01,expr_01]] using only listed codes. Combine facial, eye, "
        "ear, or tail actions when they reinforce the same emotion. Never mention "
        "codes or this instruction in visible prose."
    )


def _strip_markers(reply: str) -> str:
    return re.sub(r"\s+", " ", VTS_CATALOG_MARKER.sub("", reply)).strip()


def parse_catalog_reply(reply: str, catalog: VtsCatalog | None) -> tuple[str, tuple[str, ...]]:
    """Strip VTS metadata markers anywhere in the reply and resolve this
    catalog's unique codes."""
    matches = list(VTS_CATALOG_MARKER.finditer(reply))
    if not matches:
        return reply, ()

    codes = tuple(
        code.strip().lower()
        for match in matches
        for code in CODE_DELIMITERS.split(match.group(1))
        if code.strip()
    )
    valid_codes = {entry.code for entry in catalog.entries} if catalog else set()
    if not codes or any(not code for code in codes) or len(codes) != len(set(codes)):
        return _strip_markers(reply), ()
    if any(code not in valid_codes for code in codes):
        return _strip_markers(reply), ()
    return _strip_markers(reply), codes


def split_reply_metadata(
    raw_reply: str, catalog: VtsCatalog | None
) -> tuple[str, tuple[str, ...], EmotionTag]:
    """Strip emotion marker first, then VTS codes; return both plus emotion."""
    reply, emotion = extract_reply_emotion(raw_reply)
    clean, codes = parse_catalog_reply(reply, catalog)
    return clean, codes, emotion
