#!/usr/bin/env python3
"""Probe script for Gemini Live Vision API.

Tests WebSocket connection to Gemini Live API, handles model fallback
(1011 close code -> retry with gemini-2.0-flash-live-001).
"""

import asyncio
import base64
import json
import os
import sys
import time

import websockets

MODELS = [
    "gemini-2.0-flash-live-001",
    "gemini-2.0-flash-exp",
    "gemini-1.5-flash",
    "gemini-1.5-pro",
    "gemini-2.0-flash",
    "gemini-1.5-flash-001",
    "gemini-1.5-pro-001",
]


def make_dummy_jpeg() -> str:
    """Create a minimal 1x1 pixel JPEG as base64."""
    # Minimal JPEG header + 1x1 pixel data
    jpeg_bytes = bytes.fromhex(
        "FF D8 FF E0 00 10 4A 46 49 46 00 01 01 01 00 48 00 48 00 00 "
        "FF DB 00 43 00 08 06 06 07 06 05 08 07 07 07 09 09 08 0A 0C 14 0D 0C 0B 0B 0C 19 12 13 0F 14 1D 1A 1F 1E 1D 1A 1C 1C 20 24 2E 27 20 22 2C 23 1C 1C 28 37 29 2C 30 31 34 34 34 1F 27 39 3D 38 32 3C 2E 33 34 32 "
        "FF C0 00 11 08 00 01 00 01 03 01 22 00 02 11 01 03 11 01 "
        "FF C4 00 1F 00 00 01 05 01 01 01 01 01 01 00 00 00 00 00 00 00 00 01 02 03 04 05 06 07 08 09 0A 0B "
        "FF C4 00 B5 10 00 02 01 03 03 02 04 03 05 05 04 04 00 00 01 7D 01 02 03 00 04 11 05 12 21 31 41 06 13 51 61 07 22 71 14 32 81 91 A1 08 23 42 B1 C1 15 52 D1 F0 24 33 62 72 82 09 0A 16 17 18 19 1A 25 26 27 28 29 2A 34 35 36 37 38 39 3A 43 44 45 46 47 48 49 4A 53 54 55 56 57 58 59 5A 63 64 65 66 67 68 69 6A 73 74 75 76 77 78 79 7A 83 84 85 86 87 88 89 8A 92 93 94 95 96 97 98 99 9A A2 A3 A4 A5 A6 A7 A8 A9 AA B2 B3 B4 B5 B6 B7 B8 B9 BA C2 C3 C4 C5 C6 C7 C8 C9 CA D2 D3 D4 D5 D6 D7 D8 D9 DA E1 E2 E3 E4 E5 E6 E7 E8 E9 EA F1 F2 F3 F4 F5 F6 F7 F8 F9 FA "
        "FF DA 00 0C 03 01 00 02 11 03 11 00 3F 00 "
        "FF D9"
    )
    return base64.b64encode(jpeg_bytes).decode()


async def try_model(model: str, api_key: str) -> dict:
    """Try connecting with a specific model. Returns result dict."""
    url = (
        f"wss://generativelanguage.googleapis.com/ws/"
        f"google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent"
        f"?key={api_key}"
    )

    setup_message = {
        "setup": {
            "model": f"models/{model}",
            "generation_config": {
                "response_modalities": ["TEXT"],
                "temperature": 0.1,
                "max_output_tokens": 512,
            },
        }
    }

    dummy_frame = make_dummy_jpeg()
    client_content = {
        "client_content": {
            "turns": [
                {
                    "role": "user",
                    "parts": [
                        {"inline_data": {"mime_type": "image/jpeg", "data": dummy_frame}},
                        {"text": "Describe this image briefly."},
                    ],
                }
            ],
            "turn_complete": True,
        }
    }

    start_time = time.time()
    try:
        async with websockets.connect(
            url,
            additional_headers={"Content-Type": "application/json"},
            max_size=10 * 1024 * 1024,
        ) as ws:
            # Send setup
            await ws.send(json.dumps(setup_message))

            # Wait for setup_complete
            setup_complete = False
            while not setup_complete:
                msg = await asyncio.wait_for(ws.recv(), timeout=10.0)
                data = json.loads(msg)
                if "setup_complete" in data:
                    setup_complete = True
                    break
                if "server_content" in data:
                    # May get model turn during setup
                    pass

            # Send test turn
            await ws.send(json.dumps(client_content))

            # Wait for response
            response_text = ""
            turn_complete = False
            while not turn_complete:
                msg = await asyncio.wait_for(ws.recv(), timeout=15.0)
                data = json.loads(msg)
                if "server_content" in data:
                    sc = data["server_content"]
                    if "model_turn" in sc:
                        for part in sc["model_turn"].get("parts", []):
                            if "text" in part:
                                response_text += part["text"]
                    if sc.get("turn_complete"):
                        turn_complete = True
                        break

            latency = time.time() - start_time
            return {
                "success": True,
                "model": model,
                "response": response_text.strip(),
                "latency_sec": round(latency, 3),
            }

    except websockets.exceptions.ConnectionClosedError as e:
        latency = time.time() - start_time
        return {
            "success": False,
            "model": model,
            "close_code": e.code,
            "close_reason": e.reason,
            "latency_sec": round(latency, 3),
        }
    except Exception as e:
        latency = time.time() - start_time
        return {
            "success": False,
            "model": model,
            "error": str(e),
            "latency_sec": round(latency, 3),
        }


async def probe_gemini_vision() -> int:
    """Main probe function. Returns 0 on success, non-zero on failure."""
    api_key = os.environ.get("GEMINI_KEY")
    if not api_key:
        print("ERROR: GEMINI_KEY environment variable not set", file=sys.stderr)
        return 1

    print("Probing Gemini Live Vision API...")
    print("API key loaded from GEMINI_KEY environment variable")

    # First check available models via REST API
    import requests
    models_url = f"https://generativelanguage.googleapis.com/v1beta/models?key={api_key}"
    try:
        r = requests.get(models_url, timeout=10)
        if r.status_code == 200:
            models_data = r.json()
            print(f"\nAvailable models ({len(models_data.get('models', []))}):")
            for m in models_data.get('models', []):
                name = m.get('name', '').replace('models/', '')
                methods = m.get('supportedGenerationMethods', [])
                has_bidi = 'bidiGenerateContent' in methods
                print(f"  - {name}: {methods} {'[HAS BIDI]' if has_bidi else ''}")

            bidi_models = [m for m in models_data.get('models', []) if 'bidiGenerateContent' in m.get('supportedGenerationMethods', [])]
            if not bidi_models:
                print("\n[RESULT] No models support bidiGenerateContent (Live API)")
                print("        This API key (AI Studio) does not have Live API access.")
                print("        Live API requires Vertex AI project or special preview access.")
                return 2  # Special exit code: key valid but no Live API access
    except Exception as e:
        print(f"  Could not list models: {e}")

    # Try WebSocket models (will likely fail with 1008)
    for i, model in enumerate(MODELS):
        print(f"\nTrying model {i+1}/{len(MODELS)}: {model}")
        result = await try_model(model, api_key)

        if result["success"]:
            print("  [OK] SUCCESS")
            print(f"  Model: {result['model']}")
            print(f"  Response: {result['response'][:200]}")
            print(f"  Latency: {result['latency_sec']}s")
            return 0

        print("  [FAIL] FAILED")
        if "close_code" in result:
            print(f"  Close code: {result['close_code']} ({result.get('close_reason', 'N/A')})")
        if "error" in result:
            print(f"  Error: {result['error']}")

        if result.get("close_code") in (1008, 1011) and i + 1 < len(MODELS):
            print(f"  -> Close code {result['close_code']}, falling back to next model...")
            continue
        else:
            return 1

    return 1


if __name__ == "__main__":
    exit_code = asyncio.run(probe_gemini_vision())
    sys.exit(exit_code)
