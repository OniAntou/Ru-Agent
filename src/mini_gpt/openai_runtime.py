from __future__ import annotations

import json
from collections.abc import Callable, Sequence
from time import perf_counter
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

from ._text_utils import normalize_text


class OpenAIRuntime:
    """Uses the OpenCode OpenAI-compatible API to generate responses."""

    def __init__(self, api_key: str, base_url: str = "https://api.openai.com/v1", model_name: str = "gpt-4o-mini") -> None:
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
            "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
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
        return True

    def stop(self) -> None:
        pass

    def release_gpu(self) -> None:
        pass

    def generate(
        self,
        messages: Sequence[tuple[str, str]],
        max_new_tokens: int,
        temperature: float,
        top_k: int,
        *,
        on_complete_timing: Callable[[float], None] | None = None,
    ) -> str:
        if not messages or messages[-1][0] != "user":
            raise ValueError("messages must end with a user turn")
        if not self.api_key:
            raise ValueError("API Key is missing for OpenAI API. Please set it in Settings.")
        
        body = {
            "model": self.model_name,
            "messages": [{"role": role, "content": normalize_text(text)} for role, text in messages],
            "max_tokens": max_new_tokens,
            "temperature": temperature,
        }
        
        url = f"{self.base_url}/chat/completions"
        data = json.dumps(body).encode("utf-8")
        headers = {
            "Content-Type": "application/json",
            "Authorization": f"Bearer {self.api_key}",
            "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
        }
        request = Request(url, data=data, headers=headers, method="POST")
        started = perf_counter()
        try:
            with urlopen(request, timeout=120) as response:
                response_body = response.read().decode("utf-8")
        except HTTPError as error:
            detail = error.read().decode("utf-8", errors="replace")
            raise RuntimeError(f"OpenAI HTTP {error.code}: {detail}") from error
        except URLError as error:
            raise RuntimeError(f"OpenAI is not reachable: {error.reason}") from error

        if on_complete_timing is not None:
            on_complete_timing(max(0.0, perf_counter() - started))
            
        try:
            decoded = json.loads(response_body)
        except json.JSONDecodeError as error:
            raise RuntimeError("OpenAI returned invalid JSON") from error
            
        choices = decoded.get("choices", [])
        content = choices[0].get("message", {}).get("content") if choices else None
        
        if not isinstance(content, str) or not content.strip():
            raise RuntimeError("OpenAI returned an empty chat completion")
        return normalize_text(content)
