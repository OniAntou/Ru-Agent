"""Resolve and fingerprint the Ru runtime a live benchmark trial will start.

The benchmark does not start the Host itself: it starts the CLI, and the CLI
resolves the Host from its own layout. A flat distribution keeps
``RuBackend.exe`` beside the CLI; a repository build resolves the published Host
under ``backend-dotnet``. Recording both files, and refusing a Host older than
the CLI, prevents a trial from silently measuring a stale build.
"""

from __future__ import annotations

import hashlib
from datetime import UTC, datetime
from pathlib import Path

HOST_FILE_NAME = "RuBackend.exe"
REPOSITORY_HOST_RELATIVE = (
    Path("backend-dotnet")
    / "RuBackend"
    / "bin"
    / "Release"
    / "net10.0"
    / "win-x64"
    / "publish"
    / HOST_FILE_NAME
)

# Publish and flatten order can leave the Host a little older than the CLI without
# the two belonging to different builds. A genuinely stale publish is minutes or
# hours old; anything beyond this window is treated as stale.
STALE_TOLERANCE_SECONDS = 60.0


class RuntimeIdentityError(ValueError):
    """The CLI exists but the runtime it would start cannot be proven."""


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _describe_file(path: Path) -> dict:
    stat = path.stat()
    return {
        "path": str(path),
        "sha256": _sha256(path),
        "bytes": stat.st_size,
        "modified_utc": datetime.fromtimestamp(stat.st_mtime, UTC).isoformat(),
    }


def resolve_host(cli: Path) -> tuple[Path | None, str]:
    """Return the Host the CLI will resolve and its layout label."""
    sibling = cli.parent / HOST_FILE_NAME
    if sibling.is_file():
        return sibling, "flat"

    for parent in cli.parents:
        candidate = parent / REPOSITORY_HOST_RELATIVE
        if candidate.is_file():
            return candidate, "repository"

    return None, "missing"


def describe_runtime(executable: str | Path) -> dict:
    """Describe the CLI and the Host it resolves.

    Raises ``RuntimeIdentityError`` when the CLI is missing or no Host can be
    resolved. ``host_stale`` reports that the resolved Host is older than the
    CLI; the live runner refuses that unless stale runs are explicitly allowed.
    """
    cli = Path(executable).expanduser().resolve()
    if not cli.is_file():
        raise RuntimeIdentityError("ru_executable_missing: the CLI path is not a file")

    host, layout = resolve_host(cli)
    if host is None:
        raise RuntimeIdentityError(
            "host_executable_missing: RuBackend.exe was not found beside the CLI or under "
            "backend-dotnet/RuBackend/bin/Release/net10.0/win-x64/publish; use the flat "
            "build.bat output or publish the Host"
        )

    cli_file = _describe_file(cli)
    host_file = _describe_file(host)
    stale_seconds = cli.stat().st_mtime - host.stat().st_mtime
    return {
        "cli": cli_file,
        "host": host_file,
        "host_layout": layout,
        "host_stale": stale_seconds > STALE_TOLERANCE_SECONDS,
        "host_stale_seconds": round(max(0.0, stale_seconds), 3),
    }
