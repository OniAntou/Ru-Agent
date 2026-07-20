from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.qwen_runtime import QWEN_MODEL_ID, verify_local_qwen_base  # noqa: E402


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Download and verify Ru's local Qwen base")
    parser.add_argument("--output-dir", default=str(PROJECT_ROOT / "models" / "qwen2.5-0.5b-instruct"))
    return parser


def _report(output_dir: Path) -> dict[str, object]:
    config = verify_local_qwen_base(output_dir)
    return {
        "model_id": QWEN_MODEL_ID,
        "local_path": str(output_dir.resolve()),
        "model_type": config["model_type"],
        "safetensors_bytes": sum(path.stat().st_size for path in output_dir.glob("*.safetensors")),
    }


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    output_dir = Path(args.output_dir)
    try:
        report = _report(output_dir)
    except (FileNotFoundError, ValueError):
        from huggingface_hub import snapshot_download

        snapshot_download(repo_id=QWEN_MODEL_ID, local_dir=str(output_dir))
        report = _report(output_dir)
    print(json.dumps(report, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
