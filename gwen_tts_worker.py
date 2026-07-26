from __future__ import annotations

import argparse
import gc
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parent
MODEL_DIR = PROJECT_ROOT / "models" / "gwen-tts-0.6b"
REFERENCE_AUDIO = PROJECT_ROOT / "tyc_talk.wav"
REFERENCE_TEXT = PROJECT_ROOT / "reference_text.txt"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--text", required=True)
    parser.add_argument("--output", required=True)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    import soundfile as sf
    import torch
    from qwen_tts import Qwen3TTSModel

    if not torch.cuda.is_available():
        raise RuntimeError("CUDA is required for the local Gwen voice service.")
    if not MODEL_DIR.is_dir():
        raise FileNotFoundError(f"Gwen model directory not found: {MODEL_DIR}")
    if not REFERENCE_AUDIO.is_file() or not REFERENCE_TEXT.is_file():
        raise FileNotFoundError("Gwen reference audio or transcript is missing.")
    ref_text = REFERENCE_TEXT.read_text(encoding="utf-8").strip()
    if not ref_text:
        raise RuntimeError("Reference transcript is empty.")

    model = Qwen3TTSModel.from_pretrained(
        str(MODEL_DIR),
        device_map="cuda:0",
        dtype=torch.bfloat16,
        attn_implementation="sdpa",
    )
    try:
        wavs, sample_rate = model.generate_voice_clone(
            text=args.text,
            language="Vietnamese",
            ref_audio=str(REFERENCE_AUDIO),
            ref_text=ref_text,
            non_streaming_mode=True,
        )
        if not wavs:
            raise RuntimeError("Gwen returned no audio.")
        sf.write(args.output, wavs[0], sample_rate)
        return 0
    finally:
        del model
        gc.collect()
        torch.cuda.empty_cache()


if __name__ == "__main__":
    raise SystemExit(main())

