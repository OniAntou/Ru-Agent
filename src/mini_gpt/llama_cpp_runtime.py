from __future__ import annotations

import json
import subprocess
import time
from collections.abc import Callable, Sequence
from pathlib import Path
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

from ._text_utils import normalize_text


DEFAULT_PORT = 8188
DEFAULT_START_TIMEOUT_SECONDS = 90.0


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
        self._request_impl = request or self._request_json
        self._popen = popen
        self._process: Any | None = None
        self._log_handle: Any | None = None

    @property
    def server_executable(self) -> Path:
        return self.project_root / "tools" / "llama.cpp-bin" / "llama-server.exe"

    @property
    def base_model(self) -> Path:
        return self.project_root / "models" / "gguf" / "llama3.1-8b-instruct-q4_k_m.gguf"

    @property
    def lora_adapter(self) -> Path:
        candidates = (
            self.project_root / "models" / "gguf" / "ru-llama3.1-8b-instruct-v6.2-lora.gguf",
            self.project_root / "models" / "gguf" / "ru-llama3.1-8b-instruct-v6.1-lora.gguf",
            self.project_root / "models" / "gguf" / "ru-llama3.1-8b-instruct-v6-lora.gguf",
        )
        return next((path for path in candidates if path.is_file() and path.stat().st_size), candidates[0])

    @property
    def log_path(self) -> Path:
        return self.project_root / "logs" / "gguf" / "llama-server.log"

    @property
    def base_url(self) -> str:
        return f"http://127.0.0.1:{self.port}"

    def server_command(self) -> tuple[str, ...]:
        return (
            str(self.server_executable),
            "--model",
            str(self.base_model),
            "--lora",
            str(self.lora_adapter),
            "--host",
            "127.0.0.1",
            "--port",
            str(self.port),
            "--ctx-size",
            "2048",
            "--n-gpu-layers",
            "all",
            "--parallel",
            "1",
        )

    def validate_assets(self) -> None:
        for path, label in (
            (self.server_executable, "llama-server"),
            (self.base_model, "base GGUF"),
            (self.lora_adapter, "Ru LoRA GGUF (v6.2, v6.1, or v6)"),
        ):
            if not path.is_file() or path.stat().st_size == 0:
                raise FileNotFoundError(f"{label} not found: {path}")

    def is_running(self) -> bool:
        return self._process is not None and self._process.poll() is None

    def is_cached(self) -> bool:
        return self.is_running()

    def ensure_started(self) -> None:
        if self.is_running():
            return
        self.stop()
        self.validate_assets()
        self.log_path.parent.mkdir(parents=True, exist_ok=True)
        self._log_handle = self.log_path.open("a", encoding="utf-8")
        creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        self._process = self._popen(
            self.server_command(),
            cwd=self.project_root,
            stdout=self._log_handle,
            stderr=subprocess.STDOUT,
            creationflags=creationflags,
        )
        deadline = time.monotonic() + self.start_timeout_seconds
        last_error = "server did not become healthy"
        while time.monotonic() < deadline:
            if self._process.poll() is not None:
                last_error = f"server exited with code {self._process.returncode}"
                break
            try:
                self._request("/health", None, timeout=2)
                return
            except RuntimeError as error:
                last_error = str(error)
                time.sleep(0.25)
        self.stop()
        raise RuntimeError(f"llama.cpp server failed to start: {last_error}. See {self.log_path}")

    def stop(self) -> None:
        process = self._process
        self._process = None
        if process is not None and process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=10)
        if self._log_handle is not None:
            self._log_handle.close()
            self._log_handle = None

    def generate(
        self,
        messages: Sequence[tuple[str, str]],
        max_new_tokens: int,
        temperature: float,
        top_k: int,
    ) -> str:
        if not messages or messages[-1][0] != "user":
            raise ValueError("messages must end with a user turn")
        if max_new_tokens <= 0:
            raise ValueError("max new tokens must be positive")
        if temperature <= 0:
            raise ValueError("temperature must be positive")
        if top_k < 0:
            raise ValueError("top k cannot be negative")
        self.ensure_started()
        body = {
            "messages": [{"role": role, "content": normalize_text(text)} for role, text in messages],
            "max_tokens": max_new_tokens,
            "temperature": temperature,
            "top_k": top_k,
            "stream": False,
        }
        response = self._request("/v1/chat/completions", body, timeout=120)
        choices = response.get("choices")
        content = choices[0].get("message", {}).get("content") if isinstance(choices, list) and choices else None
        if not isinstance(content, str) or not content.strip():
            raise RuntimeError("llama.cpp returned an empty chat completion")
        return normalize_text(content)

    def _request(self, endpoint: str, payload: dict[str, Any] | None, *, timeout: float) -> dict[str, Any]:
        return self._request_impl(endpoint, payload, timeout)

    def _request_json(self, endpoint: str, payload: dict[str, Any] | None, timeout: float) -> dict[str, Any]:
        url = f"{self.base_url}{endpoint}"
        data = json.dumps(payload).encode("utf-8") if payload is not None else None
        headers = {"Content-Type": "application/json"} if data is not None else {}
        request = Request(url, data=data, headers=headers, method="POST" if data is not None else "GET")
        try:
            with urlopen(request, timeout=timeout) as response:
                body = response.read().decode("utf-8")
        except HTTPError as error:
            detail = error.read().decode("utf-8", errors="replace")
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
