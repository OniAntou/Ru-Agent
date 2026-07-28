from __future__ import annotations

import threading

from mini_gpt import vts_manager
from mini_gpt.vts_actions import VtsActions, requested_vts_actions
from mini_gpt.vts_manager import ACTION_HOTKEYS, VTSManager


def test_requested_vts_actions_handles_direct_vietnamese_body_command():
    assert requested_vts_actions("Hay gio tay chao anh di") == VtsActions(body="raise_hand")


def test_requested_vts_actions_can_combine_safe_expression_and_body_command():
    assert requested_vts_actions("Em vui len va dung duoi nhe") == VtsActions(
        facial="joy",
        body="tail_up",
    )


def test_action_mapping_contains_only_the_safe_vtube_hotkeys():
    configured_hotkeys = {hotkey for values in ACTION_HOTKEYS.values() for hotkey in values[:2] if hotkey}

    assert configured_hotkeys == {
        "星星眼",
        "眼睛恢复",
        "惊讶",
        "闭眼",
        "手抬起",
        "手放下",
        "尾巴上竖",
        "尾巴下竖",
    }
    assert "嘴恢复" not in configured_hotkeys
    assert "LLM_MouthOpen" not in configured_hotkeys


def test_manager_dispatches_safe_actions_once_and_schedules_resets(monkeypatch):
    manager = object.__new__(VTSManager)
    manager.is_connected = True
    manager._action_lock = threading.Lock()
    manager._last_action_at = {}
    triggered = []
    scheduled = []
    manager.trigger_hotkey_sync = triggered.append

    class FakeTimer:
        def __init__(self, delay, callback, args):
            self.delay = delay
            self.callback = callback
            self.args = args
            self.daemon = False
            scheduled.append(self)

        def start(self):
            return None

    monkeypatch.setattr(vts_manager.threading, "Timer", FakeTimer)

    manager.trigger_actions_sync(VtsActions(facial="joy", body="raise_hand"))
    manager.trigger_actions_sync(VtsActions(facial="joy", body="raise_hand"))

    assert triggered == ["星星眼", "手抬起"]
    assert [(timer.delay, timer.args) for timer in scheduled] == [
        (2.0, ("眼睛恢复",)),
        (2.5, ("手放下",)),
    ]
    assert all(timer.daemon for timer in scheduled)
