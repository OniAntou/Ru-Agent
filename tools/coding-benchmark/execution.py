import os
import signal
import subprocess
import threading
import time
from contextlib import suppress

from process_tree import PROCESS_CREATE_SUSPENDED, ProcessTree, resume_suspended_process

OUTPUT_BYTES = 65536
PROCESS_TREE_ATTRIBUTE = "_ru_benchmark_process_tree"


def start_process(command, **options):
    tree = ProcessTree.create()
    process = None
    try:
        if os.name == "nt":
            creationflags = options.pop("creationflags", 0) | PROCESS_CREATE_SUSPENDED
            creationflags |= subprocess.CREATE_NEW_PROCESS_GROUP
            process = subprocess.Popen(command, creationflags=creationflags, **options)
            tree.assign(process)
            resume_suspended_process(process._handle)
        else:
            options.pop("start_new_session", None)
            process = subprocess.Popen(command, start_new_session=True, **options)
            tree.assign(process)
        setattr(process, PROCESS_TREE_ATTRIBUTE, tree)
        return process
    except BaseException:
        cleanup_error = None
        try:
            if process is not None and process.poll() is None:
                process.kill()
                process.wait(timeout=10)
        except OSError as error:
            cleanup_error = error
        finally:
            try:
                tree.close()
            except OSError as error:
                cleanup_error = cleanup_error or error
        if cleanup_error is not None:
            raise OSError("process tree startup cleanup failed") from cleanup_error
        raise


def close_process_tree(process):
    tree = getattr(process, PROCESS_TREE_ATTRIBUTE, None)
    if tree is None:
        return
    delattr(process, PROCESS_TREE_ATTRIBUTE)
    tree.close()


def stop_process(process):
    if getattr(process, PROCESS_TREE_ATTRIBUTE, None) is not None:
        close_process_tree(process)
        if process.poll() is None:
            process.wait(timeout=10)
        return
    if process.poll() is None:
        if os.name == "nt":
            subprocess.run(
                ["taskkill", "/PID", str(process.pid), "/T", "/F"],
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=10, check=False,
            )
        else:
            with suppress(ProcessLookupError):
                os.killpg(process.pid, signal.SIGKILL)
        process.wait(timeout=10)


def run_command(command, cwd, extra_env, timeout):
    started = time.monotonic()
    deadline = started + timeout
    environment = os.environ.copy()
    environment.update(extra_env)
    try:
        process = start_process(
            command,
            cwd=cwd,
            env=environment,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
    except FileNotFoundError:
        return {"status": "unavailable", "exit_code": None, "stdout": "", "stderr": "toolchain_unavailable", "elapsed_seconds": 0}
    except OSError as error:
        return {"status": "process_start_failed", "exit_code": None, "stdout": "", "stderr": str(error)[:OUTPUT_BYTES], "elapsed_seconds": round(time.monotonic() - started, 3)}
    outputs = [bytearray(), bytearray()]
    exceeded = threading.Event()

    def drain(stream, buffer):
        while chunk := stream.read(4096):
            room = OUTPUT_BYTES - len(buffer)
            buffer.extend(chunk[:max(room, 0)])
            if len(chunk) > room:
                exceeded.set()
        stream.close()

    readers = [threading.Thread(target=drain, args=(stream, buffer), daemon=True)
               for stream, buffer in zip((process.stdout, process.stderr), outputs, strict=True)]
    for reader in readers:
        reader.start()
    status = "completed"
    cleanup_error = None
    try:
        while process.poll() is None or any(reader.is_alive() for reader in readers):
            if exceeded.is_set():
                status = "output_limit"
                break
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                status = "timed_out"
                break
            time.sleep(min(0.02, remaining))
        if status == "completed":
            close_process_tree(process)
        else:
            stop_process(process)
        for reader in readers:
            reader.join(timeout=2)
        if exceeded.is_set():
            status = "output_limit"
        if any(reader.is_alive() for reader in readers):
            status = "cleanup_failed"
    except OSError as error:
        status = "cleanup_failed"
        cleanup_error = str(error)[:OUTPUT_BYTES]
    finally:
        try:
            close_process_tree(process)
            if process.poll() is None:
                process.wait(timeout=10)
        except OSError as error:
            status = "cleanup_failed"
            cleanup_error = str(error)[:OUTPUT_BYTES]
    result = {
        "status": status, "exit_code": process.returncode,
        "stdout": outputs[0].decode("utf-8", errors="replace"),
        "stderr": outputs[1].decode("utf-8", errors="replace"),
        "elapsed_seconds": round(time.monotonic() - started, 3),
    }
    if cleanup_error is not None:
        result["cleanup_error"] = cleanup_error
    return result
