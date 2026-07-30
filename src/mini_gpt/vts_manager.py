import asyncio
import logging
import random
import time
import threading

import pyvts

from .vts_actions import VtsActions
from .vts_catalog import VtsCatalog, build_catalog
from .vts_motion import (
    body_pose_entries,
    EyeMotionController,
    head_nod_value,
    head_tilt_value,
    head_yaw_value,
    is_idle_safe_micro_action,
    MicroActionScheduler,
    MouseEyeTracker,
    OrganicSwayController,
    PoseCycleScheduler,
    safe_micro_action_entries,
)

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
        self._sway_controller = OrganicSwayController()
        self._eye_controller = EyeMotionController(self._motion_random)
        self._mouse_eye_tracker = MouseEyeTracker()
        self._mouse_tracking_enabled = False
        self._pose_cycle_scheduler = PoseCycleScheduler(self._motion_random)
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
        try:
            asyncio.run(coroutine)
        except Exception as exc:
            logger.debug("Background VTS coroutine failed: %s", exc)
        return None

    def shutdown(self) -> None:
        self.is_connected = False
        self.set_speaking(False)
        self._sway_controller.reset()
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
            self._sway_task = asyncio.create_task(self.sway_loop())
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
            logger.error("Failed to create custom parameter LLM_Sway: %s", e)

        for name, min_val, max_val, info in (
            ("LLM_HeadZ", -15.0, 15.0, "Head tilt (PARAM_ANGLE_Z)"),
            ("LLM_HeadY", -15.0, 15.0, "Head nod (PARAM_ANGLE_Y)"),
            ("LLM_HeadX", -10.0, 10.0, "Head yaw (PARAM_ANGLE_X)"),
            ("LLM_EyeX",  -10.0, 10.0, "Eye look left/right"),
            ("LLM_EyeY",   -6.0,  6.0, "Eye look up/down"),
        ):
            try:
                req = self.vts.vts_request.requestCustomParameter(
                    parameter=name, min=min_val, max=max_val,
                    default_value=0.0, info=info,
                )
                await self._request(req)
                logger.info("Created VTS custom parameter: %s", name)
            except Exception as e:
                logger.debug("Could not create %s: %s", name, e)

    async def sway_loop(self):
        self._sway_controller.reset()
        start_time = time.monotonic()
        while self.is_connected:
            try:
                now = time.monotonic()
                elapsed = now - start_time

                # ── Sway / Body ───────────────────────────────────────
                if self._mouse_tracking_enabled:
                    # Mouse tracking: body xoay theo hướng chuột
                    val = self._mouse_eye_tracker.read_body()
                else:
                    # Idle: body lắc tự nhiên
                    val = self._sway_controller.update(
                        now, elapsed, speaking=self.is_speaking
                    )
                req_inject = self.vts.vts_request.requestSetParameterValue(
                    parameter="LLM_Sway", value=val
                )
                await self._request(req_inject)

                # ── Eye + Head motion ──────────────────────────────────
                if self._mouse_tracking_enabled:
                    try:
                        cx, cy = self._cursor_pos()
                        self._mouse_eye_tracker.feed_position(cx, cy)
                    except Exception:
                        pass
                    eye_x, eye_y = self._mouse_eye_tracker.read_eye()
                    hz, hy, hx = self._mouse_eye_tracker.read_head()
                else:
                    eye_x, eye_y = self._eye_controller.update(now)
                    hz = head_tilt_value(elapsed)
                    hy = head_nod_value(elapsed)
                    hx = head_yaw_value(elapsed)

                await self._request(
                    self.vts.vts_request.requestSetParameterValue(
                        parameter="LLM_EyeX", value=eye_x
                    )
                )
                await self._request(
                    self.vts.vts_request.requestSetParameterValue(
                        parameter="LLM_EyeY", value=eye_y
                    )
                )
                await self._request(
                    self.vts.vts_request.requestSetParameterValue(
                        parameter="LLM_HeadZ", value=hz
                    )
                )
                await self._request(
                    self.vts.vts_request.requestSetParameterValue(
                        parameter="LLM_HeadY", value=hy
                    )
                )
                await self._request(
                    self.vts.vts_request.requestSetParameterValue(
                        parameter="LLM_HeadX", value=hx
                    )
                )

                # ── Micro-actions ─────────────────────────────────────
                if self._micro_action_scheduler.update(now, self.is_speaking):
                    await self._trigger_micro_action()

                # ── Idle pose cycle ───────────────────────────────────
                if self._pose_cycle_scheduler.update(now, self.is_speaking):
                    await self._trigger_pose()

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
            self._pose_cycle_scheduler.reset()
            if not self.is_speaking:
                self._last_micro_action_identifier = None

    # ── Mouse eye tracking ──────────────────────────────────────────────

    def set_mouse_tracking(self, enabled: bool) -> None:
        """Toggle cursor-follow gaze + head motion.

        When *enabled* the ``sway_loop`` reads the cursor position directly
        every tick (20 Hz) and feeds it to ``MouseEyeTracker`` so both eye
        and head parameters move in sync — eyes quick, head slow.
        When *disabled* the idle sine-wave head motion resumes.
        """
        self._mouse_tracking_enabled = bool(enabled)
        if enabled:
            # Reset eye + head, giữ body để tránh giật
            self._mouse_eye_tracker.reset()
            self._mouse_eye_tracker.start()
        else:
            self._mouse_eye_tracker.reset()

    @staticmethod
    def _cursor_pos() -> tuple[int, int]:
        """Get global cursor coordinates via best available API."""
        try:
            import pyautogui  # type: ignore[import-untyped]
            return pyautogui.position()  # type: ignore[no-any-return]
        except Exception:
            pass
        try:
            import ctypes
            import ctypes.wintypes
            pt = ctypes.wintypes.POINT()
            ctypes.windll.user32.GetCursorPos(ctypes.byref(pt))
            return (pt.x, pt.y)
        except Exception:
            return (0, 0)

    async def _trigger_micro_action(self) -> None:
        """Trigger one safe catalog action without racing semantic actions.

        Fires during both *speaking* and *idle* states:

        *speaking* → any safe micro-action (blink, ear twitch, head nod …).
        *idle*     → stricter subset (no tail, no body-level motions).
        """

        if not self.is_connected:
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
            if not self.is_speaking and candidates:
                candidates = [
                    e for e in candidates
                    if is_idle_safe_micro_action(e.label, e.identifier)
                ]
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

    async def _trigger_pose(self) -> None:
        """Pick and fire one body-pose hotkey from the catalog."""
        if not self.is_connected:
            return
        with self._semantic_action_lock:
            catalog = await self._fetch_catalog()
            if catalog is None:
                return
            candidates = body_pose_entries(catalog.entries)
            entry = self._pose_cycle_scheduler.pick(candidates)
            if entry is None or not entry.code:
                return
            await self._trigger_catalog_actions(catalog, (entry.code,))
            logger.info("Idle pose: %s", entry.label)

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
        if reset_hotkey is not None and self.is_connected:
            def _reset_with_guard() -> None:
                if self.is_connected:
                    self.trigger_hotkey_sync(reset_hotkey)
            reset_timer = threading.Timer(reset_delay, _reset_with_guard)
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
        if current_catalog is None:
            return
        if current_catalog.model_id != catalog.model_id:
            # Model switched — old model's expressions are irrelevant on the new model.
            with self._expression_lock:
                self._active_expressions.clear()
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
            # Model changed or catalog unavailable — expression is orphaned, just clean up.
            with self._expression_lock:
                self._active_expressions.discard(expression_key)
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
