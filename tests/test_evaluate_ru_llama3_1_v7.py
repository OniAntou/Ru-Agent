from __future__ import annotations

import importlib.util
from pathlib import Path


SCRIPT_PATH = Path(__file__).resolve().parents[1] / "scripts" / "evaluate_ru_llama3_1_v7.py"
SPEC = importlib.util.spec_from_file_location("evaluate_ru_llama3_1_v7", SCRIPT_PATH)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def test_v7_evaluator_defaults_to_v7_paths_and_64_tokens():
    args = MODULE.build_parser().parse_args([])

    assert args.adapter_dir.endswith("ru-llama3.1-8b-instruct-v7")
    assert args.output.endswith("ru-llama3.1-8b-instruct-v7\\conversation_focus_v7.json")
    assert args.max_new_tokens == 64
