from __future__ import annotations

from pathlib import Path
from urllib.request import urlretrieve


PROJECT_ROOT = Path(__file__).resolve().parents[1]
TARGET = PROJECT_ROOT / "data" / "voices" / "gwen" / "yen_nhi.wav"
SOURCE = "https://raw.githubusercontent.com/ggroup-ai-lab/gwen-tts/main/data/ref_audio/yen_nhi.wav"


def main() -> None:
    TARGET.parent.mkdir(parents=True, exist_ok=True)
    if not TARGET.is_file() or TARGET.stat().st_size == 0:
        urlretrieve(SOURCE, TARGET)
    if TARGET.stat().st_size == 0:
        raise RuntimeError("Downloaded Yến Nhi reference audio is empty.")
    print(TARGET)


if __name__ == "__main__":
    main()
