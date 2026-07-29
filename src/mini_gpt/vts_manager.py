import asyncio
import logging
import random
import time
import threading

import pyvts

from .vts_actions import VtsActions
from .vts_catalog import VtsCatalog, build_catalog
from .vts_motion import MicroActionScheduler, safe_micro_action_entries, sway_value

logger = logging.getLogger(__name__)

ACTION_HOTKEYS = {
    "joy": ("星星眼", "眼睛恢复", 2.0),
    "surprised": ("惊讶", None, 2.0),
    "gentle": ("闭眼", "眼睛恢复", 2.0),
    "raise_hand": ("手抬起", "手放下", 2.5),
    "tail_up": ("尾巴上竖", "尾巴下竖", 2.5),
}
ACTION_COOLDOWN_SECONDS = 3.0
EXPRESSION_ACTIVE_SECONDS = 4.0


class VTSManager:
    def __init__(self, random_source=None):
        plugin_info = {
            "plugin_name": "Ru Local Llama",
            "developer": "Antigravity",
            "authentication_token_path": "./vts_token.txt",
        }
        self.vts = pyvts.vts(plugin_info=plugin_info)
        self.is_connected = False
        self.hotkeys = []
        self.is_speaking = False
        self._action_lock = threading.Lock()
        self._last_action_at: dict[str, float] = {}
        self._expression_lock = threading.Lock()
        self._active_expressions: set[tuple[str, str]] = set()
        self._semantic_action_lock = threading.Lock()
        self._motion_random = random_source or random
        self._micro_action_scheduler = MicroActionScheduler(self._motion_random)
        self._last_micro_action_identifier: str | None = None
        self._loop: asyncio.AbstractEventLoop | None = None
        self._request_lock: asyncio.Lock | None = None

    def run(self) -> None:
        """Own the VTube Studio websocket on one persistent background loop."""
        loop = asyncio.new_event_loop()
        self._loop = loop
        asyncio.set_event_loop(loop)
        self._request_lock = asyncio.Lock()
        try:
            loop.run_until_complete(self.connect())
            loop.run_forever()
        finally:
            self.is_connected = False
            loop.close()

    async def _request(self, request: dict) -> dict:
        request_lock = getattr(self, "_request_lock", None)
        if request_lock is None:
            return await self.vts.request(request)
        async with request_lock:
            return await self.vts.request(request)

    def _run_on_vts_loop(self, coroutine, *, wait: bool):
        loop = getattr(self, "_loop", None)
        if loop is not None and loop.is_running():
            future = asyncio.run_coroutine_threadsafe(coroutine, loop)
            if wait:
                return future.result(timeout=5)
            return None
        if wait:
            return asyncio.run(coroutine)
        asyncio.run(coroutine)
        return None

    def shutdown(self) -> None:
        self.is_connected = False
        self.set_speaking(False)
        loop = self._loop
        if loop is None or not loop.is_running():
            return

        async def close_connection() -> None:
            if self.vts.websocket is not None:
                await self.vts.close()

        try:
            asyncio.run_coroutine_threadsafe(close_connection(), loop).result(timeout=2)
        except Exception:
            pass
        loop.call_soon_threadsafe(loop.stop)

    async def connect(self):
        try:
            await self.vts.connect()
            try:
                await self.vts.read_token()
                authenticated = await self.vts.request_authenticate()
            except Exception:
                authenticated = False
            if not authenticated:
                await self.vts.request_authenticate_token(force=True)
                authenticated = await self.vts.request_authenticate()
            if not authenticated:
                raise RuntimeError("VTube Studio did not authenticate Ru")
            self.is_connected = True
            response = await self._request(self.vts.vts_request.requestHotKeyList())
            self.hotkeys = response["data"]["availableHotkeys"]
            logger.info("VTS Connected. Found %d hotkeys.", len(self.hotkeys))
            # Khởi tạo thông số và chạy sway loop ngầm
            await self.setup_custom_parameters()
            asyncio.create_task(self.sway_loop())
        except Exception as e:
            logger.warning("Failed to connect to VTube Studio: %s", e)
            self.is_connected = False

    async def setup_custom_parameters(self):
        try:
            req_create = self.vts.vts_request.requestCustomParameter(
                parameter="LLM_Sway",
                min=-30.0,
                max=30.0,
                default_value=0.0,
                info="Swaying for LLM"
            )
            await self._request(req_create)
            logger.info("Created VTS custom parameter: LLM_Sway")
        except Exception as e:
            logger.error("Failed to create custom parameter: %s", e)

    async def sway_loop(self):
        start_time = time.monotonic()
        while self.is_connected:
            try:
                elapsed = time.monotonic() - start_time
                if self.is_speaking:
                    # Lắc mạnh và nhanh (biên độ 30, chu kỳ ngắn)
                    val = sway_value(elapsed, speaking=True)
                else:
                    # Đung đưa nhẹ nhàng (biên độ 5, chu kỳ dài)
                    val = sway_value(elapsed, speaking=False)
                req_inject = self.vts.vts_request.requestSetParameterValue(
                    parameter="LLM_Sway",
                    value=val
                )
                await self._request(req_inject)
                if self._micro_action_scheduler.update(
                    time.monotonic(), self.is_speaking
                ):
                    await self._trigger_micro_action()
                await asyncio.sleep(0.05)
            except Exception as e:
                logger.debug("Sway loop error: %s", e)
                await asyncio.sleep(1)

    def set_speaking(self, speaking: bool) -> None:
        """Update the speech state consumed by the background sway loop."""

        was_speaking = self.is_speaking
        self.is_speaking = bool(speaking)
        if not self.is_speaking or (self.is_speaking and not was_speaking):
            self._micro_action_scheduler.reset()
            if not self.is_speaking:
                self._last_micro_action_identifier = None

    async def _trigger_micro_action(self) -> None:
        """Trigger one safe catalog action without racing semantic actions."""

        if not self.is_connected or not self.is_speaking:
            return
        if not self._semantic_action_lock.acquire(blocking=False):
            return
        try:
            catalog = await self._fetch_catalog()
            if catalog is None:
                return
            candidates = safe_micro_action_entries(
                entry for entry in catalog.entries if entry.kind == "hotkey"
            )
            if self._last_micro_action_identifier and len(candidates) > 1:
                alternatives = [
                    entry
                    for entry in candidates
                    if entry.identifier != self._last_micro_action_identifier
                ]
                if alternatives:
                    candidates = alternatives
            if not candidates:
                return
            entry = self._motion_random.choice(candidates)
            if not entry.identifier:
                return
            if sum(item.identifier == entry.identifier for item in candidates) != 1:
                logger.debug("Skipped ambiguous VTube micro-action: %s", entry.label)
                return
            await self._trigger_catalog_actions(catalog, (entry.code,))
            self._last_micro_action_identifier = entry.identifier
        finally:
            self._semantic_action_lock.release()

    async def trigger_hotkey(self, hotkey_name):
        if not self.is_connected:
            return

        for hk in self.hotkeys:
            if hk["name"].lower() == hotkey_name.lower():
                request_msg = self.vts.vts_request.requestTriggerHotKey(hk["hotkeyID"])
                await self._request(request_msg)
                logger.info("Triggered hotkey: %s", hk["name"])
                return

        logger.warning("Hotkey '%s' not found.", hotkey_name)

    def trigger_hotkey_sync(self, hotkey_name):
        if not self.is_connected:
            return
        self._run_on_vts_loop(self.trigger_hotkey(hotkey_name), wait=False)

    def trigger_actions_sync(self, actions: VtsActions) -> None:
        with self._semantic_action_lock:
            for action in (actions.facial, actions.body):
                if action is not None:
                    self._trigger_action(action)

    def _trigger_action(self, action: str) -> None:
        configuration = ACTION_HOTKEYS.get(action)
        if configuration is None or not self.is_connected:
            return

        primary_hotkey, reset_hotkey, reset_delay = configuration
        now = time.monotonic()
        with self._action_lock:
            last_triggered = self._last_action_at.get(action)
            if last_triggered is not None and now - last_triggered < ACTION_COOLDOWN_SECONDS:
                return
            self._last_action_at[action] = now

        self.trigger_hotkey_sync(primary_hotkey)
        if reset_hotkey is not None:
            reset_timer = threading.Timer(reset_delay, self.trigger_hotkey_sync, args=(reset_hotkey,))
            reset_timer.daemon = True
            reset_timer.start()

    async def _fetch_catalog(self) -> VtsCatalog | None:
        if not self.is_connected:
            return None
        current = await self._request(self.vts.vts_request.BaseRequest("CurrentModelRequest"))
        current_data = current.get("data", {})
        if not current_data.get("modelLoaded"):
            return None
        model_id = current_data.get("modelID")
        model_name = current_data.get("modelName")
        if not isinstance(model_id, str) or not isinstance(model_name, str):
            return None
        hotkeys = await self._request(self.vts.vts_request.requestHotKeyList())
        expressions = await self._request(
            self.vts.vts_request.BaseRequest(
                "ExpressionStateRequest",
                {"details": False, "expressionFile": ""},
            )
        )
        return build_catalog(
            model_id=model_id,
            model_name=model_name,
            hotkeys=hotkeys.get("data", {}).get("availableHotkeys", []),
            expressions=expressions.get("data", {}).get("expressions", []),
        )

    def current_catalog_sync(self) -> VtsCatalog | None:
        try:
            return self._run_on_vts_loop(self._fetch_catalog(), wait=True)
        except Exception as error:
            logger.warning("Could not read VTube Studio action catalog: %s", error)
            return None

    async def _trigger_catalog_actions(self, catalog: VtsCatalog, codes: tuple[str, ...]) -> None:
        current_catalog = await self._fetch_catalog()
        if current_catalog is None or current_catalog.model_id != catalog.model_id:
            logger.warning("Discarded VTube actions because the active model changed")
            return

        for code in codes:
            entry = catalog.entry_for(code)
            if entry is None:
                continue
            if entry.kind == "hotkey":
                await self._request(self.vts.vts_request.requestTriggerHotKey(entry.identifier))
                continue
            await self._request(
                self.vts.vts_request.BaseRequest(
                    "ExpressionActivationRequest",
                    {"expressionFile": entry.identifier, "active": True, "fadeTime": 0.25},
                )
            )
            expression_key = (catalog.model_id, entry.identifier)
            with self._expression_lock:
                self._active_expressions.add(expression_key)
            self._schedule_expression_deactivation(expression_key)

    def trigger_catalog_actions_sync(self, catalog: VtsCatalog | None, codes: tuple[str, ...]) -> None:
        if catalog is None or not codes:
            return
        try:
            with self._semantic_action_lock:
                self._run_on_vts_loop(self._trigger_catalog_actions(catalog, codes), wait=True)
        except Exception as error:
            logger.warning("Could not trigger VTube catalog actions: %s", error)

    def _schedule_expression_deactivation(self, expression_key: tuple[str, str]) -> None:
        timer = threading.Timer(EXPRESSION_ACTIVE_SECONDS, self._deactivate_expression_sync, args=(expression_key,))
        timer.daemon = True
        timer.start()

    async def _deactivate_expression(self, expression_key: tuple[str, str]) -> None:
        with self._expression_lock:
            if expression_key not in self._active_expressions:
                return
        current_catalog = await self._fetch_catalog()
        if current_catalog is None or current_catalog.model_id != expression_key[0]:
            return
        await self._request(
            self.vts.vts_request.BaseRequest(
                "ExpressionActivationRequest",
                {"expressionFile": expression_key[1], "active": False},
            )
        )
        with self._expression_lock:
            self._active_expressions.discard(expression_key)

    def _deactivate_expression_sync(self, expression_key: tuple[str, str]) -> None:
        try:
            self._run_on_vts_loop(self._deactivate_expression(expression_key), wait=True)
        except Exception as error:
            logger.warning("Could not deactivate VTube expression: %s", error)
