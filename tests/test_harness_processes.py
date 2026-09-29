from __future__ import annotations

import io
import os

import pytest
from harness import processes as processes_module
from harness.processes import (
    MAX_LOG_BYTES,
    ChildLifecycle,
    OwnedProcess,
    stop_owned_child,
)


class FakeChild:
    def __init__(self, exit_code: int | None = None) -> None:
        self.exit_code = exit_code

    def poll(self) -> int | None:
        return self.exit_code

    def terminate(self) -> None:
        pass

    def kill_tree(self) -> None:
        pass

    def wait(self, timeout: float | None = None) -> int | None:
        return self.exit_code

    def exit(self, code: int = 1) -> None:
        self.exit_code = code


def test_ready_child_exiting_before_stop_is_unexpected() -> None:
    child = FakeChild()
    state = ChildLifecycle(role="edge", process=child)
    state.mark_ready()
    child.exit()

    assert state.poll().classification == "unexpected_exit"


def test_ready_child_exit_after_harness_stop_is_expected_stop() -> None:
    child = FakeChild()
    state = ChildLifecycle(role="worker", process=child)
    state.mark_ready()
    state.request_stop()
    child.exit()

    result = state.poll()
    assert result.classification == "expected_stop"
    assert result.code == 1


def test_child_that_exited_before_cleanup_remains_unexpected() -> None:
    child = FakeChild()
    state = ChildLifecycle(role="edge", process=child)
    state.mark_ready()
    child.exit()

    result = stop_owned_child(state)

    assert result.classification == "unexpected_exit"


class FakePopen:
    def __init__(self, payload: bytes = b"", pid: int = 4242) -> None:
        self.stdout: io.BytesIO | None = io.BytesIO(payload)
        self.pid = pid
        self.killed = False
        self.terminated = False

    def poll(self) -> int | None:
        return None

    def terminate(self) -> None:
        self.terminated = True

    def kill(self) -> None:
        self.killed = True

    def wait(self, timeout: float | None = None) -> int:
        return 0


def test_owned_process_bounds_drained_output_and_redacts_diagnostics(
    tmp_path,
) -> None:
    payload = b"secret-token " + b"x" * (MAX_LOG_BYTES + 4096)
    owned = OwnedProcess(
        FakePopen(payload), tmp_path / "logs" / "child.log", secrets=("secret-token",)
    )
    owned.wait()

    text = owned.diagnostic_text()
    assert "secret-token" not in text
    assert "<redacted>" in text
    assert len(text) <= MAX_LOG_BYTES
    assert (tmp_path / "logs" / "child.log").stat().st_size == MAX_LOG_BYTES


def test_drain_output_without_a_stream_is_a_noop(tmp_path) -> None:
    process = FakePopen()
    process.stdout = None
    owned = OwnedProcess(process, tmp_path / "child.log")
    owned.wait()

    assert owned.diagnostic_text() == ""
    assert not (tmp_path / "child.log").exists()


@pytest.mark.skipif(os.name != "nt", reason="taskkill is the Windows kill path")
def test_kill_tree_uses_taskkill_without_spawning(monkeypatch, tmp_path) -> None:
    calls: list[list[str]] = []

    def fake_run(args, **kwargs):
        del kwargs
        calls.append(args)
        return None

    monkeypatch.setattr(processes_module.subprocess, "run", fake_run)
    owned = OwnedProcess(FakePopen(pid=1234), tmp_path / "child.log")

    owned.kill_tree()

    assert calls == [["taskkill", "/PID", "1234", "/T", "/F"]]


@pytest.mark.skipif(os.name == "nt", reason="posix process-group kill path")
def test_kill_tree_kills_the_process_group_on_posix(monkeypatch, tmp_path) -> None:
    import signal

    killed: list[tuple[int, int]] = []
    monkeypatch.setattr(
        processes_module.os, "killpg", lambda pid, sig: killed.append((pid, sig)), raising=False
    )
    owned = OwnedProcess(FakePopen(pid=4321), tmp_path / "child.log")

    owned.kill_tree()

    assert killed == [(4321, signal.SIGKILL)]
