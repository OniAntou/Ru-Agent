from __future__ import annotations

import socket
from pathlib import Path
from urllib.request import urlopen, urlretrieve


PROJECT_ROOT = Path(__file__).resolve().parents[1]
TARGET = PROJECT_ROOT / "data" / "voices" / "omnivoice" / "ai_vy.wav"
SOURCE = "https://huggingface.co/g-group-ai-lab/gwen-tts-0.6B/resolve/main/data/ref_audio/ai_vy.wav"
DOWNLOAD_TIMEOUT_SECONDS = 120


def _expected_size() -> int | None:
    with urlopen(SOURCE, timeout=DOWNLOAD_TIMEOUT_SECONDS) as response:
        content_length = response.headers.get("Content-Length")
    if content_length is None:
        return None
    try:
        return int(content_length)
    except ValueError:
        return None


def main() -> None:
    TARGET.parent.mkdir(parents=True, exist_ok=True)
    if TARGET.is_file():
        print(TARGET)
        return

    socket.setdefaulttimeout(DOWNLOAD_TIMEOUT_SECONDS)
    expected_size = _expected_size()
    urlretrieve(SOURCE, TARGET)
    if expected_size is not None and expected_size > 0 and TARGET.stat().st_size != expected_size:
        TARGET.unlink(missing_ok=True)
        raise RuntimeError(
            f"Downloaded Ái Vy reference audio is incomplete "
            f"({TARGET.stat().st_size if TARGET.is_file() else 0}/{expected_size} bytes)."
        )
    if TARGET.stat().st_size <= 0:
        raise RuntimeError("Downloaded Ái Vy reference audio is empty.")
    print(TARGET)


if __name__ == "__main__":
    main()
