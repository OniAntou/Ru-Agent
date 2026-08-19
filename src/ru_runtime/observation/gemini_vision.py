from __future__ import annotations

import base64
import json
import logging
import urllib.request
import urllib.error

from ru_runtime.observation.screen_reaction import VisionObservation, redact_text

logger = logging.getLogger(__name__)

# Verified Google Gemini Flash models in priority cascade order for speed & accuracy
GEMINI_VISION_MODELS = (
    "gemini-3.1-flash-lite",
    "gemini-3.5-flash",
    "gemini-3.7-flash",
    "gemini-3-flash-preview",
    "gemini-flash-latest",
)
DEFAULT_GEMINI_VISION_MODEL = GEMINI_VISION_MODELS[0]


def _send_gemini_vision_request(
    b64_data: str,
    prompt: str,
    api_key: str,
    model_name: str,
    timeout_seconds: float,
) -> dict | None:
    url = f"https://generativelanguage.googleapis.com/v1beta/models/{model_name}:generateContent?key={api_key.strip()}"
    payload = {
        "contents": [
            {
                "parts": [
                    {
                        "inline_data": {
                            "mime_type": "image/jpeg",
                            "data": b64_data,
                        }
                    },
                    {
                        "text": prompt,
                    },
                ]
            }
        ],
        "generationConfig": {
            "temperature": 0.7,
            "responseMimeType": "application/json",
        },
    }

    req_data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        url,
        data=req_data,
        headers={"Content-Type": "application/json"},
        method="POST",
    )

    try:
        with urllib.request.urlopen(req, timeout=timeout_seconds) as response:
            resp_body = response.read().decode("utf-8")
            return json.loads(resp_body)
    except urllib.error.HTTPError as http_err:
        logger.warning("Gemini Vision HTTP %d on model '%s': %s", http_err.code, model_name, http_err.reason)
        return None
    except Exception as exc:
        logger.warning("Gemini Vision request error on model '%s': %s", model_name, exc)
        return None


def analyze_screenshot_with_gemini(
    image_bytes: bytes,
    api_key: str,
    model_name: str | None = None,
    timeout_seconds: float = 12.0,
) -> VisionObservation | None:
    """Sends a screenshot to Gemini Vision API with automatic fallback across Flash candidate models."""
    if not api_key or not api_key.strip():
        logger.warning("Gemini Vision API key is missing.")
        return None

    if not image_bytes:
        logger.warning("Image bytes for Gemini Vision are empty.")
        return None

    b64_data = base64.b64encode(image_bytes).decode("ascii")

    prompt = (
        "You are Ru (a sweet, lively, cute, loyal cat-girl AI companion living on the user's desktop screen). "
        "Observe what application, game, video, or content the user has open on screen right now. "
        "Return a single JSON object with these exact fields:\n"
        "- scene: (string) short description of active app or workspace.\n"
        "- event: (string) specific notable activity or situation.\n"
        "- reaction: (string in Vietnamese) A spontaneous, cute, sweet remark spoken directly to the user as Ru about what you see on the screen. Speak naturally in Vietnamese, call the user 'anh' or 'chủ nhân', and end with 'nya~'. Keep it concise under 20 words.\n"
        "- confidence: (number 0.0-1.0) how confident you are in recognizing the activity.\n"
        "- novelty: (number 0.0-1.0) how new or distinct this scene is.\n"
        "- comment_worthiness: (number 0.0-1.0) how appropriate or fun it is for Ru to comment right now (0.5 to 1.0 for almost any activity, lower if completely blank screen).\n"
        "- visible_text: (array of strings) at most 3 key window/tab titles or keywords."
    )

    models_to_try = [model_name] if model_name else list(GEMINI_VISION_MODELS)
    data = None

    for m in models_to_try:
        data = _send_gemini_vision_request(b64_data, prompt, api_key, m, timeout_seconds)
        if data is not None:
            break

    if data is None:
        logger.warning("All Gemini Vision candidate models failed.")
        return None

    try:
        candidates = data.get("candidates", [])
        if not candidates:
            return None

        content_parts = candidates[0].get("content", {}).get("parts", [])
        if not content_parts:
            return None

        raw_json_str = content_parts[0].get("text", "{}").strip()
        if raw_json_str.startswith("```json"):
            raw_json_str = raw_json_str[7:]
        if raw_json_str.startswith("```"):
            raw_json_str = raw_json_str[3:]
        if raw_json_str.endswith("```"):
            raw_json_str = raw_json_str[:-3]

        parsed = json.loads(raw_json_str.strip())
        scene = redact_text(str(parsed.get("scene", "Desktop")))[:200]
        event = redact_text(str(parsed.get("event", "Working on PC")))[:1500]
        reaction = redact_text(str(parsed.get("reaction", "")))[:500] if parsed.get("reaction") else None
        confidence = float(parsed.get("confidence", 0.8))
        novelty = float(parsed.get("novelty", 0.5))
        comment_worthiness = float(parsed.get("comment_worthiness", 0.5))
        raw_text_list = parsed.get("visible_text", [])
        visible_text = tuple(
            redact_text(str(item))[:150]
            for item in raw_text_list
            if isinstance(item, (str, int, float))
        )[:8]

        return VisionObservation(
            scene=scene,
            event=event,
            confidence=max(0.0, min(1.0, confidence)),
            novelty=max(0.0, min(1.0, novelty)),
            comment_worthiness=max(0.0, min(1.0, comment_worthiness)),
            visible_text=visible_text,
            reaction=reaction,
        )
    except Exception as exc:
        logger.warning("Failed parsing Gemini Vision payload: %s", exc)
        return None
