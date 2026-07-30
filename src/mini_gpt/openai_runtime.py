from __future__ import annotations

import json
from collections.abc import Callable, Sequence
from time import perf_counter
from urllib.request import Request, urlopen

from ._text_utils import normalize_text

OPENCODE_BASE_URL = "https://opencode.ai/zen/v1"


class OpenAIRuntime:
    """Uses the OpenCode OpenAI-compatible API to generate responses."""

    def __init__(
        self,
        api_key: str,
        base_url: str = OPENCODE_BASE_URL,
        model_name: str = "deepseek-v4-flash-free",
    ) -> None:
        self.api_key = api_key
        self.model_name = model_name
        self.base_url = base_url.rstrip('/')

    @staticmethod
    def fetch_models(api_key: str, base_url: str) -> list[str]:
        if not api_key:
            raise ValueError("API Key is missing")
        base_url = base_url.rstrip('/')
        url = f"{base_url}/models"
        headers = {
            "Authorization": f"Bearer {api_key}",
            "User-Agent": "Ru-LocalLLM/0.1",
        }
        request = Request(url, headers=headers, method="GET")
        try:
            with urlopen(request, timeout=30) as response:
                response_body = response.read().decode("utf-8")
        except Exception as error:
            raise RuntimeError(f"Cannot fetch models: {error}") from error
            
        try:
            decoded = json.loads(response_body)
            models = [m.get("id") for m in decoded.get("data", []) if "id" in m]
            return sorted(models)
        except json.JSONDecodeError as error:
            raise RuntimeError("API returned invalid JSON when fetching models") from error

    def is_cached(self) -> bool:
        return False

    def stop(self) -> None:
        pass

    def release_gpu(self) -> None:
        pass

    @staticmethod
    def _event_content(decoded: object, *, has_content: bool) -> str | None:
        if not isinstance(decoded, dict):
            return None
        choices = decoded.get("choices")
        if not isinstance(choices, list) or not choices or not isinstance(choices[0], dict):
            return None
        choice = choices[0]
        delta = choice.get("delta")
        if isinstance(delta, dict) and isinstance(delta.get("content"), str):
            return delta["content"]
        message = choice.get("message")
        if not has_content and isinstance(message, dict) and isinstance(message.get("content"), str):
            return message["content"]
        return None

    @staticmethod
    def _stream_error(decoded: object) -> str | None:
        if not isinstance(decoded, dict):
            return None
        error = decoded.get("error")
        if isinstance(error, str) and error.strip():
            return error.strip()
        if isinstance(error, dict):
            message = error.get("message")
            if isinstance(message, str) and message.strip():
                return message.strip()
        return None

    def _read_stream(self, request_factory: Callable[[], Request], on_delta: Callable[[str], None] | None) -> str:
        accumulated = ""
        with urlopen(request_factory(), timeout=120) as response:
            for raw_line in response:
                line = raw_line.decode("utf-8").strip()
                if not line.startswith("data: "):
                    continue
                payload = line[6:]
                if payload == "[DONE]":
                    break
                try:
                    decoded = json.loads(payload)
                except json.JSONDecodeError as error:
                    raise RuntimeError("OpenCode returned invalid JSON") from error
                error_detail = self._stream_error(decoded)
                if error_detail:
                    raise RuntimeError(f"OpenCode stream error: {error_detail}")
                raw_delta = self._event_content(decoded, has_content=bool(accumulated))
                if raw_delta is None:
                    continue

                # Support both cumulative deltas (full text each chunk) and
                # incremental deltas (the standard OpenAI streaming format).
                if not accumulated:
                    new_text = raw_delta
                    accumulated = raw_delta
                elif raw_delta.startswith(accumulated):
                    # Cumulative / prefix delta — emit only the new suffix.
                    new_text = raw_delta[len(accumulated):]
                    accumulated = raw_delta
                else:
                    # Incremental delta (standard OpenAI format) — append directly.
                    new_text = raw_delta
                    accumulated += raw_delta

                if new_text and on_delta is not None:
                    on_delta(new_text)
        return accumulated

    def generate(
        self,
        messages: Sequence[tuple[str, str]],
        max_new_tokens: int,
        temperature: float,
        top_k: int,
        *,
        on_delta: Callable[[str], None] | None = None,
        on_complete_timing: Callable[[float], None] | None = None,
    ) -> str:
        if not messages or messages[-1][0] != "user":
            raise ValueError("messages must end with a user turn")
        if not self.api_key:
            raise ValueError("OpenCode API Key is missing. Please set it in Settings.")
        
        body = {
            "model": self.model_name,
            "messages": [{"role": role, "content": normalize_text(text)} for role, text in messages],
            "max_tokens": max_new_tokens,
            "temperature": temperature,
            "stream": True,
        }
        
        url = f"{self.base_url}/chat/completions"
        data = json.dumps(body).encode("utf-8")
        headers = {
            "Content-Type": "application/json",
            "Authorization": f"Bearer {self.api_key}",
            "User-Agent": "Ru-LocalLLM/0.1",
        }

        def make_request() -> Request:
            return Request(url, data=data, headers=headers, method="POST")

        started = perf_counter()
        content = ""
        for attempt in range(2):
            # Only deliver streaming deltas on the first attempt.
            # On retry the same text arrives again, which would duplicate UI output.
            content = self._read_stream(make_request, on_delta if attempt == 0 else None)
            if content.strip():
                break

        if on_complete_timing is not None:
            on_complete_timing(max(0.0, perf_counter() - started))
            
        if not content.strip():
            raise RuntimeError("OpenCode returned empty chat completions; please try again")
        return normalize_text(content)
