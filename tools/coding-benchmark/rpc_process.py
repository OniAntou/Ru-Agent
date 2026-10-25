import json
import os
import queue
import subprocess
import threading
import time
from contextlib import suppress

from execution import close_process_tree, start_process, stop_process

MAX_RPC_LINE_BYTES = 1024 * 1024
MAX_EVENTS = 1000
MAX_QUEUED_RESPONSES = 16
MAX_SAFE_SUMMARY_CHARS = 500
TOKEN_FIELDS = ("input_tokens", "output_tokens", "total_tokens")
RPC_TIMEOUT_SECONDS = 10
CANCEL_TIMEOUT_SECONDS = 5


class RpcTrialError(RuntimeError):
    def __init__(self, code, ru_code=None):
        super().__init__(code)
        self.code = code
        self.ru_code = ru_code


class JsonRpcProcess:
    def __init__(self, command, cwd, *, environment=None):
        self.process = start_process(
            command, cwd=str(cwd), stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, bufsize=0, env=None if environment is None else os.environ | environment,
        )
        self._responses = queue.Queue(maxsize=MAX_QUEUED_RESPONSES)
        self._lock = threading.Lock()
        self._capture_lock = threading.Lock()
        self._request_id = 0
        self._events = []
        self._event_count = 0
        self._last_sequence_seen = None
        self._events_truncated = False
        self._token_usage = dict.fromkeys(TOKEN_FIELDS)
        self._reader_error = None
        self._closed = False
        self._stdout_thread = threading.Thread(target=self._read_stdout, daemon=True)
        self._stderr_thread = threading.Thread(target=self._drain_stderr, daemon=True)
        self._stdout_thread.start()
        self._stderr_thread.start()

    def request(self, method, params=None, timeout=RPC_TIMEOUT_SECONDS):
        with self._lock:
            self._request_id += 1
            request_id = self._request_id
            request = {"jsonrpc": "2.0", "id": request_id, "method": method}
            if params is not None:
                request["params"] = params
            try:
                self.process.stdin.write((json.dumps(request, separators=(",", ":")) + "\n").encode("utf-8"))
                self.process.stdin.flush()
            except (BrokenPipeError, OSError) as error:
                raise RpcTrialError("bridge_stdin_closed") from error

            deadline = time.monotonic() + timeout
            while True:
                if self._reader_error is not None:
                    raise self._reader_error
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError("rpc_request_timed_out")
                try:
                    message = self._responses.get(timeout=remaining)
                except queue.Empty as error:
                    raise TimeoutError("rpc_request_timed_out") from error
                if message is _END_OF_STREAM:
                    raise RpcTrialError("bridge_stdout_closed")
                if message.get("id") != request_id:
                    raise RpcTrialError("rpc_response_id_mismatch")
                if "error" in message:
                    error = message["error"]
                    data = error.get("data") if isinstance(error, dict) else None
                    ru_code = data.get("ruCode") if isinstance(data, dict) else None
                    raise RpcTrialError("rpc_call_failed", ru_code if isinstance(ru_code, str) else None)
                result = message.get("result")
                if not isinstance(result, dict):
                    raise RpcTrialError("rpc_result_invalid")
                return result

    def drain_notifications(self):
        with self._capture_lock:
            events, self._events = self._events, []
            return {
                "events": events,
                "event_count": self._event_count,
                "last_sequence_seen": self._last_sequence_seen,
                "events_truncated": self._events_truncated,
                "token_usage": dict(self._token_usage),
            }

    def _read_stdout(self):
        try:
            while True:
                line = self.process.stdout.readline(MAX_RPC_LINE_BYTES + 3)
                if not line:
                    self._responses.put(_END_OF_STREAM)
                    return
                payload = line.rstrip(b"\r\n")
                if len(payload) > MAX_RPC_LINE_BYTES:
                    raise RpcTrialError("rpc_frame_too_large")
                try:
                    message = json.loads(payload.decode("utf-8"))
                except (UnicodeDecodeError, json.JSONDecodeError) as error:
                    raise RpcTrialError("rpc_frame_invalid") from error
                if not isinstance(message, dict):
                    raise RpcTrialError("rpc_frame_invalid")
                if isinstance(message.get("method"), str) and "id" not in message:
                    self._capture_notification(message)
                    continue
                self._responses.put(message)
        except BaseException as error:
            self._reader_error = error if isinstance(error, RpcTrialError) else RpcTrialError("rpc_reader_failed")
            with suppress(queue.Full):
                self._responses.put_nowait(_END_OF_STREAM)

    def _capture_notification(self, message):
        if message.get("method") != "run.event":
            return
        params = message.get("params")
        if not isinstance(params, dict):
            return
        event = safe_event(params)
        with self._capture_lock:
            self._event_count += 1
            sequence = params.get("sequence")
            if type(sequence) is int and 0 <= sequence <= 2**63 - 1 and (
                self._last_sequence_seen is None or sequence > self._last_sequence_seen
            ):
                self._last_sequence_seen = sequence
            for name in TOKEN_FIELDS:
                value = valid_token_count(params.get(name))
                if value is not None:
                    self._token_usage[name] = value
            if len(self._events) < MAX_EVENTS:
                self._events.append(event)
            else:
                self._events_truncated = True

    def _drain_stderr(self):
        while self.process.stderr.read(4096):
            pass

    def close(self):
        if self._closed:
            return
        self._closed = True
        with suppress(OSError):
            self.process.stdin.close()
        try:
            self.process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            stop_process(self.process)
        finally:
            close_process_tree(self.process)
        self._stdout_thread.join(timeout=1)
        self._stderr_thread.join(timeout=1)


def valid_token_count(value):
    if type(value) is int and 0 <= value <= 2**63 - 1:
        return value
    return None


def safe_event(params):
    event = {}
    for name in ("subscription_id",):
        value = params.get(name)
        if isinstance(value, str):
            event[name] = value[:64]
    sequence = params.get("sequence")
    if type(sequence) is int and 0 <= sequence <= 2**63 - 1:
        event["sequence"] = sequence
    for name in ("event_type", "status"):
        value = params.get(name)
        if isinstance(value, str):
            event[name] = value[:80]
    summary = params.get("safe_summary")
    if isinstance(summary, str):
        event["safe_summary"] = summary[:MAX_SAFE_SUMMARY_CHARS]
    for name in TOKEN_FIELDS:
        value = valid_token_count(params.get(name))
        if value is not None:
            event[name] = value
    return event


_END_OF_STREAM = object()
