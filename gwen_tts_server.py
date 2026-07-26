from __future__ import annotations

import os
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Callable

from flask import Flask, jsonify, request

PROJECT_ROOT = Path(__file__).resolve().parent
WORKER_SCRIPT = PROJECT_ROOT / "gwen_tts_worker.py"


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

    @app.post("/tts")
    def tts():
        text = (request.get_json(silent=True) or {}).get("text", "").strip()
        if not text:
            return jsonify({"error": "No text provided"}), 400
        try:
            audio_path = (synthesize or synthesize_gwen)(text)
            if not Path(audio_path).is_file():
                raise RuntimeError("Gwen did not create a playable audio file.")
            return jsonify({"audio_path": audio_path})
        except Exception as error:
            return jsonify({"error": str(error) or type(error).__name__}), 500

    return app


if __name__ == "__main__":
    create_app().run(host="127.0.0.1", port=5051, threaded=False)

