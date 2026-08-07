from __future__ import annotations

import gc
import io
import logging
import os
import tempfile
import threading
import wave
from pathlib import Path
from time import perf_counter
from typing import Callable

from flask import Flask, Response, jsonify, request
from mini_gpt.omnivoice_tuning import settings_for_profile
from mini_gpt.tts_chunks import split_tts_chunks
from mini_gpt.voice_pacing import DEFAULT_VOICE_SPEED, clamp_voice_speed
from mini_gpt.voice_style import (
    ORIGINAL_STYLE_ID,
    VoiceStyle,
    VoiceStyleId,
    voice_style_by_id,
)

logger = logging.getLogger(__name__)
_SINGLETON_GUARD: object | None = None


def _acquire_single_instance() -> bool:
    """Hold one OS-level guard for the heavyweight OmniVoice process."""
    global _SINGLETON_GUARD
    if _SINGLETON_GUARD is not None:
        return True
    if os.name == "nt":
        import ctypes

        kernel32 = ctypes.windll.kernel32
        handle = kernel32.CreateMutexW(None, False, "Local\\RuOmniVoice5052")
        if not handle:
            raise OSError("CreateMutexW failed for OmniVoice singleton")
        if kernel32.GetLastError() == 183:
            kernel32.CloseHandle(handle)
            return False
        _SINGLETON_GUARD = handle
        return True

    import fcntl

    guard = (Path(tempfile.gettempdir()) / "ru-omnivoice-5052.lock").open("a+b")
    try:
        fcntl.flock(guard.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError:
        guard.close()
        return False
    _SINGLETON_GUARD = guard
    return True

PROJECT_ROOT = Path(__file__).resolve().parent
MODEL_CACHE = PROJECT_ROOT / "models" / "omnivoice"
REFERENCE_AUDIO = PROJECT_ROOT / "tyc_talk.wav"
REFERENCE_TEXT = PROJECT_ROOT / "reference_text.txt"
AI_VY_AUDIO = PROJECT_ROOT / "data" / "voices" / "omnivoice" / "ai_vy.wav"
AI_VY_TEXT = (
    "việt nam đang kiêu hãnh bước vào kỷ nguyên vươn mình rực rỡ với khát vọng "
    "mãnh liệt, trí tuệ đổi mới và tinh thần đoàn kết đất nước, tự tin bứt phá, "
    "kiến tạo một tương lai thịnh vượng và vươn tầm quốc tế."
)
# k2-fsa/OmniVoice outputs mono 24 kHz PCM; the "ru" transcript is loaded lazily
# so a missing reference_text.txt never breaks the server import.
MODEL_SAMPLE_RATE = 24_000
MAX_TTS_TEXT_CHARS = 10_000
VOICE_PROFILES = {
    "ru": (REFERENCE_AUDIO, None),
    "ai_vy": (AI_VY_AUDIO, AI_VY_TEXT),
}

_model = None
_voice_clone_prompts: dict[str, object] = {}
_MODEL_LOCK = threading.RLock()
_librosa = None


def get_model():
    global _model
    with _MODEL_LOCK:
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
    if transcript is None:
        try:
            transcript = REFERENCE_TEXT.read_text(encoding="utf-8").strip()
        except OSError as error:
            logger.error("OmniVoice reference transcript is missing: %s", REFERENCE_TEXT)
            raise RuntimeError(f"OmniVoice reference transcript is missing: {REFERENCE_TEXT}") from error
    if not audio.is_file():
        raise FileNotFoundError(f"OmniVoice voice asset is missing: {audio}")
    return audio, transcript


def warm_model(profile: str = "ru") -> dict[str, bool]:
    audio, transcript = voice_profile(profile)
    try:
        with _MODEL_LOCK:
            model = get_model()
            if profile not in _voice_clone_prompts:
                _voice_clone_prompts[profile] = model.create_voice_clone_prompt(
                    ref_audio=str(audio),
                    ref_text=transcript,
                )
    except Exception:
        logger.exception("Failed to warm the OmniVoice model for profile %r", profile)
        try:
            release_model()
        except Exception:
            logger.exception("Failed to release the OmniVoice model after a warm-up error")
        raise
    return {"ready": True}


def release_model() -> None:
    global _model
    with _MODEL_LOCK:
        _voice_clone_prompts.clear()
        _model = None
        gc.collect()
        try:
            import torch

            if torch.cuda.is_available():
                torch.cuda.empty_cache()
        except ImportError:
            pass


def apply_voice_speed(audio, speed: float):
    global _librosa
    import numpy as np

    speed = clamp_voice_speed(speed)
    if hasattr(audio, "detach"):
        audio = audio.detach().cpu().numpy()
    try:
        waveform = np.asarray(audio, dtype=np.float32)
        if waveform.ndim == 1:
            waveform = waveform.reshape(1, -1)
        if waveform.ndim != 2:
            raise ValueError(f"Unexpected audio shape: {waveform.shape}")
    except Exception as error:
        raise RuntimeError(f"Could not convert OmniVoice audio into a waveform: {error}") from error
    if speed == 1.0:
        if waveform.shape[0] == 1:
            return waveform.reshape(-1)
        return waveform
    if _librosa is None:
        import librosa

        _librosa = librosa
    stretched = _librosa.effects.time_stretch(waveform, rate=speed)
    if stretched.ndim == 2 and stretched.shape[0] == 1:
        return stretched.reshape(-1)
    return stretched


def synthesize_fast(
    text: str,
    profile: str = "ru",
    speed: float = DEFAULT_VOICE_SPEED,
    *,
    voice_style_id: VoiceStyleId = ORIGINAL_STYLE_ID,
    timing: Callable[[str, float], None] | None = None,
) -> str:
    total_started = perf_counter()
    warmup_started = perf_counter()
    warm_model(profile)
    if timing is not None:
        timing("warmup_ms", (perf_counter() - warmup_started) * 1000)
    import soundfile as sf
    import torch

    generation_started = perf_counter()
    style = voice_style_by_id(voice_style_id)
    generate_kwargs: dict[str, object] = {
        "text": text,
        "voice_clone_prompt": _voice_clone_prompts[profile],
        **settings_for_profile(profile).generate_kwargs(),
    }
    if style.instruct is not None:
        generate_kwargs["instruct"] = style.instruct
    with _MODEL_LOCK:
        with torch.inference_mode():
            audio = get_model().generate(**generate_kwargs)
    if timing is not None:
        timing("generate_ms", (perf_counter() - generation_started) * 1000)
    if not audio:
        raise RuntimeError("OmniVoice returned no audio.")
    waveform = apply_voice_speed(audio[0], speed)
    output = tempfile.NamedTemporaryFile(delete=False, suffix=".wav")
    output.close()
    write_started = perf_counter()
    try:
        sf.write(output.name, waveform, MODEL_SAMPLE_RATE)
        if timing is not None:
            timing("write_ms", (perf_counter() - write_started) * 1000)
            timing("total_ms", (perf_counter() - total_started) * 1000)
    except Exception:
        try:
            Path(output.name).unlink(missing_ok=True)
        except OSError:
            pass
        raise
    return output.name


def _voice_style_from_request(data: dict[str, object]) -> VoiceStyle:
    raw_style_id = data.get("voice_style_id", ORIGINAL_STYLE_ID)
    if not isinstance(raw_style_id, str):
        raise ValueError("Unsupported OmniVoice voice style.")
    return voice_style_by_id(raw_style_id)


def create_app(
    synthesize: Callable[[str, str, float, VoiceStyleId], str] | None = None,
) -> Flask:
    app = Flask(__name__)

    @app.get("/status")
    def status():
        return jsonify({"model_loaded": _model is not None})

    @app.post("/warmup")
    def warmup():
        data = request.get_json(silent=True)
        if data is None:
            return jsonify({"error": "Request body must be JSON."}), 400
        try:
            return jsonify(warm_model(data.get("voice_profile", "ru")))
        except Exception:
            logger.exception("OmniVoice /warmup failed")
            return jsonify({"error": "Model warm-up failed. Check the server logs."}), 500

    @app.post("/release")
    def release():
        release_model()
        return jsonify({"released": True})

    @app.post("/tts")
    def tts():
        data = request.get_json(silent=True)
        if data is None:
            return jsonify({"error": "Request body must be JSON."}), 400
        raw_text = data.get("text")
        text = raw_text.strip() if isinstance(raw_text, str) else ""
        profile = data.get("voice_profile", "ru")
        speed = clamp_voice_speed(data.get("speed", DEFAULT_VOICE_SPEED))
        try:
            style = _voice_style_from_request(data)
        except ValueError:
            return jsonify({"error": "Unsupported OmniVoice voice style."}), 400
        if not text:
            return jsonify({"error": "No text provided"}), 400
        if len(text) > MAX_TTS_TEXT_CHARS:
            return jsonify({"error": f"Text too long (max {MAX_TTS_TEXT_CHARS} characters)"}), 400
        audio_path: str | None = None
        try:
            started = perf_counter()
            audio_path = (
                synthesize(text, profile, speed, style.id)
                if synthesize is not None
                else synthesize_fast(
                    text,
                    profile,
                    speed,
                    voice_style_id=style.id,
                )
            )
            if not isinstance(audio_path, str) or not Path(audio_path).is_file():
                raise RuntimeError("OmniVoice did not create a playable audio file.")
            audio_bytes = Path(audio_path).read_bytes()
            elapsed_ms = round((perf_counter() - started) * 1000)
            return Response(
                audio_bytes,
                mimetype="audio/wav",
                headers={
                    "Content-Disposition": "inline; filename=ru_tts.wav",
                    "X-Synthesis-Ms": str(elapsed_ms),
                },
            )
        except Exception:
            logger.exception("OmniVoice /tts synthesis failed")
            return jsonify({"error": "Synthesis failed. Check the server logs."}), 500
        finally:
            if isinstance(audio_path, str):
                try:
                    Path(audio_path).unlink(missing_ok=True)
                except OSError:
                    pass

    @app.post("/tts_stream")
    def tts_stream():
        data = request.get_json(silent=True)
        if data is None:
            return jsonify({"error": "Request body must be JSON."}), 400
        raw_text = data.get("text")
        text = raw_text.strip() if isinstance(raw_text, str) else ""
        profile = data.get("voice_profile", "ru")
        speed = clamp_voice_speed(data.get("speed", DEFAULT_VOICE_SPEED))
        try:
            style = _voice_style_from_request(data)
        except ValueError:
            return jsonify({"error": "Unsupported OmniVoice voice style."}), 400
        if not text:
            return jsonify({"error": "No text provided"}), 400
        if len(text) > MAX_TTS_TEXT_CHARS:
            return jsonify({"error": f"Text too long (max {MAX_TTS_TEXT_CHARS} characters)"}), 400
        try:
            warm_model(profile)
            buffer = io.BytesIO()
            with wave.open(buffer, "wb") as output:
                output.setnchannels(1)
                output.setsampwidth(2)
                output.setframerate(MODEL_SAMPLE_RATE)
                for chunk in split_tts_chunks(text):
                    chunk_path = synthesize_fast(
                        chunk,
                        profile,
                        speed,
                        voice_style_id=style.id,
                    )
                    try:
                        with wave.open(chunk_path, "rb") as source:
                            output.writeframes(source.readframes(source.getnframes()))
                    finally:
                        try:
                            Path(chunk_path).unlink(missing_ok=True)
                        except OSError:
                            pass
            return Response(buffer.getvalue(), mimetype="audio/wav")
        except Exception:
            logger.exception("OmniVoice /tts_stream failed")
            return jsonify({"error": "Synthesis failed. Check the server logs."}), 500

    return app


def run_server() -> int:
    if not _acquire_single_instance():
        logger.error("Another OmniVoice instance already owns the Ru voice service")
        return 2
    create_app().run(host="127.0.0.1", port=5052, threaded=False)
    return 0


if __name__ == "__main__":
    raise SystemExit(run_server())
