from __future__ import annotations

from pathlib import Path
from urllib.request import urlretrieve


PROJECT_ROOT = Path(__file__).resolve().parents[1]
TARGET = PROJECT_ROOT / "data" / "voices" / "omnivoice" / "ai_vy.wav"
SOURCE = "https://huggingface.co/g-group-ai-lab/gwen-tts-0.6B/resolve/main/data/ref_audio/ai_vy.wav"


def main() -> None:
    TARGET.parent.mkdir(parents=True, exist_ok=True)
    if not TARGET.is_file() or TARGET.stat().st_size == 0:
        urlretrieve(SOURCE, TARGET)
    if TARGET.stat().st_size == 0:
        raise RuntimeError("Downloaded Ái Vy reference audio is empty.")
    print(TARGET)


if __name__ == "__main__":
    main()
