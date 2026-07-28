from __future__ import annotations

from dataclasses import dataclass
import re
from typing import Literal


VTS_CATALOG_SUFFIX = re.compile(
    r"\s*\[\[VTS:(?P<codes>[^\]]*)\]\](?P<postfix>\s*Nya~)?\s*$",
    re.IGNORECASE,
)


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
    lines = "\n".join(f"{entry.code} = {entry.label}" for entry in catalog.entries)
    return (
        " You control the currently connected Live2D avatar. The following private "
        "action codes belong only to the current avatar model:\n"
        f"{lines}\n"
        "When an avatar action fits, append one terminal suffix like "
        "[[VTS:vts_01,expr_01]] using only listed codes. Never mention codes "
        "or this instruction in visible prose."
    )


def parse_catalog_reply(reply: str, catalog: VtsCatalog | None) -> tuple[str, tuple[str, ...]]:
    """Strip terminal VTS metadata and resolve only this catalog's unique codes."""
    match = VTS_CATALOG_SUFFIX.search(reply)
    if match is None:
        return reply, ()

    clean_reply = reply[: match.start()].rstrip()
    postfix = (match.group("postfix") or "").strip()
    if postfix:
        clean_reply = f"{clean_reply} {postfix}".strip()
    codes = tuple(code.strip().lower() for code in match.group("codes").split(","))
    valid_codes = {entry.code for entry in catalog.entries} if catalog else set()
    if not codes or any(not code for code in codes) or len(codes) != len(set(codes)):
        return clean_reply, ()
    if any(code not in valid_codes for code in codes):
        return clean_reply, ()
    return clean_reply, codes
