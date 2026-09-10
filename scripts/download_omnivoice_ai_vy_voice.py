from __future__ import annotations

import tempfile
from pathlib import Path
from urllib.request import urlopen

PROJECT_ROOT = Path(__file__).resolve().parents[1]
TARGET = PROJECT_ROOT / "data" / "voices" / "omnivoice" / "ai_vy.wav"
SOURCE = "https://huggingface.co/g-group-ai-lab/gwen-tts-0.6B/resolve/main/data/ref_audio/ai_vy.wav"
DOWNLOAD_TIMEOUT_SECONDS = 120
_MAX_DOWNLOAD_BYTES = 50 * 1024 * 1024
_CHUNK_BYTES = 1 << 20


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

    expected_size = _expected_size()
    if expected_size is not None and expected_size > _MAX_DOWNLOAD_BYTES:
        raise RuntimeError("Downloaded Ái Vy reference audio exceeds the size limit.")
    tmp_path: Path | None = None
    try:
        with urlopen(SOURCE, timeout=DOWNLOAD_TIMEOUT_SECONDS) as response, tempfile.NamedTemporaryFile(
            delete=False, dir=str(TARGET.parent), prefix=TARGET.name, suffix=".part"
        ) as tmp_file:
            tmp_path = Path(tmp_file.name)
            downloaded = 0
            while True:
                chunk = response.read(_CHUNK_BYTES)
                if not chunk:
                    break
                downloaded += len(chunk)
                if downloaded > _MAX_DOWNLOAD_BYTES:
                    raise RuntimeError("Downloaded Ái Vy reference audio exceeds the size limit.")
                tmp_file.write(chunk)
        if tmp_path is None or tmp_path.stat().st_size <= 0:
            raise RuntimeError("Downloaded Ái Vy reference audio is empty.")
        if expected_size is not None and expected_size > 0 and tmp_path.stat().st_size != expected_size:
            raise RuntimeError(
                f"Downloaded Ái Vy reference audio is incomplete "
                f"({tmp_path.stat().st_size}/{expected_size} bytes)."
            )
        tmp_path.replace(TARGET)
        tmp_path = None
    finally:
        if tmp_path is not None:
            tmp_path.unlink(missing_ok=True)
    print(TARGET)


if __name__ == "__main__":
    main()
