from __future__ import annotations

import tempfile
import gc
from pathlib import Path
from time import perf_counter
from typing import Callable

from flask import Flask, jsonify, request

PROJECT_ROOT = Path(__file__).resolve().parent
MODEL_DIR = PROJECT_ROOT / "models" / "gwen-tts-0.6b"
REFERENCE_AUDIO = PROJECT_ROOT / "tyc_talk.wav"
REFERENCE_TEXT = PROJECT_ROOT / "reference_text.txt"
YEN_NHI_AUDIO = PROJECT_ROOT / "data" / "voices" / "gwen" / "yen_nhi.wav"
YEN_NHI_TEXT = (
    "sao lại không liên quan. các anh lấy vợ rồi các anh cứ đội chị lên đầu "
    "làm nóc nhà ấy, suốt ngày hỏi ý kiến các chị thì làm sao mà ra vấn đề "
    "được cho em đúng không."
)
VOICE_PROFILES = {
    "ru": (REFERENCE_AUDIO, REFERENCE_TEXT.read_text(encoding="utf-8").strip()),
    "yen_nhi": (YEN_NHI_AUDIO, YEN_NHI_TEXT),
}

_model = None
_voice_clone_prompts: dict[str, object] = {}

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


def voice_profile(profile: str) -> tuple[Path, str]:
    if not isinstance(profile, str):
        raise ValueError(f"Unsupported Gwen voice profile: {profile!r}")
    try:
        audio, transcript = VOICE_PROFILES[profile]
    except KeyError as error:
        raise ValueError(f"Unsupported Gwen voice profile: {profile}") from error
    if not audio.is_file():
        raise FileNotFoundError(f"Gwen voice asset is missing: {audio}")
    return audio, transcript


def warm_model(profile: str = "ru") -> dict[str, bool]:
    audio, transcript = voice_profile(profile)
    try:
        model = get_model()
        if profile not in _voice_clone_prompts:
            _voice_clone_prompts[profile] = model.create_voice_clone_prompt(
                ref_audio=str(audio),
                ref_text=transcript,
            )
    except Exception:
        release_model()
        raise
    return {"ready": True}


def release_model() -> None:
    global _model
    _voice_clone_prompts.clear()
    _model = None
    gc.collect()
    try:
        import torch

        if torch.cuda.is_available():
            torch.cuda.empty_cache()
    except ImportError:
        pass


def synthesize_fast(text: str, profile: str = "ru") -> str:
    warm_model(profile)
    import torch
    import soundfile as sf
    model = get_model()
    with torch.inference_mode():
        wavs, sample_rate = model.generate_voice_clone(
            text=text,
            language="Vietnamese",
            voice_clone_prompt=_voice_clone_prompts[profile],
            non_streaming_mode=True,
        )
    if not wavs:
        raise RuntimeError("Gwen returned no audio.")
    output = tempfile.NamedTemporaryFile(delete=False, suffix=".wav")
    output.close()
    sf.write(output.name, wavs[0], sample_rate)
    return output.name

def create_app(synthesize: Callable[[str], str] | None = None) -> Flask:
    app = Flask(__name__)

    @app.post("/warmup")
    def warmup():
        try:
            data = request.get_json(silent=True) or {}
            return jsonify(warm_model(data.get("voice_profile", "ru")))
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
        profile = data.get("voice_profile", "ru")
        if not text:
            return jsonify({"error": "No text provided"}), 400
        try:
            started = perf_counter()
            if synthesize is not None:
                audio_path = synthesize(text)
            else:
                audio_path = synthesize_fast(text, profile)
            if not Path(audio_path).is_file():
                raise RuntimeError("Gwen did not create a playable audio file.")
            elapsed_ms = round((perf_counter() - started) * 1000)
            return jsonify({"audio_path": audio_path, "synthesis_ms": elapsed_ms})
        except Exception as error:
            return jsonify({"error": str(error) or type(error).__name__}), 500

    return app


if __name__ == "__main__":
    create_app().run(host="127.0.0.1", port=5051, threaded=False)
