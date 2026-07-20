from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.qwen_runtime import require_qwen_dependencies  # noqa: E402


def build_parser() -> argparse.ArgumentParser:
    return argparse.ArgumentParser(description="Check the local Qwen CUDA runtime without downloading a model")


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    parser.parse_args(argv)
    require_qwen_dependencies()

    import torch
    from transformers import BitsAndBytesConfig

    if not torch.cuda.is_available():
        raise RuntimeError("Qwen QLoRA requires an available CUDA GPU")
    device_index = torch.cuda.current_device()
    properties = torch.cuda.get_device_properties(device_index)
    quantization = BitsAndBytesConfig(
        load_in_4bit=True,
        bnb_4bit_quant_type="nf4",
        bnb_4bit_use_double_quant=True,
        bnb_4bit_compute_dtype=torch.float16,
    )
    print(
        json.dumps(
            {
                "cuda": True,
                "device": properties.name,
                "total_vram_mb": round(properties.total_memory / 1024**2, 1),
                "quantization": quantization.to_dict(),
            },
            indent=2,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
