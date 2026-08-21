from __future__ import annotations

import json
from pathlib import Path

from ru_runtime.vts.hotkey_mapping import (
    DEFAULT_HOTKEY_SPECS,
    load_custom_hotkeys,
    resolve_hotkey,
)


def test_default_specs_cover_key_emotions() -> None:
    expected = {"joy", "angry", "sad", "shy", "surprised", "gentle", "raise_hand", "tail_up"}
    assert expected.issubset(DEFAULT_HOTKEY_SPECS.keys())


def test_resolve_hotkey_exact_match() -> None:
    available = [
        {"name": "星星眼", "hotkeyID": "hk_joy"},
        {"name": "眼睛恢复", "hotkeyID": "hk_reset"},
    ]
    primary, reset, delay = resolve_hotkey("joy", available)
    assert primary == "星星眼"
    assert reset == "眼睛恢复"
    assert delay == 2.0


def test_resolve_hotkey_substring_english() -> None:
    available = [
        {"name": "01. Star Eyes Expression", "hotkeyID": "hk_star"},
        {"name": "Reset Eyes Default", "hotkeyID": "hk_reset"},
        {"name": "Angry_Face_02", "hotkeyID": "hk_angry"},
    ]
    primary, reset, delay = resolve_hotkey("joy", available)
    assert primary == "01. Star Eyes Expression"
    assert reset == "Reset Eyes Default"

    primary_angry, reset_angry, delay_angry = resolve_hotkey("angry", available)
    assert primary_angry == "Angry_Face_02"
    assert delay_angry == 3.0


def test_resolve_hotkey_vietnamese_aliases() -> None:
    available = [
        {"name": "Biểu cảm Đỏ Mặt", "hotkeyID": "hk_blush"},
        {"name": "Khóc Nước Mắt", "hotkeyID": "hk_cry"},
        {"name": "Vẫy Tay Chào", "hotkeyID": "hk_wave"},
        {"name": "Hạ Tay", "hotkeyID": "hk_lower"},
    ]
    primary_shy, _, _ = resolve_hotkey("shy", available)
    assert primary_shy == "Biểu cảm Đỏ Mặt"

    primary_sad, _, _ = resolve_hotkey("sad", available)
    assert primary_sad == "Khóc Nước Mắt"

    primary_wave, reset_wave, delay_wave = resolve_hotkey("raise_hand", available)
    assert primary_wave == "Vẫy Tay Chào"
    assert reset_wave == "Hạ Tay"
    assert delay_wave == 2.5


def test_resolve_hotkey_custom_override(tmp_path: Path) -> None:
    config_file = tmp_path / "vts_hotkeys.json"
    config_file.write_text(
        json.dumps({
            "custom_hotkeys": {
                "angry": {
                    "primary": "Custom_Super_Rage",
                    "reset": "Custom_Calm",
                    "delay": 4.5
                }
            }
        }),
        encoding="utf-8"
    )

    custom_cfg = load_custom_hotkeys(config_file)
    available = [
        {"name": "Custom_Super_Rage", "hotkeyID": "hk1"},
        {"name": "Custom_Calm", "hotkeyID": "hk2"},
    ]

    primary, reset, delay = resolve_hotkey("angry", available, custom_config=custom_cfg)
    assert primary == "Custom_Super_Rage"
    assert reset == "Custom_Calm"
    assert delay == 4.5


def test_resolve_hotkey_returns_no_mapping_when_catalog_has_no_match() -> None:
    primary, reset, delay = resolve_hotkey(
        "joy",
        [{"name": "Unrelated hotkey", "hotkeyID": "other"}],
    )

    assert primary is None
    assert reset is None
    assert delay == 2.0


def test_custom_hotkey_must_exist_in_catalog() -> None:
    primary, reset, _ = resolve_hotkey(
        "angry",
        [{"name": "Other hotkey", "hotkeyID": "other"}],
        custom_config={
            "angry": {
                "primary": "Missing primary",
                "reset": "Missing reset",
            }
        },
    )

    assert primary is None
    assert reset is None
