import subprocess
from pathlib import Path

from ru_runtime.vts.overlay import (
    OVERLAY_SENDER,
    overlay_error,
    overlay_process_ids,
    start_overlay,
    stop_overlay,
)


class _FakeRun:
    def __init__(self, stdout: str = ""):
        self._stdout = stdout

    def __call__(self, *args, **kwargs):
        return _FakeResult(self._stdout)


class _FakeResult:
    def __init__(self, stdout: str = ""):
        self.stdout = stdout
        self.returncode = 0


def test_overlay_process_ids_parses_multiple_rows():
    rows = (
        '"RuDesktopOverlay.exe","1234","Console","1","12,345 K"\n'
        '"RuDesktopOverlay.exe","5678","Console","1","9,999 K"\n'
    )
    assert overlay_process_ids(run=_FakeRun(rows)) == (1234, 5678)


def test_overlay_process_ids_no_process():
    assert overlay_process_ids(run=_FakeRun("")) == ()


def test_start_overlay_spawns_with_sender(tmp_path, monkeypatch):
    exe = tmp_path / "RuDesktopOverlay.exe"
    exe.write_bytes(b"MZ")
    monkeypatch.setattr("ru_runtime.vts.overlay.OVERLAY_EXE_PATH", exe)
    calls = {}

    class FakePopen:
        def __init__(self, args, cwd=None, creationflags=0):
            calls["args"] = args
            calls["cwd"] = cwd
            calls["creationflags"] = creationflags

    monkeypatch.setattr("ru_runtime.vts.overlay.subprocess.Popen", FakePopen)
    assert start_overlay() is True
    assert str(calls["args"][0]).endswith("RuDesktopOverlay.exe")
    assert calls["args"][1:] == ["--sender", OVERLAY_SENDER]
    assert calls["creationflags"] != 0
    assert overlay_error() is None


def test_start_overlay_missing_exe_returns_false(tmp_path, monkeypatch):
    monkeypatch.setattr("ru_runtime.vts.overlay.OVERLAY_EXE_PATH", tmp_path / "missing.exe")
    assert start_overlay() is False
    assert overlay_error() is not None


def test_stop_overlay_no_process_returns_false(monkeypatch):
    monkeypatch.setattr("ru_runtime.vts.overlay.overlay_process_ids", lambda: ())
    assert stop_overlay() is False


def test_stop_overlay_kills_pids(monkeypatch):
    calls = []

    def fake_overlay_process_ids():
        calls.append("ids")
        if len(calls) == 1:
            return (1234, 5678)
        return ()

    monkeypatch.setattr("ru_runtime.vts.overlay.overlay_process_ids", fake_overlay_process_ids)
    killed = []

    def fake_run(args, capture_output=False, text=False):
        killed.append(" ".join(args))
        return _FakeResult()

    monkeypatch.setattr("ru_runtime.vts.overlay.subprocess.run", fake_run)
    assert stop_overlay() is True
    assert any("1234" in cmd for cmd in killed)
    assert any("5678" in cmd for cmd in killed)
    assert overlay_error() is None
