from __future__ import annotations

import json
import logging
import socket
import time
import urllib.error
from collections.abc import Callable, Sequence
from time import perf_counter
from urllib.error import HTTPError
from urllib.request import Request, urlopen

from ._text_utils import normalize_text

logger = logging.getLogger(__name__)

OPENCODE_BASE_URL = "https://opencode.ai/zen/v1"
KILO_GATEWAY_BASE_URL = "https://api.kilo.ai/api/gateway"
_STREAM_TIMEOUT_SECONDS = 120.0
_MAX_ERROR_BODY_BYTES = 4096
_RETRY_DELAY_SECONDS = 1.0
_MAX_RETRIES = 2
_MAX_RETRY_DELAY_SECONDS = 60.0
_HTTP_UNAUTHORIZED = 401
_HTTP_TOO_MANY_REQUESTS = 429


class _NetworkStreamError(RuntimeError):
    """Transient failure while communicating with the hosted API (retryable)."""

    def __init__(self, message: str, *, retry_after: float | None = None) -> None:
        super().__init__(message)
        # Seconds the server asked us to wait (Retry-After / 429).
        self.retry_after = retry_after


class OpenAIGenerationCancelled(RuntimeError):
    pass


class AuthenticationError(RuntimeError):
    """The hosted API rejected the saved key (HTTP 401)."""


def _parse_retry_after(value: str | None) -> float | None:
    """Parse Retry-After as either delta-seconds or an HTTP-date."""
    if not value:
        return None
    try:
        seconds = float(value.strip())
        return None if seconds < 0 else seconds
    except ValueError:
        pass
    from email.utils import parsedate_to_datetime
    try:
        when = parsedate_to_datetime(value)
    except (TypeError, ValueError):
        return None
    if when is None:
        return None
    return max(0.0, when.timestamp() - time.time())


def _raise_if_cancelled(is_cancelled: Callable[[], bool] | None) -> None:
    if is_cancelled is not None and is_cancelled():
        raise OpenAIGenerationCancelled("Hosted generation was superseded.")


class OpenAIRuntime:
    """Uses the OpenCode OpenAI-compatible API to generate responses."""

    def __repr__(self) -> str:
        return f"OpenAIRuntime(base_url={self.base_url!r}, model_name={self.model_name!r})"

    def __str__(self) -> str:
        return self.__repr__()

    def __init__(
        self,
        api_key: str,
        base_url: str = OPENCODE_BASE_URL,
        model_name: str = "deepseek-v4-flash-free",
        api_variant: str = "Default",
    ) -> None:
        self.api_key = api_key
        self.model_name = model_name
        self.api_variant = api_variant
        self.base_url = base_url.rstrip('/')
        if self.base_url.startswith("http://"):
            logger.warning(
                "base_url uses plaintext HTTP; the API key will be sent unencrypted: %s",
                self.base_url,
            )

    @staticmethod
    def fetch_models(api_key: str, base_url: str) -> list[dict]:
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
        except HTTPError as error:
            if error.code == _HTTP_UNAUTHORIZED:
                raise AuthenticationError(
                    "API key was rejected (HTTP 401). Check the saved key in Settings."
                ) from error
            raise RuntimeError(f"Cannot fetch models: HTTP {error.code} {error.reason}") from error
        except Exception as error:
            raise RuntimeError(f"Cannot fetch models: {error}") from error

        try:
            decoded = json.loads(response_body)
        except json.JSONDecodeError as error:
            raise RuntimeError("API returned invalid JSON when fetching models") from error
        if not isinstance(decoded, dict):
            raise RuntimeError("API returned an invalid response when fetching models")
        data = decoded.get("data")
        if not isinstance(data, list):
            raise RuntimeError("API returned an invalid model list")
        models = [m for m in data if isinstance(m, dict) and isinstance(m.get("id"), str)]
        models.sort(key=lambda m: m["id"])
        return models

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

    def _read_stream(
        self,
        request_factory: Callable[[], Request],
        on_delta: Callable[[str], None] | None,
        *,
        timeout_seconds: float = _STREAM_TIMEOUT_SECONDS,
        is_cancelled: Callable[[], bool] | None = None,
    ) -> str:
        accumulated = ""
        stream_mode: str | None = None  # 'cumulative' or 'incremental'
        saw_data_line = False
        deadline = perf_counter() + timeout_seconds
        try:
            _raise_if_cancelled(is_cancelled)
            with urlopen(request_factory(), timeout=timeout_seconds) as response:
                # OpenCode API is expected to return text/event-stream
                for raw_line in response:
                    if perf_counter() > deadline:
                        raise _NetworkStreamError(
                            "Hosted generation exceeded its total response deadline"
                        )
                    _raise_if_cancelled(is_cancelled)
                    line = raw_line.decode("utf-8").strip()
                    if not line:
                        continue
                    if not line.startswith("data:"):
                        continue
                    saw_data_line = True
                    payload = line.removeprefix("data:").strip()
                    if payload == "[DONE]":
                        break

                    try:
                        decoded = json.loads(payload)
                    except json.JSONDecodeError as error:
                        # Fallback for unexpected payloads (e.g. HTML error pages mixed into stream)
                        raise RuntimeError(f"OpenCode returned invalid JSON: {payload[:50]}") from error

                    error_detail = self._stream_error(decoded)
                    if error_detail:
                        raise RuntimeError(f"OpenCode stream error: {error_detail}")
                    raw_delta = self._event_content(decoded, has_content=bool(accumulated))
                    if raw_delta is None:
                        continue

                    # Providers vary: some emit the full text so far on every
                    # chunk (cumulative), the standard OpenAI format emits
                    # only the new suffix (incremental). Detect the mode once
                    # on the second event and stay consistent afterwards so
                    # text is never duplicated or dropped.
                    if stream_mode is None:
                        stream_mode = "cumulative" if raw_delta.startswith(accumulated) else "incremental"
                    if stream_mode == "cumulative" and raw_delta.startswith(accumulated):
                        new_text = raw_delta[len(accumulated):]
                        accumulated = raw_delta
                    else:
                        stream_mode = "incremental"
                        new_text = raw_delta
                        accumulated += raw_delta

                    if new_text and on_delta is not None:
                        on_delta(new_text)
            _raise_if_cancelled(is_cancelled)
        except HTTPError as error:
            # The error body may echo the request payload; log it separately
            # instead of embedding user content in the exception message.
            error_body = error.read(_MAX_ERROR_BODY_BYTES).decode("utf-8", errors="replace")
            logger.debug("OpenCode API error body (HTTP %s): %s", error.code, error_body)
            message = f"OpenCode API HTTP {error.code}: {error.reason}"
            try:
                payload = json.loads(error_body)
                provider_message = (payload.get("error") or {}).get("message") or ""
            except (ValueError, AttributeError):
                provider_message = ""
            if provider_message and not provider_message.casefold().startswith(error.reason.casefold()):
                message = f"{message}: {provider_message}"
            if error.code == _HTTP_UNAUTHORIZED:
                raise AuthenticationError(
                    "API key was rejected (HTTP 401). Check the saved key in Settings."
                ) from error
            if error.code == _HTTP_TOO_MANY_REQUESTS:
                retry_after = _parse_retry_after(error.headers.get("Retry-After"))
                raise _NetworkStreamError(
                    "OpenCode API rate limited (HTTP 429); retrying after backoff",
                    retry_after=retry_after,
                ) from error
            raise RuntimeError(message) from error
        except (socket.timeout, urllib.error.URLError) as e:
            raise _NetworkStreamError(f"Network error while communicating with OpenCode API: {e}") from e
        if not saw_data_line:
            raise RuntimeError(
                "OpenCode returned a non-SSE response (HTTP 200 but no 'data:' lines found)"
            )
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
        timeout_seconds: float = _STREAM_TIMEOUT_SECONDS,
        max_retries: int = _MAX_RETRIES,
        is_cancelled: Callable[[], bool] | None = None,
        model_override: str | None = None,
        reasoning_effort_override: str | None = None,
    ) -> str:
        if not messages or messages[-1][0] != "user":
            raise ValueError("messages must end with a user turn")
        if not self.api_key:
            raise ValueError("OpenCode API Key is missing. Please set it in Settings.")
        if max_new_tokens <= 0:
            raise ValueError("max new tokens must be positive")
        if not 0 < temperature <= 2:
            raise ValueError("temperature must be in the range (0, 2]")
        if top_k <= 0:
            raise ValueError("top k must be positive")

        body = {
            "model": model_override or self.model_name,
            "messages": [{"role": role, "content": normalize_text(text)} for role, text in messages],
            "max_tokens": max_new_tokens,
            "temperature": temperature,
            "top_k": top_k,
            "stream": True,
        }
        if reasoning_effort_override is not None:
            body["reasoning"] = {"effort": reasoning_effort_override}
        elif self.api_variant and self.api_variant != "Default":
            body["reasoning_effort"] = self.api_variant

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
        for attempt in range(max_retries + 1):
            retry_after: float | None = None
            _raise_if_cancelled(is_cancelled)
            try:
                # Only deliver streaming deltas on the first attempt.
                # On retry the same text arrives again, which would duplicate UI output.
                content = self._read_stream(
                    make_request,
                    on_delta if attempt == 0 else None,
                    timeout_seconds=timeout_seconds,
                    is_cancelled=is_cancelled,
                )
                if content.strip():
                    break
                logger.warning("OpenCode returned an empty stream (attempt %s); retrying", attempt + 1)
            except _NetworkStreamError as error:
                # Retry transient network failures only before any delta has
                # been streamed to the UI (idempotent attempt).
                logger.warning("OpenCode network error (attempt %s): %s", attempt + 1, error)
                if attempt >= max_retries:
                    raise
                retry_after = error.retry_after
            if attempt < max_retries:
                _raise_if_cancelled(is_cancelled)
                if retry_after is None:
                    backoff = _RETRY_DELAY_SECONDS * (attempt + 1)
                else:
                    # Never blindly follow a hostile/very long Retry-After.
                    backoff = min(max(0.0, retry_after), _MAX_RETRY_DELAY_SECONDS)
                time.sleep(backoff)

        if on_complete_timing is not None:
            on_complete_timing(max(0.0, perf_counter() - started))

        if not content.strip():
            raise RuntimeError("OpenCode returned empty chat completions; please try again")
        return normalize_text(content)
