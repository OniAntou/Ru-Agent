from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

ALLOWED_CLASSIFICATIONS = {"aspnet-direct", "runtime-worker"}
LEGACY_OWNERSHIP_FIELDS = {
    "default_owner",
    "ownership",
    "ownership_config",
    "shadow_compare",
}


def load_route_contract(repo_root: Path) -> list[dict[str, Any]]:
    contract_path = repo_root / "backend-dotnet" / "public-route-contract.json"
    data = json.loads(contract_path.read_text(encoding="utf-8"))
    if not isinstance(data, list):
        raise ValueError("public route contract must be a JSON list")
    return data


def validate_route_contract(repo_root: Path) -> tuple[int, int]:
    entries = load_route_contract(repo_root)
    identities: set[tuple[str, str, str]] = set()
    for entry in entries:
        for field in (
            "method",
            "path",
            "transport",
            "source",
            "classification",
            "winui_callers",
        ):
            if field not in entry:
                raise ValueError(f"route entry is missing {field!r}: {entry!r}")

        method = entry["method"]
        path = entry["path"]
        transport = entry["transport"]
        identity = (method, path, transport)
        if identity in identities:
            raise ValueError(f"duplicate public route: {identity!r}")
        identities.add(identity)

        if not isinstance(path, str) or not path.startswith("/"):
            raise ValueError(f"invalid public route path: {path!r}")
        if transport == "http" and method not in {"GET", "POST", "PUT", "DELETE"}:
            raise ValueError(f"invalid HTTP method: {identity!r}")
        if transport == "websocket" and method != "WS":
            raise ValueError(f"invalid WebSocket method: {identity!r}")
        if transport not in {"http", "websocket"}:
            raise ValueError(f"invalid transport: {identity!r}")
        if entry["classification"] not in ALLOWED_CLASSIFICATIONS:
            raise ValueError(f"invalid final classification: {identity!r}")
        if not isinstance(entry["winui_callers"], list):
            raise ValueError(f"winui_callers must be a list: {identity!r}")
        if LEGACY_OWNERSHIP_FIELDS.intersection(entry):
            raise ValueError(f"legacy ownership metadata remains: {identity!r}")

        source = Path(entry["source"])
        if source.is_absolute() or source.parts[:2] != ("backend-dotnet", "RuBackend"):
            raise ValueError(f"public route is not owned by RuBackend: {identity!r}")
        source_path = repo_root / source
        if not source_path.is_file():
            raise ValueError(f"route source does not exist: {source.as_posix()}")
        if path not in source_path.read_text(encoding="utf-8"):
            raise ValueError(
                f"route path {path!r} is absent from {source.as_posix()}"
            )

    http_count = sum(transport == "http" for _, _, transport in identities)
    websocket_count = sum(
        transport == "websocket" for _, _, transport in identities
    )
    if (http_count, websocket_count) != (61, 1):
        raise ValueError(
            "expected 61 HTTP routes and 1 WebSocket route (/voice/stream), "
            f"found {http_count} and {websocket_count}"
        )
    return http_count, websocket_count


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Validate the final ASP.NET Core public route contract"
    )
    parser.add_argument(
        "--repo-root",
        type=Path,
        default=Path(__file__).resolve().parents[1],
    )
    args = parser.parse_args()
    try:
        http_count, websocket_count = validate_route_contract(
            args.repo_root.resolve()
        )
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"Public route contract invalid: {error}")
        return 1
    print(
        "Public route contract OK: "
        f"{http_count} HTTP + {websocket_count} WebSocket (/voice/stream)"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
