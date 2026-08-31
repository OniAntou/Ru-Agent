from __future__ import annotations

import hashlib
import json
from pathlib import Path

from scripts.check_python_runtime_boundary import _reference_cases, _summary, scan_runtime_boundary

ROOT = Path(__file__).resolve().parents[1]


def test_inventory_identifies_known_entrypoints_without_claiming_reachability(tmp_path: Path) -> None:
    files = {
        "backend-dotnet/RuBackend/Program.cs": 'Launch("ru_runtime.worker.server");',
        "src/ru_runtime/voice/omnivoice_server.py": 'raise RuntimeError("must not execute")',
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
    assert payloads["src/ru_runtime/voice/omnivoice_server.py"].get("is_known_managed_startup") is False
    assert payloads["src/ru_runtime/voice/omnivoice_server.py"]["classification"] == "provider-only"
    assert payloads["scripts/probe.py"]["classification"] == "tooling"
    assert all("reachable_from_managed_startup" not in item for item in payloads.values())
    summary = _summary(records)
    assert summary["schema_version"] == 2
    assert summary["known_managed_startup_count"] == 1
    assert "reachable_count" not in summary


def test_inventory_skips_generated_files_and_bounds_hash_input(tmp_path: Path) -> None:
    for relative in (
        "scripts/large.py", "scripts/__pycache__/ignored.py",
        "backend-dotnet/Project/bin/ignored.cs", "backend-dotnet/Project/obj/ignored.cs",
        "src/ru_runtime/voice/audio.wav",
    ):
        path = tmp_path / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(b"x" * 70_000)
    records = scan_runtime_boundary(tmp_path)
    assert len(records) == 1
    assert records[0].path == "scripts/large.py"
    assert records[0].size_bytes == 70_000
    assert records[0].sha256 == hashlib.sha256(b"x" * 65_536).hexdigest()


def test_reference_cases_match_the_checked_in_cross_language_fixture() -> None:
    fixture = json.loads((ROOT / "tests/fixtures/python-csharp-phase1-baseline.json").read_text(encoding="utf-8"))
    assert _reference_cases(ROOT) == fixture["cases"]
