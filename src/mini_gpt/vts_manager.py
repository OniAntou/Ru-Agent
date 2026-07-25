import asyncio
import logging

import pyvts

logger = logging.getLogger(__name__)


class VTSManager:
    def __init__(self):
        plugin_info = {
            "plugin_name": "Mini-GPT Ru",
            "developer": "Antigravity",
            "authentication_token_path": "./vts_token.txt",
        }
        self.vts = pyvts.vts(plugin_info=plugin_info)
        self.is_connected = False
        self.hotkeys = []

    async def connect(self):
        try:
            await self.vts.connect()
            await self.vts.request_authenticate_token()
            await self.vts.request_authenticate()
            self.is_connected = True
            response = await self.vts.request(self.vts.vts_request.requestHotKeyList())
            self.hotkeys = response["data"]["availableHotkeys"]
            logger.info("VTS Connected. Found %d hotkeys.", len(self.hotkeys))
        except Exception as e:
            logger.warning("Failed to connect to VTube Studio: %s", e)
            self.is_connected = False

    async def trigger_hotkey(self, hotkey_name):
        if not self.is_connected:
            return

        for hk in self.hotkeys:
            if hk["name"].lower() == hotkey_name.lower():
                request_msg = self.vts.vts_request.requestTriggerHotKey(hk["hotkeyID"])
                await self.vts.request(request_msg)
                logger.info("Triggered hotkey: %s", hk["name"])
                return

        logger.warning("Hotkey '%s' not found.", hotkey_name)

    def trigger_hotkey_sync(self, hotkey_name):
        if not self.is_connected:
            return
        try:
            loop = asyncio.get_running_loop()
            loop.create_task(self.trigger_hotkey(hotkey_name))
        except RuntimeError:
            asyncio.run(self.trigger_hotkey(hotkey_name))
