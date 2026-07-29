from __future__ import annotations

import gc
import os
import tempfile
from pathlib import Path
from time import perf_counter
from typing import Callable

from flask import Flask, jsonify, request
from mini_gpt.omnivoice_tuning import GUI_TRIAL_SETTINGS

PROJECT_ROOT = Path(__file__).resolve().parent
MODEL_CACHE = PROJECT_ROOT / "models" / "omnivoice"
REFERENCE_AUDIO = PROJECT_ROOT / "tyc_talk.wav"
REFERENCE_TEXT = PROJECT_ROOT / "reference_text.txt"
YEN_NHI_AUDIO = PROJECT_ROOT / "data" / "voices" / "gwen" / "yen_nhi.wav"
YEN_NHI_TEXT = (
    "sao láº¡i khÃ´ng liÃªn quan. cÃ¡c anh láº¥y vá»£ rá»“i cÃ¡c anh cá»© Ä‘á»™i chá»‹ lÃªn Ä‘áº§u "
    "lÃ m nÃ³c nhÃ  áº¥y, suá»‘t ngÃ y há»i Ã½ kiáº¿n cÃ¡c chá»‹ thÃ¬ lÃ m sao mÃ  ra váº¥n Ä‘á» "
    "Ä‘Æ°á»£c cho em Ä‘Ãºng khÃ´ng."
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
        os.environ["HF_HOME"] = str(MODEL_CACHE)
        os.environ["HF_HUB_CACHE"] = str(MODEL_CACHE / "hub")
        import torch
        from omnivoice import OmniVoice

        if not torch.cuda.is_available():
            raise RuntimeError("CUDA is required for the local OmniVoice service.")
        _model = OmniVoice.from_pretrained(
            "k2-fsa/OmniVoice",
            device_map="cuda:0",
            dtype=torch.float16,
        )
    return _model


def voice_profile(profile: str) -> tuple[Path, str]:
    if not isinstance(profile, str):
        raise ValueError(f"Unsupported OmniVoice voice profile: {profile!r}")
    try:
        audio, transcript = VOICE_PROFILES[profile]
    except KeyError as error:
        raise ValueError(f"Unsupported OmniVoice voice profile: {profile}") from error
    if not audio.is_file():
        raise FileNotFoundError(f"OmniVoice voice asset is missing: {audio}")
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
    import soundfile as sf
    import torch

    with torch.inference_mode():
        audio = get_model().generate(
            text=text,
            voice_clone_prompt=_voice_clone_prompts[profile],
            **GUI_TRIAL_SETTINGS.generate_kwargs(),
        )
    if not audio:
        raise RuntimeError("OmniVoice returned no audio.")
    output = tempfile.NamedTemporaryFile(delete=False, suffix=".wav")
    output.close()
    sf.write(output.name, audio[0], 24_000)
    return output.name


def create_app(synthesize: Callable[[str, str], str] | None = None) -> Flask:
    app = Flask(__name__)

    @app.get("/status")
    def status():
        return jsonify({"pid": os.getpid(), "model_loaded": _model is not None})

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
            audio_path = (
                synthesize(text, profile) if synthesize is not None else synthesize_fast(text, profile)
            )
            if not Path(audio_path).is_file():
                raise RuntimeError("OmniVoice did not create a playable audio file.")
            elapsed_ms = round((perf_counter() - started) * 1000)
            return jsonify({"audio_path": audio_path, "synthesis_ms": elapsed_ms})
        except Exception as error:
            return jsonify({"error": str(error) or type(error).__name__}), 500

    return app


if __name__ == "__main__":
    create_app().run(host="127.0.0.1", port=5052, threaded=False)
