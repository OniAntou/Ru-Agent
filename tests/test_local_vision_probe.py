from __future__ import annotations

import base64
import hashlib
import json
import re
import subprocess
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import pytest

from mini_gpt.local_vision_probe import (
    VisionProbeConfig,
    build_request,
    build_server_command,
    parse_response,
)
from scripts import benchmark_local_vision
from scripts.benchmark_local_vision import run_benchmark


ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "scripts" / "vision_model_manifest.json"
SETUP_SCRIPT = ROOT / "scripts" / "setup_local_vision.ps1"


def test_manifest_is_revision_size_and_hash_pinned() -> None:
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))

    assert manifest["repository"] == "Qwen/Qwen3-VL-2B-Instruct-GGUF"
    assert manifest["revision"] == "52d6c8ffea26cc873ac5ad116f8631268d7eb503"
    assert manifest["license"] == "apache-2.0"
    assert {item["size"] for item in manifest["files"]} == {1107409952, 445053216}
    assert all(re.fullmatch(r"[0-9a-f]{64}", item["sha256"]) for item in manifest["files"])


def test_setup_script_requires_explicit_download_confirmation() -> None:
    result = subprocess.run(
        ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(SETUP_SCRIPT)],
        cwd=ROOT,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )

    assert result.returncode != 0
    assert "Pass -ConfirmModelDownload" in result.stderr


def test_server_command_is_loopback_only_and_multimodal(tmp_path: Path) -> None:
    command = build_server_command(VisionProbeConfig(project_root=tmp_path, port=18111))

    assert command[0].endswith("llama-server.exe")
    assert command[command.index("--host") + 1] == "127.0.0.1"
    assert command[command.index("--port") + 1] == "18111"
    assert "--mmproj" in command
    assert command[command.index("--ctx-size") + 1] == "4096"
    assert command[command.index("--image-max-tokens") + 1] == "512"
    assert "--no-webui" in command


def test_request_uses_b10142_compatible_schema_and_keeps_supported_bounds() -> None:
    request = build_request(("first", "second"))
    content = request["messages"][0]["content"]
    schema = request["response_format"]["schema"]

    assert [item["image_url"]["url"] for item in content[1:]] == [
        "data:image/jpeg;base64,first",
        "data:image/jpeg;base64,second",
    ]
    assert request["response_format"]["type"] == "json_object"
    assert "json_schema" not in request["response_format"]
    assert schema["properties"]["scene"]["maxLength"] == 500
    assert schema["properties"]["event"] == {"type": "string"}
    assert schema["properties"]["visible_text"]["maxItems"] == 16
    assert schema["properties"]["visible_text"]["items"]["maxLength"] == 200
    for score in ("confidence", "novelty", "comment_worthiness"):
        assert schema["properties"][score] == {"type": "number"}


@pytest.mark.parametrize("images", [(), ("1", "2", "3", "4", "5")])
def test_request_rejects_batches_outside_one_to_four_images(images: tuple[str, ...]) -> None:
    with pytest.raises(ValueError, match="one to four"):
        build_request(images)


def test_response_parser_accepts_only_the_exact_bounded_observation_shape() -> None:
    observation = {
        "scene": "game",
        "event": "the player missed",
        "confidence": 0.8,
        "novelty": 0.7,
        "comment_worthiness": 0.9,
        "visible_text": ["DEFEAT"],
    }
    payload = {"choices": [{"message": {"content": json.dumps(observation)}}]}

    assert parse_response(payload) == observation
    payload["choices"][0]["message"]["content"] = json.dumps({**observation, "extra": True})
    with pytest.raises(ValueError, match="schema"):
        parse_response(payload)


@pytest.mark.parametrize(
    ("field", "value"),
    [
        ("scene", "x" * 501),
        ("event", "x" * 2001),
        ("confidence", -0.1),
        ("novelty", 1.1),
        ("visible_text", ["x"] * 17),
        ("visible_text", ["x" * 201]),
    ],
)
def test_response_parser_rejects_values_outside_schema(field: str, value: object) -> None:
    observation = {
        "scene": "game",
        "event": "the player missed",
        "confidence": 0.8,
        "novelty": 0.7,
        "comment_worthiness": 0.9,
        "visible_text": ["DEFEAT"],
    }
    observation[field] = value
    payload = {"choices": [{"message": {"content": json.dumps(observation)}}]}

    with pytest.raises(ValueError, match="schema"):
        parse_response(payload)


class _FakeChild:
    def __init__(self) -> None:
        self.terminated = False
        self.waited = False
        self.killed = False

    def poll(self) -> int | None:
        return 0 if self.terminated or self.killed else None

    def terminate(self) -> None:
        self.terminated = True

    def wait(self, timeout: float | None = None) -> int:
        del timeout
        self.waited = True
        return 0

    def kill(self) -> None:
        self.killed = True


def _start_fake_vision_server(*, malformed: bool = False):
    requests: list[dict[str, object]] = []
    observation = {
        "scene": "test scene",
        "event": "test event",
        "confidence": 0.9,
        "novelty": 0.8,
        "comment_worthiness": 0.7,
        "visible_text": [],
    }

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, _format: str, *_args: object) -> None:
            return

        def do_GET(self) -> None:  # noqa: N802
            assert self.path == "/health"
            encoded = b'{"status":"ok"}'
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(encoded)))
            self.end_headers()
            self.wfile.write(encoded)

        def do_POST(self) -> None:  # noqa: N802
            assert self.path == "/v1/chat/completions"
            length = int(self.headers["Content-Length"])
            requests.append(json.loads(self.rfile.read(length)))
            content = "not json" if malformed else json.dumps(observation)
            encoded = json.dumps({"choices": [{"message": {"content": content}}]}).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(encoded)))
            self.end_headers()
            self.wfile.write(encoded)

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    worker = threading.Thread(target=server.serve_forever, daemon=True)
    worker.start()
    return server, worker, requests


def test_benchmark_posts_only_in_memory_images_and_waits_for_child(tmp_path: Path) -> None:
    server, worker, requests = _start_fake_vision_server()
    child = _FakeChild()
    try:
        result = run_benchmark(
            VisionProbeConfig(project_root=tmp_path, port=server.server_port),
            runs=2,
            process_factory=lambda *_args, **_kwargs: child,
            gpu_probe=lambda: 321,
        )
    finally:
        server.shutdown()
        worker.join(timeout=2)
        server.server_close()

    assert len(requests) == 2
    urls = [
        item["image_url"]["url"]
        for item in requests[0]["messages"][0]["content"]
        if item["type"] == "image_url"
    ]
    assert len(urls) == 1
    assert urls[0].startswith("data:image/jpeg;base64,")
    assert child.terminated and child.waited and not child.killed
    assert result["gpu_before_mib"] == 321
    assert result["gpu_peak_mib"] == 321
    assert len(result["runs_ms"]) == 2


def test_benchmark_smoke_image_is_the_pinned_valid_jpeg() -> None:
    raw = base64.b64decode(benchmark_local_vision._SMOKE_JPEG_B64, validate=True)

    assert len(raw) == 693
    assert hashlib.sha256(raw).hexdigest() == "dd7504323dad364d3140dfe14667148d1618e1eb81792df357111b4de81f9cf2"


def test_benchmark_waits_for_child_when_response_is_invalid(tmp_path: Path) -> None:
    server, worker, _requests = _start_fake_vision_server(malformed=True)
    child = _FakeChild()
    try:
        with pytest.raises(ValueError, match="schema"):
            run_benchmark(
                VisionProbeConfig(project_root=tmp_path, port=server.server_port),
                runs=1,
                process_factory=lambda *_args, **_kwargs: child,
                gpu_probe=lambda: 0,
            )
    finally:
        server.shutdown()
        worker.join(timeout=2)
        server.server_close()

    assert child.terminated and child.waited
