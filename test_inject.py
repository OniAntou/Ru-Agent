import asyncio
import sys
import os
import time
import math

sys.stdout.reconfigure(encoding='utf-8')
sys.path.append(os.path.abspath('.'))
from src.mini_gpt.vts_manager import VTSManager

async def test_inject():
    mgr = VTSManager()
    await mgr.connect()
    if not mgr.is_connected:
        print("Failed to connect")
        return

    # Create custom parameter LLM_Sway
    req_create = mgr.vts.vts_request.requestCustomParameter(
        parameter="LLM_Sway",
        min=-30.0,
        max=30.0,
        default=0.0,
        info="Swaying for LLM"
    )
    await mgr.vts.request(req_create)
    print("Created parameter LLM_Sway")

    print("Injecting sine wave for 5 seconds...")
    start_time = time.time()
    while time.time() - start_time < 5.0:
        elapsed = time.time() - start_time
        # Sine wave oscillating between -30 and 30, with a period of 2 seconds
        val = math.sin(elapsed * math.pi) * 30.0

        req_inject = mgr.vts.vts_request.requestSetParameterValue(
            parameter="LLM_Sway",
            value=val
        )
        await mgr.vts.request(req_inject)
        await asyncio.sleep(0.05) # 20fps

    # Reset to 0
    req_inject = mgr.vts.vts_request.requestSetParameterValue(parameter="LLM_Sway", value=0.0)
    await mgr.vts.request(req_inject)

    # Cleanup: xóa custom parameter đã tạo (pyvts hỗ trợ requestDeleteCustomParameter)
    req_delete = mgr.vts.vts_request.requestDeleteCustomParameter(parameter="LLM_Sway")
    await mgr.vts.request(req_delete)
    print("Deleted parameter LLM_Sway")
    print("Done")

if __name__ == '__main__':
    asyncio.run(test_inject())
