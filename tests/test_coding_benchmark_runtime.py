import hashlib
import os
import sys
import time
from pathlib import Path

import pytest

TOOL_ROOT = Path(__file__).resolve().parents[1] / "tools" / "coding-benchmark"
sys.path.insert(0, str(TOOL_ROOT))

from runtime_identity import (  # noqa: E402
    REPOSITORY_HOST_RELATIVE,
    RuntimeIdentityError,
    describe_runtime,
)


def _write(path: Path, content: bytes) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(content)
    return path


def _age(path: Path, seconds_ago: float) -> None:
    timestamp = time.time() - seconds_ago
    os.utime(path, (timestamp, timestamp))


def test_flat_publish_records_cli_and_sibling_host(tmp_path):
    cli = _write(tmp_path / "publish" / "RuAgent.Cli.exe", b"cli")
    host = _write(tmp_path / "publish" / "RuBackend.exe", b"host")
    _age(cli, 20)
    _age(host, 10)

    runtime = describe_runtime(cli)

    assert runtime["host_layout"] == "flat"
    assert runtime["host"]["path"] == str(host.resolve())
    assert runtime["host"]["sha256"] == hashlib.sha256(b"host").hexdigest()
    assert runtime["host_stale"] is False


def test_repository_cli_resolves_the_published_host(tmp_path):
    cli = _write(
        tmp_path / "backend-dotnet" / "RuAgent.Cli" / "bin" / "Debug" / "net10.0-windows" / "RuAgent.Cli.dll",
        b"cli",
    )
    host = _write(tmp_path / REPOSITORY_HOST_RELATIVE, b"host")
    _age(cli, 10)
    _age(host, 5)

    runtime = describe_runtime(cli)

    assert runtime["host_layout"] == "repository"
    assert runtime["host"]["path"] == str(host.resolve())


def test_host_older_than_cli_is_reported_as_stale(tmp_path):
    cli = _write(tmp_path / "publish" / "RuAgent.Cli.exe", b"cli")
    host = _write(tmp_path / "publish" / "RuBackend.exe", b"host")
    _age(cli, 5)
    _age(host, 180)

    runtime = describe_runtime(cli)

    assert runtime["host_stale"] is True
    assert runtime["host_stale_seconds"] > 60


def test_missing_host_is_a_named_failure(tmp_path):
    cli = _write(tmp_path / "publish" / "RuAgent.Cli.exe", b"cli")

    with pytest.raises(RuntimeIdentityError, match="host_executable_missing"):
        describe_runtime(cli)


def test_missing_cli_is_a_named_failure(tmp_path):
    with pytest.raises(RuntimeIdentityError, match="ru_executable_missing"):
        describe_runtime(tmp_path / "RuAgent.Cli.exe")
