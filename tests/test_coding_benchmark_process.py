import os
import signal
import subprocess
import sys
import time
from pathlib import Path
from unittest.mock import patch

import pytest

TOOL_ROOT = Path(__file__).resolve().parents[1] / "tools" / "coding-benchmark"
sys.path.insert(0, str(TOOL_ROOT))
from execution import run_command, start_process  # noqa: E402
from process_tree import ProcessTree  # noqa: E402

CHILD_PROGRAM = "import pathlib,sys,time; pathlib.Path(sys.argv[1]).write_text(str(__import__('os').getpid())); time.sleep(60)"
CHILD_WITH_CLOSED_OUTPUT_PROGRAM = (
    "import os,pathlib,sys,time; pathlib.Path(sys.argv[1]).write_text(str(os.getpid())); "
    "os.close(1); os.close(2); time.sleep(60)"
)
LEADER_PROGRAM = "import subprocess,sys; subprocess.Popen([sys.executable, '-c', sys.argv[1], sys.argv[2]])"
LEADER_DETACHED_OUTPUT_PROGRAM = (
    "import pathlib,subprocess,sys,time; subprocess.Popen([sys.executable, '-c', sys.argv[1], sys.argv[2]], "
    "stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL); deadline=time.monotonic()+2; "
    "exec('while not pathlib.Path(sys.argv[2]).exists() and time.monotonic()<deadline: time.sleep(0.01)')"
)


def test_leader_exit_waits_for_inherited_pipes_then_kills_descendant(tmp_path):
    pid_file = tmp_path / "descendant.pid"
    result = run_command(
        [sys.executable, "-c", LEADER_PROGRAM, CHILD_PROGRAM, str(pid_file)],
        tmp_path,
        {},
        2,
    )
    child_pid = _read_pid(pid_file)
    try:
        assert result["status"] == "timed_out"
        assert not _is_process_running(child_pid)
    finally:
        _terminate_if_running(child_pid)


def test_normal_leader_exit_closes_tree_after_descendant_closes_pipes(tmp_path):
    pid_file = tmp_path / "descendant.pid"
    result = run_command(
        [sys.executable, "-c", LEADER_DETACHED_OUTPUT_PROGRAM, CHILD_PROGRAM, str(pid_file)],
        tmp_path,
        {},
        10,
    )
    child_pid = _read_pid(pid_file)
    try:
        assert result["status"] == "completed"
        assert not _is_process_running(child_pid)
    finally:
        _terminate_if_running(child_pid)


@pytest.mark.skipif(os.name != "nt", reason="Windows Job Object assignment contract")
def test_windows_job_assignment_failure_never_runs_the_suspended_child(tmp_path):
    marker = tmp_path / "child-ran"
    command = [
        sys.executable,
        "-c",
        "import pathlib,sys; pathlib.Path(sys.argv[1]).write_text('ran')",
        str(marker),
    ]

    with (
        patch.object(ProcessTree, "assign", side_effect=OSError("job assignment failed")),
        pytest.raises(OSError, match="job assignment failed"),
    ):
        start_process(command, cwd=tmp_path, env=os.environ.copy())

    assert not marker.exists()


def _read_pid(path):
    deadline = time.monotonic() + 2
    while time.monotonic() < deadline:
        if path.exists():
            return int(path.read_text(encoding="utf-8"))
        time.sleep(0.01)
    pytest.fail("descendant did not publish its process id")


def _is_process_running(pid):
    if os.name == "nt":
        result = subprocess.run(
            ["tasklist", "/FI", f"PID eq {pid}", "/NH"],
            capture_output=True,
            text=True,
            check=False,
        )
        return any(len(fields := line.split()) > 1 and fields[1] == str(pid)
                   for line in result.stdout.splitlines())

    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    return True


def _terminate_if_running(pid):
    if not _is_process_running(pid):
        return
    if os.name == "nt":
        subprocess.run(
            ["taskkill", "/PID", str(pid), "/T", "/F"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            timeout=5,
            check=False,
        )
    else:
        os.kill(pid, signal.SIGKILL)
