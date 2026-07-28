from __future__ import annotations

import os
import subprocess
import sys
import tempfile
import gc
from pathlib import Path
from time import perf_counter
from typing import Callable

from flask import Flask, jsonify, request

PROJECT_ROOT = Path(__file__).resolve().parent
WORKER_SCRIPT = PROJECT_ROOT / "gwen_tts_worker.py"
MODEL_DIR = PROJECT_ROOT / "models" / "gwen-tts-0.6b"
REFERENCE_AUDIO = PROJECT_ROOT / "tyc_talk.wav"
REFERENCE_TEXT = PROJECT_ROOT / "reference_text.txt"

_model = None
_voice_clone_prompt = None

def get_model():
    global _model
    if _model is None:
        import torch
        from qwen_tts import Qwen3TTSModel
        if not torch.cuda.is_available():
            raise RuntimeError("CUDA is required for the local Gwen voice service.")
        _model = Qwen3TTSModel.from_pretrained(
            str(MODEL_DIR),
            device_map="cuda:0",
            dtype=torch.bfloat16,
            attn_implementation="sdpa",
        )
    return _model


def warm_model() -> dict[str, bool]:
    global _voice_clone_prompt
    try:
        model = get_model()
        if _voice_clone_prompt is None:
            _voice_clone_prompt = model.create_voice_clone_prompt(
                ref_audio=str(REFERENCE_AUDIO),
                ref_text=REFERENCE_TEXT.read_text(encoding="utf-8").strip(),
            )
    except Exception:
        release_model()
        raise
    return {"ready": True}


def release_model() -> None:
    global _model, _voice_clone_prompt
    _voice_clone_prompt = None
    _model = None
    gc.collect()
    try:
        import torch

        if torch.cuda.is_available():
            torch.cuda.empty_cache()
    except ImportError:
        pass


def synthesize_fast(text: str) -> str:
    import torch
    import soundfile as sf
    warm_model()
    model = get_model()
    with torch.inference_mode():
        wavs, sample_rate = model.generate_voice_clone(
            text=text,
            language="Vietnamese",
            voice_clone_prompt=_voice_clone_prompt,
            non_streaming_mode=True,
        )
    if not wavs:
        raise RuntimeError("Gwen returned no audio.")
    output = tempfile.NamedTemporaryFile(delete=False, suffix=".wav")
    output.close()
    sf.write(output.name, wavs[0], sample_rate)
    return output.name

def synthesize_gwen(text: str) -> str:
    if not WORKER_SCRIPT.is_file():
        raise FileNotFoundError(f"Gwen worker not found: {WORKER_SCRIPT}")
    output = tempfile.NamedTemporaryFile(delete=False, suffix=".wav")
    output.close()
    try:
        completed = subprocess.run(
            [sys.executable, str(WORKER_SCRIPT), "--text", text, "--output", output.name],
            capture_output=True,
            encoding="utf-8",
            errors="replace",
            timeout=120,
            check=False,
            env={**os.environ, "PYTHONUTF8": "1"},
        )
        if completed.returncode != 0:
            detail = completed.stderr.strip() or completed.stdout.strip() or "Gwen worker failed."
            raise RuntimeError(detail)
        output_path = Path(output.name)
        if not output_path.is_file() or output_path.stat().st_size == 0:
            raise RuntimeError("Gwen did not create a playable audio file.")
        return output.name
    except Exception:
        Path(output.name).unlink(missing_ok=True)
        raise

def create_app(synthesize: Callable[[str], str] | None = None) -> Flask:
    app = Flask(__name__)

    @app.post("/warmup")
    def warmup():
        try:
            return jsonify(warm_model())
        except Exception as error:
            return jsonify({"error": str(error) or type(error).__name__}), 500

    @app.post("/release")
    def release():
        release_model()
        return jsonify({"released": True})

    @app.post("/tts")
    def tts():
        data = request.get_json(silent=True) or {}
        text = data.get("text", "").strip()
        fast_mode = data.get("fast_mode", True)
        if not text:
            return jsonify({"error": "No text provided"}), 400
        try:
            started = perf_counter()
            if synthesize is not None:
                audio_path = synthesize(text)
            else:
                audio_path = synthesize_fast(text) if fast_mode else synthesize_gwen(text)
            if not Path(audio_path).is_file():
                raise RuntimeError("Gwen did not create a playable audio file.")
            elapsed_ms = round((perf_counter() - started) * 1000)
            return jsonify({"audio_path": audio_path, "synthesis_ms": elapsed_ms})
        except Exception as error:
            return jsonify({"error": str(error) or type(error).__name__}), 500

    return app


if __name__ == "__main__":
    create_app().run(host="127.0.0.1", port=5051, threaded=False)
