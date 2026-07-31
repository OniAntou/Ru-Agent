import asyncio
import concurrent.futures
import logging
import os
import random
import threading
import time

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

REQUEST_TIMEOUT_SECONDS = 2.0
VTS_LOOP_WAIT_TIMEOUT_SECONDS = 10.0
SWAY_LOOP_SLEEP_SECONDS = 0.1
DISCONNECTED_POLL_SECONDS = 0.5
ERROR_BACKOFF_SECONDS = 1.0
RECONNECT_MIN_INTERVAL_SECONDS = 5.0
RECONNECT_MAX_ATTEMPTS = 5
MAX_CONCURRENT_REQUESTS = 4
CATALOG_CACHE_TTL_SECONDS = 10.0
VTS_TOKEN_PATH = os.path.join(
    os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))),
    "vts_token.txt",
)

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
            "authentication_token_path": VTS_TOKEN_PATH,
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
        self._injection_paused = False
        self._sway_running = False
        self._sway_task: asyncio.Task | None = None
        self._loop: asyncio.AbstractEventLoop | None = None
        self._request_lock: asyncio.Lock | None = None
        self._request_semaphore: asyncio.Semaphore | None = None
        self._reconnect_lock: asyncio.Lock | None = None
        self._background_tasks: set[asyncio.Task] = set()
        self._timers: list[threading.Timer] = []
        self._injection_chain: asyncio.Task | None = None
        self._catalog_cache: VtsCatalog | None = None
        self._catalog_cache_at = 0.0
        self._last_reconnect_attempt_at = 0.0
        self._reconnect_attempts = 0
        self._reconnect_exhausted_logged = False

    def run(self) -> None:
        """Own the VTube Studio websocket on one persistent background loop."""
        loop = asyncio.new_event_loop()
        self._loop = loop
        asyncio.set_event_loop(loop)
        try:
            loop.run_until_complete(self.connect())
            loop.run_forever()
        finally:
            self.is_connected = False
            loop.close()

    def _ensure_async_primitives(self) -> None:
        if self._request_lock is None:
            self._request_lock = asyncio.Lock()
        if self._request_semaphore is None:
            self._request_semaphore = asyncio.Semaphore(MAX_CONCURRENT_REQUESTS)

    async def _request(self, request: dict) -> dict:
        if not self.is_connected:
            return {}
        self._ensure_async_primitives()
        async with self._request_lock:
            try:
                return await asyncio.wait_for(
                    self.vts.request(request), timeout=REQUEST_TIMEOUT_SECONDS
                )
            except Exception as exc:
                logger.warning("VTS request failed: %s", exc)
                self.is_connected = False
                self._schedule_reconnect()
                return {}

    def _schedule_reconnect(self) -> None:
        if not self._sway_running:
            return
        now = time.monotonic()
        if self._reconnect_attempts >= RECONNECT_MAX_ATTEMPTS:
            if not self._reconnect_exhausted_logged:
                self._reconnect_exhausted_logged = True
                logger.error(
                    "VTS auto-reconnect exhausted after %d attempts",
                    self._reconnect_attempts,
                )
            return
        self._reconnect_exhausted_logged = False
        if now - self._last_reconnect_attempt_at < RECONNECT_MIN_INTERVAL_SECONDS:
            return
        self._last_reconnect_attempt_at = now
        logger.info("VTS connection lost, scheduling reconnect")
        self._spawn_background_task(self._auto_reconnect())

    async def _auto_reconnect(self) -> None:
        if self._reconnect_lock is None:
            self._reconnect_lock = asyncio.Lock()
        async with self._reconnect_lock:
            while self._reconnect_attempts < RECONNECT_MAX_ATTEMPTS:
                await asyncio.sleep(RECONNECT_MIN_INTERVAL_SECONDS)
                if self.is_connected or not self._sway_running:
                    return
                self._reconnect_attempts += 1
                self._last_reconnect_attempt_at = time.monotonic()
                logger.info(
                    "VTS reconnect attempt %d/%d",
                    self._reconnect_attempts,
                    RECONNECT_MAX_ATTEMPTS,
                )
                await self._reconnect()
                if self.is_connected:
                    self._reconnect_attempts = 0
                    self._reconnect_exhausted_logged = False
                    return
            if not self._reconnect_exhausted_logged:
                self._reconnect_exhausted_logged = True
                logger.error(
                    "VTS auto-reconnect exhausted after %d attempts",
                    self._reconnect_attempts,
                )

    def _run_on_vts_loop(self, coroutine, *, wait: bool):
        loop = getattr(self, "_loop", None)
        if loop is not None and loop.is_running():
            future = asyncio.run_coroutine_threadsafe(coroutine, loop)
            if wait:
                try:
                    return future.result(timeout=VTS_LOOP_WAIT_TIMEOUT_SECONDS)
                except concurrent.futures.TimeoutError:
                    logger.debug("VTS loop wait timed out")
                    future.cancel()
                    return None
                except Exception:
                    future.cancel()
                    raise
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
        self._sway_running = False
        self.set_speaking(False)
        self._sway_controller.reset()
        for timer in self._timers:
            timer.cancel()
        self._timers.clear()
        loop = self._loop
        if loop is None or not loop.is_running():
            return

        async def close_connection() -> None:
            sway_task = getattr(self, "_sway_task", None)
            if sway_task is not None and not sway_task.done():
                sway_task.cancel()
                try:
                    await sway_task
                except (asyncio.CancelledError, Exception):
                    pass
            for task in list(self._background_tasks):
                task.cancel()
            self._background_tasks.clear()
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
            try:
                response = await asyncio.wait_for(
                    self.vts.request(self.vts.vts_request.requestHotKeyList()),
                    timeout=REQUEST_TIMEOUT_SECONDS,
                )
                self.hotkeys = response.get("data", {}).get("availableHotkeys", [])
            except Exception as e:
                logger.warning("Could not load hotkeys during connect: %s", e)
            logger.info("VTS Connected. Found %d hotkeys.", len(self.hotkeys))
            await self.setup_custom_parameters()
            self._catalog_cache = None
            self._reconnect_attempts = 0
            self._reconnect_exhausted_logged = False
            self._ensure_sway_task()
        except Exception as e:
            logger.warning("Failed to connect to VTube Studio: %s", e)
            self.is_connected = False

    def _ensure_sway_task(self) -> None:
        if self._loop is None or not self._loop.is_running():
            return
        task = getattr(self, "_sway_task", None)
        if task is not None and not task.done():
            return
        self._sway_task = asyncio.create_task(self.sway_loop())

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
        self._sway_running = True
        self._sway_controller.reset()
        start_time = time.monotonic()
        try:
            while self._sway_running:
                if not self.is_connected:
                    self._schedule_reconnect()
                    await asyncio.sleep(DISCONNECTED_POLL_SECONDS)
                    continue
                try:
                    now = time.monotonic()
                    elapsed = now - start_time

                    # ── Sway / Body ───────────────────────────────────────
                    if self._mouse_tracking_enabled:
                        val = self._mouse_eye_tracker.read_body()
                    else:
                        val = self._sway_controller.update(
                            now, elapsed, speaking=self.is_speaking
                        )

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

                    # ── Batch gửi tất cả parameter mỗi tick ──────────────
                    if not self._injection_paused:
                        req = self.vts.vts_request.requestSetMultiParameterValue(
                            parameters=["LLM_Sway", "LLM_EyeX", "LLM_EyeY",
                                        "LLM_HeadZ", "LLM_HeadY", "LLM_HeadX"],
                            values=[val, eye_x, eye_y, hz, hy, hx],
                        )
                        self._spawn_background_task(self._send_request(req))

                    # ── Micro-actions ─────────────────────────────────────
                    if self._micro_action_scheduler.update(now, self.is_speaking):
                        self._spawn_background_task(self._trigger_micro_action())

                    # ── Idle pose cycle ───────────────────────────────────
                    if self._pose_cycle_scheduler.update(now, self.is_speaking):
                        self._spawn_background_task(self._trigger_pose())

                    await asyncio.sleep(SWAY_LOOP_SLEEP_SECONDS)  # 10 Hz
                except Exception as e:
                    logger.debug("Sway loop error: %s", e)
                    await asyncio.sleep(ERROR_BACKOFF_SECONDS)
        finally:
            self._sway_running = False

    def _spawn_background_task(self, coroutine) -> None:
        task = asyncio.create_task(coroutine)
        self._background_tasks.add(task)
        task.add_done_callback(self._background_tasks.discard)

    async def _send_request(self, request: dict) -> None:
        self._ensure_async_primitives()
        if not self.is_connected:
            return
        async with self._request_semaphore:
            await self._request(request)

    def set_speaking(self, speaking: bool) -> None:
        """Update the speech state consumed by the background sway loop."""
        speaking = bool(speaking)
        if speaking == self.is_speaking:
            return
        self.is_speaking = speaking
        self._micro_action_scheduler.reset()
        self._pose_cycle_scheduler.reset()
        if not speaking:
            self._last_micro_action_identifier = None

    def set_injection_paused(self, paused: bool) -> None:
        """Toggle VTS WebSocket connection entirely for FPS test."""
        self._injection_paused = bool(paused)
        self._run_on_vts_loop(self._run_injection_toggle(), wait=False)
        logger.info("VTS injection paused=%s", paused)

    async def _run_injection_toggle(self) -> None:
        chain = self._injection_chain
        if chain is not None and chain is not asyncio.current_task():
            try:
                await chain
            except (asyncio.CancelledError, Exception):
                pass
        self._injection_chain = asyncio.current_task()
        try:
            if self._injection_paused:
                await self._disconnect()
            else:
                await self._reconnect()
                self._ensure_sway_task()
        finally:
            self._injection_chain = None

    async def _disconnect(self) -> None:
        try:
            await self.vts.close()
        except Exception:
            pass
        self.is_connected = False

    async def _reconnect(self) -> None:
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
                logger.error("VTS reconnect auth failed")
                return
            self.is_connected = True
            await self.setup_custom_parameters()
            self._catalog_cache = None
            self._reconnect_attempts = 0
            self._reconnect_exhausted_logged = False
            self._ensure_sway_task()
            logger.info("VTS reconnected")
        except Exception as e:
            self.is_connected = False
            logger.error("VTS reconnect failed: %s", e)

    # ── Mouse eye tracking ──────────────────────────────────────────────

    def set_mouse_tracking(self, enabled: bool) -> None:
        """Toggle cursor-follow gaze + head motion.

        When *enabled* the ``sway_loop`` reads the cursor position directly
        every tick (10 Hz) and feeds it to ``MouseEyeTracker`` so both eye
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
            import ctypes
            import ctypes.wintypes
            pt = ctypes.wintypes.POINT()
            if ctypes.windll.user32.GetCursorPos(ctypes.byref(pt)):
                return (pt.x, pt.y)
        except Exception:
            pass
        try:
            import pyautogui  # type: ignore[import-untyped]
            return pyautogui.position()  # type: ignore[no-any-return]
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
        if not self._semantic_action_lock.acquire(blocking=False):
            return
        try:
            catalog = await self._fetch_catalog()
            if catalog is None:
                return
            candidates = body_pose_entries(catalog.entries)
            entry = self._pose_cycle_scheduler.pick(candidates)
            if entry is None or not entry.code:
                return
            await self._trigger_catalog_actions(catalog, (entry.code,))
            logger.info("Idle pose: %s", entry.label)
        finally:
            self._semantic_action_lock.release()

    async def trigger_hotkey(self, hotkey_name):
        if not self.is_connected:
            return

        for attempt in range(2):
            if not self.hotkeys:
                await self._reload_hotkeys()
            for hk in self.hotkeys:
                if hk["name"].lower() == hotkey_name.lower():
                    request_msg = self.vts.vts_request.requestTriggerHotKey(hk["hotkeyID"])
                    await self._request(request_msg)
                    logger.info("Triggered hotkey: %s", hk["name"])
                    return
            if attempt == 0:
                await self._reload_hotkeys()

        logger.warning("Hotkey '%s' not found.", hotkey_name)

    async def _reload_hotkeys(self) -> None:
        try:
            response = await self._request(self.vts.vts_request.requestHotKeyList())
            hotkeys = response.get("data", {}).get("availableHotkeys", [])
            if hotkeys:
                self.hotkeys = hotkeys
                logger.info("Reloaded %d hotkeys", len(hotkeys))
        except Exception as error:
            logger.warning("Could not reload hotkeys: %s", error)

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
            self._timers.append(reset_timer)
            reset_timer.start()

    async def _fetch_catalog(self) -> VtsCatalog | None:
        if not self.is_connected:
            return None
        now = time.monotonic()
        if self._catalog_cache is not None and now - self._catalog_cache_at < CATALOG_CACHE_TTL_SECONDS:
            return self._catalog_cache
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
        catalog = build_catalog(
            model_id=model_id,
            model_name=model_name,
            hotkeys=hotkeys.get("data", {}).get("availableHotkeys", []),
            expressions=expressions.get("data", {}).get("expressions", []),
        )
        self._catalog_cache = catalog
        self._catalog_cache_at = now
        return catalog

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
        self._timers.append(timer)
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
        finally:
            with self._expression_lock:
                self._active_expressions.discard(expression_key)
