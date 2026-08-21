from __future__ import annotations

import json
import logging
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from ru_runtime.persistence.canonical import canonical_data_root

logger = logging.getLogger(__name__)

HOTKEYS_CONFIG_FILENAME = "vts_hotkeys.json"


@dataclass(frozen=True)
class HotkeyActionSpec:
    aliases: tuple[str, ...]
    reset_aliases: tuple[str, ...] = ()
    reset_delay: float = 2.0


DEFAULT_HOTKEY_SPECS: dict[str, HotkeyActionSpec] = {
    "joy": HotkeyActionSpec(
        aliases=("星星眼", "star eyes", "heart eyes", "mắt sao", "vui", "cười", "smile", "happy", "joy", "gleam", "sparkle"),
        reset_aliases=("眼睛恢复", "reset eyes", "default eyes", "normal"),
        reset_delay=2.0,
    ),
    "angry": HotkeyActionSpec(
        aliases=("tức giận", "giận", "angry", "rage", "quạo", "生气", "黑脸", "dark", "pout", "dỗi", "mad"),
        reset_aliases=("hết giận", "normal", "reset", "default"),
        reset_delay=3.0,
    ),
    "sad": HotkeyActionSpec(
        aliases=("cry", "khóc", "sad", "nước mắt", "buồn", "流泪", "tear", "tears", "sob"),
        reset_aliases=("hết khóc", "reset", "normal", "default"),
        reset_delay=3.0,
    ),
    "shy": HotkeyActionSpec(
        aliases=("blush", "ngại", "đỏ mặt", "ngượng", "shy", "脸红", "flush", "embarrassed"),
        reset_aliases=("hết ngại", "reset", "normal", "default"),
        reset_delay=3.0,
    ),
    "surprised": HotkeyActionSpec(
        aliases=("惊讶", "surprised", "surprise", "shock", "ngạc nhiên", "giật mình", "gasp"),
        reset_aliases=("normal", "reset", "default"),
        reset_delay=2.0,
    ),
    "gentle": HotkeyActionSpec(
        aliases=("闭眼", "close eyes", "gentle", "nhắm mắt", "sleepy", "blink"),
        reset_aliases=("眼睛恢复", "reset eyes", "normal", "default"),
        reset_delay=2.0,
    ),
    "raise_hand": HotkeyActionSpec(
        aliases=("手抬起", "raise hand", "wave", "giơ tay", "vẫy tay", "hands up", "greet"),
        reset_aliases=("手放下", "lower hand", "hands down", "hạ tay"),
        reset_delay=2.5,
    ),
    "tail_up": HotkeyActionSpec(
        aliases=("尾巴上竖", "tail up", "vẫy đuôi", "dựng đuôi", "tail wag", "tail erect"),
        reset_aliases=("尾巴下竖", "tail down", "hạ đuôi", "tail normal"),
        reset_delay=2.5,
    ),
}


def get_custom_hotkeys_path() -> Path:
    return canonical_data_root() / HOTKEYS_CONFIG_FILENAME


def load_custom_hotkeys(path: Path | None = None) -> dict[str, dict[str, Any]]:
    target = path or get_custom_hotkeys_path()
    if not target.is_file():
        return {}
    try:
        data = json.loads(target.read_text(encoding="utf-8"))
        if isinstance(data, dict):
            custom = data.get("custom_hotkeys", data)
            if isinstance(custom, dict):
                return custom
    except Exception as error:
        logger.warning("Could not read custom hotkey configuration '%s': %s", target, error)
    return {}


def _find_best_hotkey_match(aliases: tuple[str, ...], available_hotkeys: list[dict[str, Any]]) -> str | None:
    if not available_hotkeys or not aliases:
        return None

    # Step 1: Exact case-insensitive match
    for alias in aliases:
        alias_clean = alias.strip().casefold()
        for hk in available_hotkeys:
            name = str(hk.get("name") or "").strip()
            if name.casefold() == alias_clean:
                return name

    # Step 2: Substring / contains match
    for alias in aliases:
        alias_clean = alias.strip().casefold()
        if not alias_clean:
            continue
        for hk in available_hotkeys:
            name = str(hk.get("name") or "").strip()
            if alias_clean in name.casefold():
                return name

    return None


def resolve_hotkey(
    action: str,
    available_hotkeys: list[dict[str, Any]],
    custom_config: dict[str, dict[str, Any]] | None = None,
) -> tuple[str | None, str | None, float]:
    """Resolve an action to (primary_hotkey_name, reset_hotkey_name, reset_delay).
    
    Order of precedence:
    1. Custom mapping in vts_hotkeys.json (by exact name or fuzzy search).
    2. Default multi-language aliases (exact match then substring match).
    """
    config = custom_config if custom_config is not None else load_custom_hotkeys()
    
    # 1. Check custom overrides
    if action in config:
        entry = config[action]
        if isinstance(entry, dict):
            primary = entry.get("primary")
            reset = entry.get("reset")
            try:
                delay = float(entry.get("delay", 2.0))
            except (ValueError, TypeError):
                delay = 2.0
            if isinstance(primary, str) and primary:
                primary_match = _find_best_hotkey_match((primary,), available_hotkeys)
                if primary_match is None:
                    return None, None, delay
                reset_match = None
                if isinstance(reset, str) and reset:
                    reset_match = _find_best_hotkey_match((reset,), available_hotkeys)
                return primary_match, reset_match, delay

    # 2. Check default specs
    spec = DEFAULT_HOTKEY_SPECS.get(action)
    if spec is None:
        return None, None, 2.0

    primary_match = _find_best_hotkey_match(spec.aliases, available_hotkeys)
    reset_match = _find_best_hotkey_match(spec.reset_aliases, available_hotkeys) if spec.reset_aliases else None

    return primary_match, reset_match, spec.reset_delay
