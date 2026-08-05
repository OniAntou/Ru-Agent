from __future__ import annotations

import json
import logging
import os
import secrets
import subprocess
import threading
import time
from collections.abc import Callable, Sequence
from pathlib import Path
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

from ._text_utils import normalize_text

logger = logging.getLogger(__name__)

# 8188 is ComfyUI's default port; pick a dedicated high port for llama-server.
DEFAULT_PORT = 18088
DEFAULT_START_TIMEOUT_SECONDS = 90.0
_CONTEXT_SIZE_TOKENS = 2048
_HEALTH_CHECK_TIMEOUT_SECONDS = 2.0
_HEALTH_POLL_SLEEP_SECONDS = 0.25
_REQUEST_TIMEOUT_SECONDS = 120.0
_STOP_TIMEOUT_SECONDS = 10.0
_MAX_ERROR_BODY_BYTES = 4096
_DEFAULT_GPU_LAYERS = "all"
_GPU_LAYER_FALLBACKS = (_DEFAULT_GPU_LAYERS, "20", "0")
_DEFAULT_REPEAT_PENALTY = 1.4
_DEFAULT_REPEAT_LAST_N = 512
_CHARS_PER_TOKEN_ESTIMATE = 4
_CONTEXT_PROTOCOL_RESERVE_TOKENS = 64


def fit_messages_to_context(
    messages: Sequence[tuple[str, str]],
    max_new_tokens: int,
    *,
    context_size_tokens: int = _CONTEXT_SIZE_TOKENS,
) -> tuple[tuple[str, str], ...]:
    """Conservatively fit messages into llama.cpp's fixed context window.

    The base system contract and current user turn always survive. Saved memory
    and the oldest completed turns are discarded first.
    """
    if not messages or messages[0][0] != "system" or messages[-1][0] != "user":
        raise ValueError("messages must start with system and end with user")
    input_tokens = context_size_tokens - max_new_tokens - _CONTEXT_PROTOCOL_RESERVE_TOKENS
    if input_tokens <= 0:
        raise ValueError("max new tokens leaves no room for the prompt")
    budget = input_tokens * _CHARS_PER_TOKEN_ESTIMATE
    first_role, first_content = messages[0]
    last_role, last_content = messages[-1]
    mandatory_size = len(first_content) + len(last_content)
    if mandatory_size > budget:
        system_budget = min(len(first_content), max(1, budget // 2))
        user_budget = max(1, budget - system_budget)
        return (
            (first_role, first_content[:system_budget]),
            (last_role, last_content[:user_budget]),
        )

    selected = {0, len(messages) - 1}
    used = mandatory_size
    for index in range(len(messages) - 2, 0, -1):
        content_size = len(messages[index][1])
        if used + content_size > budget:
            continue
        selected.add(index)
        used += content_size
    ordered = [messages[index] for index in sorted(selected)]
    while len(ordered) > 2 and ordered[1][0] == "assistant":
        ordered.pop(1)
    return tuple(ordered)


class LlamaCppRuntime:
    """Own one local llama.cpp server for the optional Ru GGUF experiment."""

    def __init__(
        self,
        *,
        project_root: Path,
        request: Callable[[str, dict[str, Any] | None, float], dict[str, Any]] | None = None,
        popen: Callable[..., Any] = subprocess.Popen,
        port: int = DEFAULT_PORT,
        start_timeout_seconds: float = DEFAULT_START_TIMEOUT_SECONDS,
    ) -> None:
        self.project_root = Path(project_root)
        self.port = port
        self.start_timeout_seconds = start_timeout_seconds
        # Per-process bearer key for the owned llama-server; any local process
        # can reach 127.0.0.1:18088, so the server is not left unauthenticated.
        self.api_key = secrets.token_hex(32)
        self._request_impl = request or self._request_json
        self._popen = popen
        self._process: Any | None = None
        self._log_handle: Any | None = None
        # Reentrant: ensure_started() calls stop() while holding the lock.
        self._lifecycle_lock = threading.RLock()

    @property
    def server_executable(self) -> Path:
        return self.project_root / "tools" / "llama.cpp-bin" / "llama-server.exe"

    @property
    def base_model(self) -> Path:
        return self.project_root / "models" / "gguf" / "llama3.1-8b-instruct-q4_k_m.gguf"

    @property
    def lora_adapter(self) -> Path | None:
        candidates = (
            self.project_root / "models" / "gguf" / "ru-llama3.1-8b-instruct-v6.2-lora.gguf",
            self.project_root / "models" / "gguf" / "ru-llama3.1-8b-instruct-v6.1-lora.gguf",
            self.project_root / "models" / "gguf" / "ru-llama3.1-8b-instruct-v6-lora.gguf",
        )
        for path in candidates:
            try:
                if path.is_file() and path.stat().st_size:
                    return path
            except OSError:
                continue
        return None

    @property
    def log_path(self) -> Path:
        return self.project_root / "logs" / "gguf" / "llama-server.log"

    @property
    def base_url(self) -> str:
        return f"http://127.0.0.1:{self.port}"

    def server_command(self, gpu_layers: str = _DEFAULT_GPU_LAYERS) -> tuple[str, ...]:
        threads = str(os.cpu_count() or 4)
        command = [
            str(self.server_executable),
            "--model",
            str(self.base_model),
            "--host",
            "127.0.0.1",
            "--port",
            str(self.port),
            "--ctx-size",
            str(_CONTEXT_SIZE_TOKENS),
            "--n-gpu-layers",
            gpu_layers,
            "--parallel",
            "1",
            "--threads",
            threads,
            # llama-server b10142 auto-builds a PEG grammar from the chat
            # template and hard-fails (HTTP 500 "does not match the expected
            # peg-native format") whenever a plain-text reply does not parse.
            # Ru does not use server-side tools, so force the pure content
            # parser instead.
            "--skip-chat-parsing",
            "--api-key",
            self.api_key,
        ]
        lora_adapter = self.lora_adapter
        if lora_adapter is not None:
            command[1:1] = ["--lora", str(lora_adapter)]
        return tuple(command)

    def validate_assets(self) -> None:
        lora_adapter = self.lora_adapter
        if lora_adapter is None:
            raise FileNotFoundError("Ru LoRA GGUF (v6.2, v6.1, or v6) not found")
        for path, label in (
            (self.server_executable, "llama-server"),
            (self.base_model, "base GGUF"),
            (lora_adapter, "Ru LoRA GGUF (v6.2, v6.1, or v6)"),
        ):
            try:
                st = path.stat()
                if st.st_size == 0:
                    raise FileNotFoundError(f"{label} is empty: {path}")
            except OSError:
                raise FileNotFoundError(f"{label} not found: {path}")

    def is_running(self) -> bool:
        return self._process is not None and self._process.poll() is None

    def is_cached(self) -> bool:
        return self.is_running()

    def ensure_started(self) -> None:
        # The whole start/stop lifecycle must be serialized: two threads could
        # otherwise both see "not running", spawn two servers on the same port,
        # or close a log handle the other is still writing to.
        with self._lifecycle_lock:
            if self.is_running():
                return
            self.stop()
            self.validate_assets()
            last_error: Exception | None = None
            for gpu_layers in _GPU_LAYER_FALLBACKS:
                try:
                    self._start_server(gpu_layers)
                    return
                except RuntimeError as error:
                    last_error = error
                    logger.warning(
                        "llama.cpp failed to start with n-gpu-layers=%s: %s",
                        gpu_layers,
                        error,
                    )
            raise RuntimeError(f"llama.cpp server failed to start with any GPU layer configuration: {last_error}")

    def _start_server(self, gpu_layers: str) -> None:
        self.log_path.parent.mkdir(parents=True, exist_ok=True)
        log_handle = self.log_path.open("a", encoding="utf-8")
        try:
            creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
            self._process = self._popen(
                self.server_command(gpu_layers),
                cwd=self.project_root,
                stdout=log_handle,
                stderr=subprocess.STDOUT,
                creationflags=creationflags,
            )
            self._log_handle = log_handle
        except Exception:
            log_handle.close()
            raise
        deadline = time.monotonic() + self.start_timeout_seconds
        last_error = "server did not become healthy"
        while time.monotonic() < deadline:
            if self._process.poll() is not None:
                last_error = f"server exited with code {self._process.returncode}"
                break
            try:
                self._request("/health", None, timeout=_HEALTH_CHECK_TIMEOUT_SECONDS)
                return
            except RuntimeError as error:
                last_error = str(error)
                time.sleep(_HEALTH_POLL_SLEEP_SECONDS)
        self.stop()
        raise RuntimeError(
            f"llama.cpp server failed to start with n-gpu-layers={gpu_layers}: {last_error}. See {self.log_path}"
        )

    def stop(self) -> None:
        with self._lifecycle_lock:
            process = self._process
            if process is not None and process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=_STOP_TIMEOUT_SECONDS)
                except subprocess.TimeoutExpired:
                    process.kill()
                    try:
                        process.wait(timeout=_STOP_TIMEOUT_SECONDS)
                    except subprocess.TimeoutExpired:
                        logger.warning(
                            "llama.cpp server did not exit after SIGKILL within %ss; abandoning its handle",
                            _STOP_TIMEOUT_SECONDS,
                        )
            self._process = None
            if self._log_handle is not None:
                try:
                    self._log_handle.close()
                except OSError:
                    pass
                self._log_handle = None

    def generate(
        self,
        messages: Sequence[tuple[str, str]],
        max_new_tokens: int,
        temperature: float,
        top_k: int,
        *,
        on_delta: Callable[[str], None] | None = None,
    ) -> str:
        if not messages or messages[-1][0] != "user":
            raise ValueError("messages must end with a user turn")
        if max_new_tokens <= 0:
            raise ValueError("max new tokens must be positive")
        if temperature <= 0:
            raise ValueError("temperature must be positive")
        if top_k < 0:
            raise ValueError("top k cannot be negative")
        messages = fit_messages_to_context(messages, max_new_tokens)
        self.ensure_started()
        body = {
            "messages": [{"role": role, "content": normalize_text(text)} for role, text in messages],
            "max_tokens": max_new_tokens,
            "temperature": temperature,
            "top_k": top_k,
            "repeat_penalty": _DEFAULT_REPEAT_PENALTY,
            "repeat_last_n": _DEFAULT_REPEAT_LAST_N,
            "stream": on_delta is not None,
        }
        if on_delta is None:
            response = self._request("/v1/chat/completions", body, timeout=_REQUEST_TIMEOUT_SECONDS)
            choices = response.get("choices")
            content = None
            if isinstance(choices, list):
                for choice in choices:
                    if isinstance(choice, dict):
                        content = choice.get("message", {}).get("content")
                        if content is not None:
                            break
        else:
            content = self._request_stream("/v1/chat/completions", body, on_delta, timeout=_REQUEST_TIMEOUT_SECONDS)
        if not isinstance(content, str) or not content.strip():
            raise RuntimeError("llama.cpp returned an empty chat completion")
        return normalize_text(content)

    def _request_headers(self, payload: dict[str, Any] | None) -> dict[str, str]:
        headers: dict[str, str] = {}
        if payload is not None:
            headers["Content-Type"] = "application/json"
        headers["Authorization"] = f"Bearer {self.api_key}"
        return headers

    def _request_stream(
        self,
        endpoint: str,
        payload: dict[str, Any],
        on_delta: Callable[[str], None],
        *,
        timeout: float,
    ) -> str:
        """POST a streaming chat completion and emit incremental text deltas."""
        url = f"{self.base_url}{endpoint}"
        data = json.dumps(payload).encode("utf-8")
        request = Request(
            url,
            data=data,
            headers=self._request_headers(payload),
            method="POST",
        )
        accumulated = ""
        try:
            with urlopen(request, timeout=timeout) as response:
                for raw_line in response:
                    line = raw_line.decode("utf-8").strip()
                    if not line.startswith("data:"):
                        continue
                    chunk = line.removeprefix("data:").strip()
                    if chunk == "[DONE]":
                        break
                    try:
                        decoded = json.loads(chunk)
                    except json.JSONDecodeError:
                        continue
                    if not isinstance(decoded, dict):
                        continue
                    if isinstance(decoded.get("error"), (str, dict)):
                        raise RuntimeError(f"llama.cpp stream error: {decoded.get('error')}")
                    choices = decoded.get("choices")
                    if not isinstance(choices, list) or not choices or not isinstance(choices[0], dict):
                        continue
                    delta = choices[0].get("delta")
                    if not isinstance(delta, dict):
                        continue
                    content = delta.get("content")
                    if not isinstance(content, str) or not content:
                        continue
                    accumulated += content
                    on_delta(content)
        except HTTPError as error:
            detail = error.read(_MAX_ERROR_BODY_BYTES).decode("utf-8", errors="replace")
            raise RuntimeError(f"llama.cpp HTTP {error.code}: {detail}") from error
        except URLError as error:
            raise RuntimeError(f"llama.cpp is not reachable at {url}: {error.reason}") from error
        return accumulated

    def _request(self, endpoint: str, payload: dict[str, Any] | None, *, timeout: float) -> dict[str, Any]:
        return self._request_impl(endpoint, payload, timeout)

    def _request_json(self, endpoint: str, payload: dict[str, Any] | None, timeout: float) -> dict[str, Any]:
        url = f"{self.base_url}{endpoint}"
        data = json.dumps(payload).encode("utf-8") if payload is not None else None
        request = Request(
            url,
            data=data,
            headers=self._request_headers(payload),
            method="POST" if data is not None else "GET",
        )
        try:
            with urlopen(request, timeout=timeout) as response:
                body = response.read().decode("utf-8")
        except HTTPError as error:
            detail = error.read(_MAX_ERROR_BODY_BYTES).decode("utf-8", errors="replace")
            raise RuntimeError(f"llama.cpp HTTP {error.code}: {detail}") from error
        except URLError as error:
            raise RuntimeError(f"llama.cpp is not reachable at {url}: {error.reason}") from error
        try:
            decoded = json.loads(body)
        except json.JSONDecodeError as error:
            raise RuntimeError("llama.cpp returned invalid JSON") from error
        if not isinstance(decoded, dict):
            raise RuntimeError("llama.cpp returned an invalid JSON object")
        return decoded
