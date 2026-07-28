from __future__ import annotations

import asyncio
import threading

from mini_gpt.vts_catalog import build_catalog, catalog_instruction, parse_catalog_reply
from mini_gpt.vts_manager import VTSManager


def _catalog():
    return build_catalog(
        model_id="model-a",
        model_name="Model A",
        hotkeys=[
            {"hotkeyID": "hotkey-b", "name": "Wave"},
            {"hotkeyID": "hotkey-a", "name": "Blink"},
        ],
        expressions=[
            {"expressionFile": "smile.exp3.json", "name": "Smile"},
            {"file": "ignore.json", "name": "Ignore"},
            {"expressionFile": "blush.exp3.json", "name": "Blush"},
        ],
    )


def test_build_catalog_assigns_stable_codes_for_current_model_actions():
    catalog = _catalog()

    assert catalog.model_id == "model-a"
    assert [(entry.code, entry.kind, entry.identifier) for entry in catalog.entries] == [
        ("vts_01", "hotkey", "hotkey-a"),
        ("vts_02", "hotkey", "hotkey-b"),
        ("expr_01", "expression", "blush.exp3.json"),
        ("expr_02", "expression", "smile.exp3.json"),
    ]
    assert "vts_01 = Blink" in catalog_instruction(catalog)
    assert "expr_02 = Smile" in catalog_instruction(catalog)


def test_parse_catalog_reply_hides_suffix_and_resolves_only_unique_known_codes():
    catalog = _catalog()

    text, codes = parse_catalog_reply("Hello [[VTS:vts_01,expr_02]]", catalog)
    assert text == "Hello"
    assert codes == ("vts_01", "expr_02")

    invalid_text, invalid_codes = parse_catalog_reply("Hello [[VTS:vts_01,vts_01,other]]", catalog)
    assert invalid_text == "Hello"
    assert invalid_codes == ()


def test_parse_catalog_reply_hides_vts_before_terminal_nya():
    text, codes = parse_catalog_reply("Hello [[VTS:vts_01]] Nya~", _catalog())

    assert text == "Hello Nya~"
    assert codes == ("vts_01",)


def test_manager_dispatches_catalog_actions_only_while_the_same_model_is_active():
    catalog = _catalog()
    requests: list[dict[str, object]] = []
    active_model_id = "model-a"

    class FakeRequests:
        @staticmethod
        def requestTriggerHotKey(hotkey_id):
            return {"messageType": "HotkeyTriggerRequest", "hotkeyID": hotkey_id}

        @staticmethod
        def BaseRequest(message_type, data=None):
            return {"messageType": message_type, "data": data or {}}

    class FakeVts:
        vts_request = FakeRequests()

        async def request(self, request):
            requests.append(request)
            return {}

    manager = object.__new__(VTSManager)
    manager.vts = FakeVts()
    manager.is_connected = True
    manager._expression_lock = threading.Lock()
    manager._active_expressions = set()
    scheduled = []
    manager._schedule_expression_deactivation = scheduled.append

    async def current_catalog():
        return build_catalog(
            model_id=active_model_id,
            model_name="Current model",
            hotkeys=[{"hotkeyID": "hotkey-a", "name": "Blink"}],
            expressions=[{"expressionFile": "blush.exp3.json", "name": "Blush"}],
        )

    manager._fetch_catalog = current_catalog

    manager.trigger_catalog_actions_sync(catalog, ("vts_01", "expr_01"))

    assert requests == [
        {"messageType": "HotkeyTriggerRequest", "hotkeyID": "hotkey-a"},
        {
            "messageType": "ExpressionActivationRequest",
            "data": {"expressionFile": "blush.exp3.json", "active": True, "fadeTime": 0.25},
        },
    ]
    assert scheduled == [("model-a", "blush.exp3.json")]

    active_model_id = "model-b"
    manager.trigger_catalog_actions_sync(catalog, ("vts_01",))

    assert len(requests) == 2


def test_manager_deactivates_only_expressions_it_activated():
    catalog = _catalog()
    requests: list[dict[str, object]] = []

    class FakeRequests:
        @staticmethod
        def BaseRequest(message_type, data=None):
            return {"messageType": message_type, "data": data or {}}

    class FakeVts:
        vts_request = FakeRequests()

        async def request(self, request):
            requests.append(request)
            return {}

    manager = object.__new__(VTSManager)
    manager.vts = FakeVts()
    manager.is_connected = True
    manager._expression_lock = threading.Lock()
    manager._active_expressions = set()

    async def current_catalog():
        return catalog

    manager._fetch_catalog = current_catalog
    expression_key = ("model-a", "blush.exp3.json")

    asyncio.run(manager._deactivate_expression(expression_key))
    assert requests == []

    manager._active_expressions.add(expression_key)
    asyncio.run(manager._deactivate_expression(expression_key))

    assert requests == [
        {
            "messageType": "ExpressionActivationRequest",
            "data": {"expressionFile": "blush.exp3.json", "active": False},
        }
    ]
    assert manager._active_expressions == set()
