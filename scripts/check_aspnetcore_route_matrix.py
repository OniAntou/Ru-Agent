from __future__ import annotations

import argparse
import ast
import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any


@dataclass(frozen=True)
class DiscoveredRoute:
    method: str
    path: str
    transport: str


def _literal_string(node: ast.AST | None, *, default: str = "") -> str:
    if isinstance(node, ast.Constant) and isinstance(node.value, str):
        return node.value
    return default


def _join_route(prefix: str, path: str) -> str:
    combined = f"{prefix.rstrip('/')}/{path.lstrip('/')}"
    return combined if combined != "//" else "/"


def _router_imports(server_tree: ast.Module, repo_root: Path) -> dict[str, Path]:
    result: dict[str, Path] = {}
    for node in server_tree.body:
        if not isinstance(node, ast.ImportFrom) or node.module is None:
            continue
        if not node.module.startswith("mini_gpt.routers."):
            continue
        module_name = node.module.rsplit(".", 1)[-1]
        router_path = repo_root / "src" / "mini_gpt" / "routers" / f"{module_name}.py"
        for alias in node.names:
            if alias.name == "router":
                result[alias.asname or alias.name] = router_path
    return result


def _included_router_prefixes(server_tree: ast.Module) -> list[tuple[str, str]]:
    prefixes: list[tuple[str, str]] = []
    for node in ast.walk(server_tree):
        if not isinstance(node, ast.Call) or not isinstance(node.func, ast.Attribute):
            continue
        if node.func.attr != "include_router" or not node.args or not isinstance(node.args[0], ast.Name):
            continue
        prefix = ""
        for keyword in node.keywords:
            if keyword.arg == "prefix":
                prefix = _literal_string(keyword.value)
        prefixes.append((node.args[0].id, prefix))
    return prefixes


def _router_routes(router_path: Path) -> list[tuple[str, str]]:
    tree = ast.parse(router_path.read_text(encoding="utf-8"), filename=str(router_path))
    routes: list[tuple[str, str]] = []
    for node in ast.walk(tree):
        if not isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
            continue
        for decorator in node.decorator_list:
            if not isinstance(decorator, ast.Call) or not isinstance(decorator.func, ast.Attribute):
                continue
            if not isinstance(decorator.func.value, ast.Name) or decorator.func.value.id != "router":
                continue
            route_path = _literal_string(decorator.args[0] if decorator.args else None)
            if not route_path:
                continue
            if decorator.func.attr == "websocket":
                routes.append(("WS", route_path))
            elif decorator.func.attr in {"get", "post", "put", "patch", "delete"}:
                routes.append((decorator.func.attr.upper(), route_path))
    return routes


def discover_fastapi_routes(repo_root: Path) -> tuple[DiscoveredRoute, ...]:
    server_path = repo_root / "src" / "mini_gpt" / "server.py"
    server_tree = ast.parse(server_path.read_text(encoding="utf-8"), filename=str(server_path))
    imports = _router_imports(server_tree, repo_root)
    discovered: list[DiscoveredRoute] = []
    for router_name, prefix in _included_router_prefixes(server_tree):
        router_path = imports.get(router_name)
        if router_path is None:
            raise ValueError(f"Cannot resolve included router {router_name!r}")
        for method, path in _router_routes(router_path):
            transport = "websocket" if method == "WS" else "http"
            discovered.append(DiscoveredRoute(method, _join_route(prefix, path), transport))
    return tuple(sorted(set(discovered), key=lambda route: (route.transport, route.path, route.method)))


def load_route_matrix(repo_root: Path) -> list[dict[str, Any]]:
    matrix_path = repo_root / "backend-dotnet" / "compatibility-route-matrix.json"
    data = json.loads(matrix_path.read_text(encoding="utf-8"))
    if not isinstance(data, list):
        raise ValueError("route matrix must be a JSON list")
    return data


def validate_route_matrix(repo_root: Path) -> tuple[int, int]:
    matrix = load_route_matrix(repo_root)
    expected = {
        (entry["method"], entry["path"], entry["transport"])
        for entry in matrix
    }
    actual = {
        (route.method, route.path, route.transport)
        for route in discover_fastapi_routes(repo_root)
    }
    missing = sorted(actual - expected)
    extra = sorted(expected - actual)
    if missing or extra:
        details = []
        if missing:
            details.append(f"missing: {missing}")
        if extra:
            details.append(f"extra: {extra}")
        raise ValueError("; ".join(details))
    for entry in matrix:
        for field in ("source", "classification", "winui_callers"):
            if field not in entry:
                raise ValueError(f"route entry is missing {field!r}: {entry!r}")
    http_count = sum(transport == "http" for _, _, transport in expected)
    websocket_count = sum(transport == "websocket" for _, _, transport in expected)
    return http_count, websocket_count


def main() -> int:
    parser = argparse.ArgumentParser(description="Validate the ASP.NET Core compatibility route matrix")
    parser.add_argument("--repo-root", type=Path, default=Path(__file__).resolve().parents[1])
    args = parser.parse_args()
    try:
        http_count, websocket_count = validate_route_matrix(args.repo_root.resolve())
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"Route matrix invalid: {error}")
        return 1
    print(f"Route matrix OK: {http_count} HTTP routes, {websocket_count} WebSocket routes")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
