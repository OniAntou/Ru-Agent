"""Owned child-process lifecycle and bounded diagnostic capture."""

from __future__ import annotations

import os
import signal
import subprocess
import threading
import time
from collections.abc import Sequence
from dataclasses import dataclass, field
from pathlib import Path
from typing import Protocol

from .model import ChildExit, ChildExitClassification

MAX_LOG_BYTES = 64 * 1024


class ProcessLike(Protocol):
    def poll(self) -> int | None: ...

    def terminate(self) -> None: ...

    def kill_tree(self) -> None: ...

    def wait(self, timeout: float | None = None) -> int | None: ...


@dataclass(slots=True)
class ChildLifecycle:
    role: str
    process: ProcessLike
    ready: bool = False
    stop_requested: bool = False
    started_at: float = field(default_factory=time.monotonic)
    ready_at: float | None = None
    stop_requested_at: float | None = None
    exit_code: int | None = None
    _classification: ChildExitClassification = "running"

    def mark_ready(self) -> None:
        self.ready = True
        self.ready_at = time.monotonic()

    def request_stop(self) -> None:
        self.stop_requested = True
        self.stop_requested_at = time.monotonic()

    def poll(self) -> ChildExit:
        try:
            code = self.process.poll()
        except Exception:
            self._classification = "cleanup_failure"
            return ChildExit(self.role, None, self._classification)
        if code is None:
            self._classification = "running"
            return ChildExit(self.role, None, self._classification)
        self.exit_code = int(code)
        if self.stop_requested:
            self._classification = "expected_stop"
        else:
            self._classification = "unexpected_exit"
        return ChildExit(self.role, self.exit_code, self._classification)

    def result(self) -> ChildExit:
        return self.poll()


class OwnedProcess:
    """Subprocess wrapper that drains output while retaining at most 64 KiB."""

    def __init__(
        self,
        process: subprocess.Popen[bytes],
        log_path: Path,
        secrets: Sequence[str] = (),
    ) -> None:
        self._process = process
        self.log_path = log_path
        self._secrets = tuple(secret for secret in secrets if secret)
        self._retained = bytearray()
        self._log_lock = threading.Lock()
        self._reader = threading.Thread(target=self._drain_output, name="ru-harness-log", daemon=True)
        self._reader.start()

    def _drain_output(self) -> None:
        stream = self._process.stdout
        if stream is None:
            return
        self.log_path.parent.mkdir(parents=True, exist_ok=True)
        try:
            with self.log_path.open("wb") as log_file:
                while True:
                    chunk = stream.read(4096)
                    if not chunk:
                        break
                    with self._log_lock:
                        remaining = MAX_LOG_BYTES - len(self._retained)
                        if remaining > 0:
                            bounded = chunk[:remaining]
                            self._retained.extend(bounded)
                            log_file.write(bounded)
                            log_file.flush()
        except OSError:
            return

    def poll(self) -> int | None:
        return self._process.poll()

    def terminate(self) -> None:
        self._process.terminate()

    def kill_tree(self) -> None:
        if os.name == "nt":
            subprocess.run(
                ["taskkill", "/PID", str(self._process.pid), "/T", "/F"],
                check=False,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                timeout=5,
            )
            return
        try:
            os.killpg(self._process.pid, signal.SIGKILL)
        except (OSError, ProcessLookupError):
            self._process.kill()

    def wait(self, timeout: float | None = None) -> int | None:
        code = self._process.wait(timeout=timeout)
        self._reader.join(timeout=1.0)
        return code

    def diagnostic_text(self) -> str:
        with self._log_lock:
            text = bytes(self._retained).decode("utf-8", errors="replace")
        for secret in self._secrets:
            text = text.replace(secret, "<redacted>")
        return text


def start_owned_child(
    command: Sequence[str],
    *,
    repo_root: Path,
    environment: dict[str, str],
    role: str,
    log_path: Path,
    secrets: Sequence[str] = (),
) -> ChildLifecycle:
    """Start one child in its own process group and return its lifecycle state."""

    creationflags = getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0) if os.name == "nt" else 0
    process = subprocess.Popen(
        list(command),
        cwd=str(repo_root),
        env=environment,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        stdin=subprocess.DEVNULL,
        creationflags=creationflags,
        start_new_session=os.name != "nt",
    )
    owned = OwnedProcess(process, log_path, secrets)
    return ChildLifecycle(role=role, process=owned)


def stop_owned_child(
    child: ChildLifecycle,
    *,
    terminate_timeout_seconds: float = 3.0,
    kill_timeout_seconds: float = 3.0,
) -> ChildExit:
    """Request a bounded graceful stop, then kill only the owned process tree."""

    before_stop = child.poll()
    if before_stop.code is not None:
        return before_stop
    child.request_stop()
    try:
        if child.process.poll() is None:
            child.process.terminate()
        child.process.wait(timeout=terminate_timeout_seconds)
    except subprocess.TimeoutExpired:
        try:
            child.process.kill_tree()
            child.process.wait(timeout=kill_timeout_seconds)
        except Exception:
            child._classification = "cleanup_failure"
            return ChildExit(child.role, child.process.poll(), "cleanup_failure")
    except Exception:
        child._classification = "cleanup_failure"
        return ChildExit(child.role, child.process.poll(), "cleanup_failure")
    return child.poll()
