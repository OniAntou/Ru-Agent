from __future__ import annotations

import argparse
import json
import re
from pathlib import Path
from typing import Any

ALLOWED_CLASSIFICATIONS = {"aspnet-direct"}
LEGACY_OWNERSHIP_FIELDS = {
    "default_owner",
    "ownership",
    "ownership_config",
    "shadow_compare",
}

# The endpoint modules that own the public surface, scanned for what they actually
# register. Reading the registrations is what makes this a contract check rather than
# a self-consistency check of the JSON: the contract previously could not notice a
# route the app maps but never declared.
_ENDPOINT_SOURCE_DIR = ("backend-dotnet", "RuBackend")
_MAP_CALL = re.compile(
    r'\.Map(Get|Post|Put|Delete|Methods)\s*\(\s*"(?P<path>/[^"]*)"',
    re.IGNORECASE,
)
_MAP_METHODS = re.compile(r"new\[\]\s*\{\s*HttpMethods\.(?P<method>\w+)")
_BLOCK_COMMENT = re.compile(r"/\*.*?\*/", re.DOTALL)
# Wildcard patterns are deliberate non-contract surface: the CORS-style OPTIONS
# catch-all and anything else that matches every path. They are excluded from the
# comparison but still reported, so a new one is a visible decision.
_WILDCARD_MARKER = "**"


def _strip_comments(text: str) -> str:
    """Remove C# comments so a commented-out Map call cannot satisfy the scan."""
    without_blocks = _BLOCK_COMMENT.sub("", text)
    return "\n".join(
        line for line in without_blocks.splitlines() if not line.lstrip().startswith("//")
    )


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
    if (http_count, websocket_count) != (53, 0):
        raise ValueError(
            "expected 53 HTTP routes and no WebSocket route, "
            f"found {http_count} and {websocket_count}"
        )
    return http_count, websocket_count


def mapped_routes(repo_root: Path) -> tuple[set[tuple[str, str]], list[str]]:
    """Every route the endpoint modules register, read from the registrations.

    Returns the (METHOD, path) pairs plus the wildcard patterns that are
    deliberately outside the contract.
    """
    routes: set[tuple[str, str]] = set()
    wildcards: list[str] = []
    root = repo_root.joinpath(*_ENDPOINT_SOURCE_DIR)
    for source in sorted(root.rglob("*.cs")):
        if "bin" in source.parts or "obj" in source.parts:
            continue
        text = _strip_comments(source.read_text(encoding="utf-8"))
        for match in _MAP_CALL.finditer(text):
            verb = match.group(1).upper()
            path = match.group("path")
            if _WILDCARD_MARKER in path:
                wildcards.append(f"{verb} {path}")
                continue
            if verb == "METHODS":
                methods = _MAP_METHODS.search(text, match.end())
                if methods is None:
                    raise ValueError(
                        f"MapMethods without HttpMethods in {source.name}: {path!r}"
                    )
                routes.add((methods.group("method").upper(), path))
                continue
            routes.add((verb, path))
    return routes, wildcards


def validate_mapped_routes_are_declared(repo_root: Path) -> tuple[int, list[str]]:
    """Every mapped route must appear in the contract.

    This is the direction the contract check was missing: the contract could be
    complete while the app quietly mapped an extra endpoint, because the guard only
    ever confirmed that declared paths exist in the source.
    """
    contract = load_route_contract(repo_root)
    declared = {(entry["method"].upper(), entry["path"]) for entry in contract}
    routes, wildcards = mapped_routes(repo_root)

    undeclared = sorted(routes - declared)
    if undeclared:
        listed = ", ".join(f"{method} {path}" for method, path in undeclared)
        raise ValueError(
            f"mapped route(s) missing from public-route-contract.json: {listed}"
        )

    missing = sorted(declared - routes)
    if missing:
        listed = ", ".join(f"{method} {path}" for method, path in missing)
        raise ValueError(f"declared route(s) not mapped by any endpoint module: {listed}")

    return len(routes), wildcards


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
        mapped_count, wildcards = validate_mapped_routes_are_declared(
            args.repo_root.resolve()
        )
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"Public route contract invalid: {error}")
        return 1
    print(
        "Public route contract OK: "
        f"{http_count} HTTP + {websocket_count} WebSocket; "
        f"{mapped_count} mapped route(s) all declared"
    )
    if wildcards:
        print(
            "Outside the contract (wildcard, deliberate): "
            + ", ".join(sorted(set(wildcards)))
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
