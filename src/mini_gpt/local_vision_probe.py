from __future__ import annotations

import json
import math
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path


VISION_OBSERVATION_PROMPT = """Inspect these consecutive primary-monitor frames and return only JSON.
Describe the current scene, the newest visually supported event, confidence, novelty,
whether the event is worth a brief social comment, and any relevant visible text.
Do not give advice, diagnose software, or invent activity outside the frames."""

# llama.cpp b10142 cannot compile number min/max or a 2,000-character string
# repetition into its request grammar. parse_response enforces those bounds
# after the grammar has guaranteed the exact keys and JSON value types.
_SCORE_SCHEMA = {"type": "number"}
_OBSERVATION_SCHEMA = {
    "type": "object",
    "additionalProperties": False,
    "required": [
        "scene",
        "event",
        "confidence",
        "novelty",
        "comment_worthiness",
        "visible_text",
    ],
    "properties": {
        "scene": {"type": "string", "maxLength": 500},
        "event": {"type": "string"},
        "confidence": _SCORE_SCHEMA,
        "novelty": _SCORE_SCHEMA,
        "comment_worthiness": _SCORE_SCHEMA,
        "visible_text": {
            "type": "array",
            "maxItems": 16,
            "items": {"type": "string", "maxLength": 200},
        },
    },
}
VISION_JSON_SCHEMA = {
    "type": "json_object",
    "schema": _OBSERVATION_SCHEMA,
}

_REQUIRED_FIELDS = frozenset(_OBSERVATION_SCHEMA["required"])


@dataclass(frozen=True, slots=True)
class VisionProbeConfig:
    project_root: Path
    port: int

    @property
    def model_dir(self) -> Path:
        return self.project_root / "models" / "vision" / "qwen3-vl-2b-instruct"


def build_server_command(config: VisionProbeConfig) -> list[str]:
    return [
        str(config.project_root / "tools" / "llama.cpp-bin" / "llama-server.exe"),
        "--model",
        str(config.model_dir / "Qwen3VL-2B-Instruct-Q4_K_M.gguf"),
        "--mmproj",
        str(config.model_dir / "mmproj-Qwen3VL-2B-Instruct-Q8_0.gguf"),
        "--host",
        "127.0.0.1",
        "--port",
        str(config.port),
        "--ctx-size",
        "2048",
        "--n-predict",
        "192",
        "--parallel",
        "1",
        "--n-gpu-layers",
        "99",
        "--flash-attn",
        "on",
        "--cache-type-k",
        "q8_0",
        "--cache-type-v",
        "q8_0",
        "--image-max-tokens",
        "256",
        "--reasoning",
        "off",
        "--no-webui",
        # Same llama-server auto chat-parser guard as the main GGUF runtime.
        "--skip-chat-parsing",
    ]


def build_request(images_b64: Sequence[str]) -> dict[str, object]:
    if not 1 <= len(images_b64) <= 4:
        raise ValueError("vision probe requires one to four images")
    if any(not isinstance(value, str) or not value for value in images_b64):
        raise ValueError("vision probe images must be non-empty base64 strings")
    content: list[dict[str, object]] = [{"type": "text", "text": VISION_OBSERVATION_PROMPT}]
    content.extend(
        {"type": "image_url", "image_url": {"url": f"data:image/jpeg;base64,{value}"}}
        for value in images_b64
    )
    return {
        "model": "qwen3-vl-2b-instruct",
        "messages": [{"role": "user", "content": content}],
        "temperature": 0.1,
        "max_tokens": 192,
        "stream": False,
        "response_format": VISION_JSON_SCHEMA,
    }


def _invalid_schema() -> ValueError:
    return ValueError("vision response does not match the observation schema")


def parse_response(payload: dict[str, object]) -> dict[str, object]:
    try:
        content = payload["choices"][0]["message"]["content"]  # type: ignore[index]
        decoded = json.loads(content)  # type: ignore[arg-type]
    except (KeyError, IndexError, TypeError, json.JSONDecodeError) as error:
        raise _invalid_schema() from error
    if not isinstance(decoded, dict) or set(decoded) != _REQUIRED_FIELDS:
        raise _invalid_schema()
    scene = decoded["scene"]
    event = decoded["event"]
    visible_text = decoded["visible_text"]
    if not isinstance(scene, str) or len(scene) > 500:
        raise _invalid_schema()
    if not isinstance(event, str) or len(event) > 2000:
        raise _invalid_schema()
    if (
        not isinstance(visible_text, list)
        or len(visible_text) > 16
        or any(not isinstance(value, str) or len(value) > 200 for value in visible_text)
    ):
        raise _invalid_schema()
    for field in ("confidence", "novelty", "comment_worthiness"):
        value = decoded[field]
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            raise _invalid_schema()
        if not math.isfinite(value) or not 0 <= value <= 1:
            raise _invalid_schema()
    return decoded
