from __future__ import annotations

import hashlib
import json
from pathlib import Path

from scripts.check_python_runtime_boundary import (
    _summary,
    interpreter_files_in_product,
    main,
    scan_runtime_boundary,
)


def test_inventory_identifies_known_entrypoints_without_claiming_reachability(tmp_path: Path) -> None:
    files = {
        "backend-dotnet/RuBackend/Program.cs": 'Launch("ru_runtime.worker.server");',
        "backend-dotnet/RuBackend/Business/BusinessEndpoints.cs": "// bounded public endpoint",
        "scripts/probe.py": "pass",
    }
    for relative, text in files.items():
        path = tmp_path / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
    records = scan_runtime_boundary(tmp_path)
    payloads = {item.path: item.to_payload() for item in records}
    assert set(payloads) == set(files)
    assert payloads["backend-dotnet/RuBackend/Program.cs"].get("is_known_managed_startup") is True
    assert payloads["backend-dotnet/RuBackend/Business/BusinessEndpoints.cs"].get("is_known_managed_startup") is False
    assert payloads["backend-dotnet/RuBackend/Business/BusinessEndpoints.cs"]["classification"] == "legacy"
    assert payloads["scripts/probe.py"]["classification"] == "tooling"
    assert all("reachable_from_managed_startup" not in item for item in payloads.values())
    summary = _summary(records)
    assert summary["schema_version"] == 2
    assert summary["known_managed_startup_count"] == 1
    assert summary["truncated"] is False
    assert "reachable_count" not in summary


def test_a_scan_that_hits_the_file_cap_is_reported_and_fails(
    tmp_path: Path, monkeypatch, capsys
) -> None:
    scripts = tmp_path / "scripts"
    scripts.mkdir()
    for name in ("a.py", "b.py", "c.py"):
        (scripts / name).write_text("pass", encoding="utf-8")
    monkeypatch.setattr("scripts.check_python_runtime_boundary._MAX_FILES", 2)

    exit_code = main(["--root", str(tmp_path)])
    output = capsys.readouterr().out.splitlines()
    summary = json.loads(output[0])

    assert exit_code == 1
    assert summary["truncated"] is True
    assert summary["file_count"] == 2
    assert json.loads(output[1])["truncated"] is True


def test_inventory_skips_generated_files_and_bounds_hash_input(tmp_path: Path) -> None:
    for relative in (
        "scripts/large.py", "scripts/__pycache__/ignored.py",
        "backend-dotnet/Project/bin/ignored.cs", "backend-dotnet/Project/obj/ignored.cs",
        "backend-dotnet/Project/audio.wav",
    ):
        path = tmp_path / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(b"x" * 70_000)
    records = scan_runtime_boundary(tmp_path)
    assert len(records) == 1
    assert records[0].path == "scripts/large.py"
    assert records[0].size_bytes == 70_000
    assert records[0].sha256 == hashlib.sha256(b"x" * 65_536).hexdigest()


def test_the_product_tree_ships_no_interpreter(tmp_path: Path) -> None:
    """The rule this inventory exists to enforce: an interpreter is repository tooling.

    `scripts`, `tools`, and `tests` may hold Python. A `.py` (or Node, or any other
    interpreter file) under a product root is the regression this fails on.
    """
    files = [
        "backend-dotnet/RuBackend/Program.cs",
        "gui-winui3/Views/ChatView.xaml.cs",
        "scripts/check_something.py",
        "tools/probe.py",
        "tests/test_thing.py",
    ]
    for relative in files:
        path = tmp_path / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("pass", encoding="utf-8")

    records = scan_runtime_boundary(tmp_path)

    assert interpreter_files_in_product(records) == []
    assert _summary(records)["interpreter_files_in_product_count"] == 0


def test_a_python_file_inside_a_product_root_is_reported(tmp_path: Path) -> None:
    for relative in (
        "backend-dotnet/RuBackend/helper.py",
        "gui-winui3/Services/bridge.js",
        "scripts/tooling.py",
    ):
        path = tmp_path / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("pass", encoding="utf-8")

    records = scan_runtime_boundary(tmp_path)

    assert interpreter_files_in_product(records) == [
        "backend-dotnet/RuBackend/helper.py",
        "gui-winui3/Services/bridge.js",
    ]


def test_the_vendored_browser_assets_are_the_only_product_tree_exception(tmp_path: Path) -> None:
    """The GUI's xterm.js assets are parsed by WebView2, not shipped as a runtime.

    The allowlist is exact paths: a different name beside them (or a renamed copy
    elsewhere) still fails, so the exception cannot grow silently.
    """
    files = [
        "gui-winui3/Assets/terminal/xterm.js",
        "gui-winui3/Assets/terminal/addon-fit.js",
        "gui-winui3/Assets/terminal/sneaky.js",
    ]
    for relative in files:
        path = tmp_path / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("// pass", encoding="utf-8")

    records = scan_runtime_boundary(tmp_path)

    assert interpreter_files_in_product(records) == ["gui-winui3/Assets/terminal/sneaky.js"]
