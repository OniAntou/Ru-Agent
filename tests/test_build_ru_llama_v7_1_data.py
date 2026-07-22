from __future__ import annotations

import importlib.util
import json
from pathlib import Path


SCRIPT_PATH = Path(__file__).resolve().parents[1] / "scripts" / "build_ru_llama_v7_1_data.py"
SPEC = importlib.util.spec_from_file_location("build_ru_llama_v7_1_data", SCRIPT_PATH)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def test_v7_1_builder_parser_defaults_to_isolated_output():
    args = MODULE.build_parser().parse_args([])

    assert args.output_dir.endswith("data\\conversations\\ru_v7_1")
    assert args.seed == 42
    assert args.audit is False


def test_v7_1_builder_audit_prints_combined_report(capsys):
    assert MODULE.main(["--audit"]) == 0
    payload = json.loads(capsys.readouterr().out)

    assert payload["report"]["total_examples"] == 300
    assert payload["report"]["split_counts"] == {"train": 228, "validation": 36, "held_out": 36}
    assert len(payload["held_out"]) == 36
