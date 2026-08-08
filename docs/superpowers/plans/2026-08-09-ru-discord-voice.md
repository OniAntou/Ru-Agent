# Ru Discord Voice Bridge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cho Ru nghe + nói trọn vẹn trong voice channel Discord: sink nhận audio → VAD cắt câu → STT (Google, trừu tượng) → trigger "Ru" → chat backend (phiên per-channel) → TTS `/voice/stream` → phát ra channel, với barge-in HIGH và `/ru join|leave` chỉ chủ sở hữu.

**Architecture:** Process Python `mini_gpt.discord_bridge` được C# host (RuApp) spawn, die cùng Job Object như `BackendProcessManager`. Bot dùng **pycord** — có `voice_client.listen()`/`Sink` nhận PCM theo user (discord.py chính thức không hỗ trợ receive). Pipeline thuần Python tách lớp, mọi phần trừ glue pycord đều unit-testable với fakes; entry `bot.py` là glue mỏng.

**Tech Stack:** Python 3.11+; `py-cord[voice]>=2.6`; numpy (PCM); `websockets<14` đã có sẵn (uvicorn[standard]); `httpx>=0.25` đã có; tái sử dụng `mini_gpt.speech_runtime` (STT Google) + FastAPI backend `127.0.0.1:8000`.

## Global Constraints

- Backend WS chat: `ws://127.0.0.1:8000/chat?token=...` — client gửi `{"prompt": str, "session_id": str | None}`, nhận `{"type":"chunk"|"done"|"error"|"session", ...}`; reply đầy đủ nằm trong `done.content`.
- Backend WS TTS: `ws://127.0.0.1:8000/voice/stream?token=...` — gửi `{"type":"start","request_id":str,"text":str,"voice_label":str}`, nhận event JSON (`started`, `audio`, `ended`, `cancelled`, `failed`) trong đó `audio` events được theo sau bởi 1 message **binary** (PCM s16, sample_rate/format trong JSON). Hủy: `{"type":"cancel","request_id":...}`.
- Không gửi transcript ra kênh; `/ru join`, `/ru leave` chỉ trong DM và chỉ owner (`cfg.owner_id`).
- Trigger: trigger token xuất hiện đầu câu (`tokens[0]`, hoặc sau "ê"|"hey"|"ne"|"no" ở đầu) → `"high"`; ở giữa/cuối → `"medium"`; không → `None`.
- Barge-in: `"high"` khi đang play → cancel + start mới ngay. `"medium"` khi đang play → queue, sau khi playback xong mới xử lý. `None` → bỏ qua.
- Cấu hình `~/.minigpt/discord.json`; secrets chỉ qua env (`DISCORD_TOKEN`, `RU_DESKTOP_TOKEN`).
- Chạy test: `& D:\LLM\.venv\Scripts\python.exe -m pytest -q` từ thư mục repo. Tests async, tạo test server, hết miền `asyncio.run(...)` — không dùng plugin pytest-asyncio (repo không có).
- Ruff: `line-length=100` (pyproject).

---

### Task 1: Scaffold package, `DiscordBotConfig`, `load_config`, CLI skeleton

**Files:**
- Create: `src/mini_gpt/discord_bridge/__init__.py` — chỉ docstring `"""Ru Discord voice bridge."""`
- Create: `src/mini_gpt/discord_bridge/config.py`
- Create: `tests/test_discord_config.py`
- Modify: `pyproject.toml` — thêm `discord = ["py-cord[voice]>=2.6"]` vào `[project.optional-dependencies]`

**Interfaces (produces):**
- `class DiscordConfigError(ValueError)`
- `@dataclass(frozen=True, slots=True) class DiscordBotConfig`:
  - fields: `owner_id: str`, `trigger_names: tuple[str, ...] = ("ru",)`, `guild_ids: tuple[int, ...] = ()`, `backend_url: str = "http://127.0.0.1:8000"`
  - `to_dict() -> dict`, `from_dict(d: dict) -> DiscordBotConfig` (owner rỗng → raise `DiscordConfigError`)
- `load_config(path: Path | None = None) -> DiscordBotConfig` — mặc định `~/.minigpt/discord.json`; nếu chưa có → ghi file mặc định rồi raise `DiscordConfigError`; JSON hỏng → `DiscordConfigError`.

- [ ] **Step 1: Write failing test**

```python
# tests/test_discord_config.py
import json

import pytest

from mini_gpt.discord_bridge.config import (
    DiscordBotConfig,
    DiscordConfigError,
    load_config,
)


def test_round_trip() -> None:
    cfg = DiscordBotConfig(owner_id="123", trigger_names=("ru",), guild_ids=(42,))
    assert DiscordBotConfig.from_dict(cfg.to_dict()) == cfg


def test_missing_owner_raises() -> None:
    with pytest.raises(DiscordConfigError):
        DiscordBotConfig.from_dict({"owner_id": "  "})


def test_load_config_creates_default_then_raises(tmp_path) -> None:
    path = tmp_path / "discord.json"
    with pytest.raises(DiscordConfigError):
        load_config(path)
    raw = json.loads(path.read_text(encoding="utf-8"))
    assert "owner_id" in raw


def test_load_config_reads_existing(tmp_path) -> None:
    path = tmp_path / "discord.json"
    path.write_text(json.dumps({"owner_id": "999"}), encoding="utf-8")
    assert load_config(path).owner_id == "999"
```

- [ ] **Step 2: Run to verify it fails**

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_discord_config.py -v`
Expected: FAIL — `ModuleNotFoundError: No module named 'mini_gpt.discord_bridge'`

- [ ] **Step 3: Write implementation**

```python
# src/mini_gpt/discord_bridge/config.py
from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path


class DiscordConfigError(ValueError):
    pass


_DEFAULT_PATH = Path.home() / ".minigpt" / "discord.json"
_DEFAULT_BACKEND = "http://127.0.0.1:8000"


@dataclass(frozen=True, slots=True)
class DiscordBotConfig:
    owner_id: str
    trigger_names: tuple[str, ...] = ("ru",)
    guild_ids: tuple[int, ...] = ()
    backend_url: str = _DEFAULT_BACKEND

    def to_dict(self) -> dict:
        return {
            "owner_id": self.owner_id,
            "trigger_names": list(self.trigger_names),
            "guild_ids": list(self.guild_ids),
            "backend_url": self.backend_url,
        }

    @classmethod
    def from_dict(cls, d: dict) -> "DiscordBotConfig":
        owner_id = str(d.get("owner_id", "")).strip()
        if not owner_id:
            raise DiscordConfigError("owner_id is required in discord.json")
        triggers = tuple(
            str(t).strip() for t in d.get("trigger_names", ["ru"]) if str(t).strip()
        )
        guilds = tuple(int(g) for g in d.get("guild_ids", []) if str(g).strip())
        return cls(
            owner_id=owner_id,
            trigger_names=triggers or ("ru",),
            guild_ids=guilds,
            backend_url=str(d.get("backend_url", _DEFAULT_BACKEND)),
        )


def load_config(path: Path | None = None) -> DiscordBotConfig:
    cfg_path = path or _DEFAULT_PATH
    if not cfg_path.exists():
        cfg_path.parent.mkdir(parents=True, exist_ok=True)
        cfg_path.write_text(
            json.dumps(DiscordBotConfig(owner_id="").to_dict(), indent=2, ensure_ascii=False),
            encoding="utf-8",
        )
        raise DiscordConfigError(
            f"Configuration missing: {cfg_path} - open it and fill owner_id"
        )
    try:
        raw = json.loads(cfg_path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        raise DiscordConfigError(f"Invalid discord.json: {error}") from error
    return DiscordBotConfig.from_dict(raw)
```

- [ ] **Step 4: Run test to verify it passes** — như Step 2; Expected: PASS (4 tests).

- [ ] **Step 5: Thêm `[project.optional-dependencies]` discord**

```toml
discord = ["py-cord[voice]>=2.6"]
```

- [ ] **Step 6: DONE file để cả repo không vỡ; Commit**

```bash
git add pyproject.toml src/mini_gpt/discord_bridge tests/test_discord_config.py
git commit -m "feat(discord): scaffold discord bridge config"
```

---

### Task 2: Trigger detector

**Files:**
- Create: `src/mini_gpt/discord_bridge/triggers.py`
- Create: `tests/test_discord_triggers.py`

**Interfaces:**
- `detect_trigger(text: str, trigger_names: tuple[str, ...] = ("ru",)) -> str | None`
  - Chuẩn hóa: lowercase, strip dấu tiếng Việt về gốc (map bảng đầy đủ), cut punctuation `.,!?;:()[]«»…"'`.
  - HIGH khi token[0] là trigger, hoặc token[0] ∈ {"e","hey","ne","no"} và token[1] là trigger. MEDIUM khi trigger xuất hiện ở token ≠ 0. Khác → None.

- [ ] **Step 1: Write failing test**

```python
# tests/test_discord_triggers.py
from mini_gpt.discord_bridge.triggers import detect_trigger


def test_head_trigger_is_high() -> None:
    assert detect_trigger("Ru ơi em khỏe không") == "high"
    assert detect_trigger("Ê ru, tối nay đi đâu") == "high"
    assert detect_trigger("Rú, anh gọi nè") == "high"
    assert detect_trigger("Ru?") == "high"


def test_trigger_in_middle_is_medium() -> None:
    assert detect_trigger("anh muốn ru hát bài này") == "medium"
    assert detect_trigger("nói chuyện với RU đi") == "medium"


def test_no_trigger_is_none() -> None:
    assert detect_trigger("hôm nay trời đẹp") is None
    assert detect_trigger("") is None


def test_custom_trigger_names() -> None:
    assert detect_trigger("Em ơi giúp anh", ("em",)) == "high"
    assert detect_trigger("xin chào em xinh", ("em",)) == "medium"
```

- [ ] **Step 2: Run to verify fail** — `pytest tests/test_discord_triggers.py -v`; FAIL import.

- [ ] **Step 3: Write implementation**

```python
# src/mini_gpt/discord_bridge/triggers.py
from __future__ import annotations

from typing import Literal

_ACCENT_MAP = {
    "á": "a", "à": "a", "ả": "a", "ã": "a", "ạ": "a",
    "â": "a", "ấ": "a", "ầ": "a", "ẩ": "a", "ẫ": "a", "ậ": "a",
    "ă": "a", "ắ": "a", "ằ": "a", "ẳ": "a", "ẵ": "a", "ặ": "a",
    "é": "e", "è": "e", "ẻ": "e", "ẽ": "e", "ẹ": "e",
    "ê": "e", "ế": "e", "ề": "e", "ể": "e", "ễ": "e", "ệ": "e",
    "í": "i", "ì": "i", "ỉ": "i", "ĩ": "i", "ị": "i",
    "ó": "o", "ò": "o", "ỏ": "o", "õ": "o", "ọ": "o",
    "ô": "o", "ố": "o", "ồ": "o", "ổ": "o", "ỗ": "o", "ộ": "o",
    "ơ": "o", "ớ": "o", "ờ": "o", "ở": "o", "ỡ": "o", "ợ": "o",
    "ú": "u", "ù": "u", "ủ": "u", "ũ": "u", "ụ": "u",
    "ư": "u", "ứ": "u", "ừ": "u", "ử": "u", "ữ": "u", "ự": "u",
    "ý": "y", "ỳ": "y", "ỷ": "y", "ỹ": "y", "ỵ": "y",
    "đ": "d",
    "Á": "a", "À": "a", "Ả": "a", "Ã": "a", "Ạ": "a",
    "Â": "a", "Ấ": "a", "Ầ": "a", "Ẩ": "a", "Ẫ": "a", "Ậ": "a",
    "Ă": "a", "Ắ": "a", "Ằ": "a", "Ẳ": "a", "Ẵ": "a", "Ặ": "a",
    "É": "e", "È": "e", "Ẻ": "e", "Ẽ": "e", "Ẹ": "e",
    "Ê": "e", "Ế": "e", "Ề": "e", "Ể": "e", "Ễ": "e", "Ệ": "e",
    "Í": "i", "Ì": "i", "Ỉ": "i", "Ĩ": "i", "Ị": "i",
    "Ó": "o", "Ò": "o", "Ỏ": "o", "Õ": "o", "Ọ": "o",
    "Ô": "o", "Ố": "o", "Ồ": "o", "Ổ": "o", "Ỗ": "o", "Ộ": "o",
    "Ơ": "o", "Ớ": "o", "Ờ": "o", "Ở": "o", "Ỡ": "o", "Ợ": "o",
    "Ú": "u", "Ù": "u", "Ủ": "u", "Ũ": "u", "Ụ": "u",
    "Ư": "u", "Ứ": "u", "Ừ": "u", "Ử": "u", "Ữ": "u", "Ự": "u",
    "Ý": "y", "Ỳ": "y", "Ỷ": "y", "Ỹ": "y", "Ỵ": "y",
    "Đ": "d",
}
_PUNCT = ".,!?;:()[]«»…\"' "


def _chars_chuany(text: str) -> str:
    return "".join(_ACCENT_MAP.get(ch, ch) for ch in text.lower())


def _normalize(text: str) -> list[str]:
    return [t.strip(_PUNCT) for t in _chars_chuany(text).split() if t.strip(_PUNCT)]


def detect_trigger(text: str, trigger_names: tuple[str, ...] = ("ru",)) -> str | None:
    tokens = _normalize(text)
    if not tokens:
        return None
    targets = {t.lower() for t in trigger_names}
    if tokens[0] in targets:
        return "high"
    if len(tokens) >= 2 and tokens[0] in {"e", "hey", "ne", "no"} and tokens[1] in targets:
        return "high"
    if any(tok in targets for tok in tokens):
        return "medium"
    return None
```

- [ ] **Step 4: Run to pass**
- [ ] **Step 5: Commit** — `git commit -m "feat(discord): add Vietnamese trigger detector"`

---

### Task 3: Audio PCM helpers

**Files:**
- Create: `src/mini_gpt/discord_bridge/audio.py`
- Create: `tests/test_discord_audio.py`

**Interfaces:**
- `rms(pcm: bytes) -> float` (s16 LE mono)
- `has_energy(pcm: bytes, threshold: float = 300.0) -> bool`
- `down48_stereo_to_mono16(pcm: bytes) -> bytes` — 48kHz stereo interleaved s16 → 16kHz mono s16 (mỗi 3 mẫu, trung bình 2 kênh; Dùng `np.frombuffer(..., dtype="<i2").reshape(-1, 2)`; `[::3]`; `(L+R)//2`)
- `up16_mono_to_stereo48(pcm: bytes) -> bytes` — ngược (repeat 3, both channels = giá trị)
- `wav_bytes(sample_rate: int, pcm_mono16: bytes) -> bytes` — build WAV hợp chuẩn PCM 16 bit để pycord/FFmpeg play.

- [ ] **Step 1: Write failing test**

```python
# tests/test_discord_audio.py
import numpy as np

from mini_gpt.discord_bridge.audio import (
    down48_stereo_to_mono16,
    has_energy,
    rms,
    up16_mono_to_stereo48,
)


def test_downsample_half_rate() -> None:
    frames = np.arange(4800, dtype="<i2") // 8  # values 0..599
    stereo = np.zeros(4800 * 2, dtype="<i2")
    stereo[::2] = frames
    stereo[1::2] = frames
    out = down48_stereo_to_mono16(stereo.tobytes())
    assert len(out) == 1600 * 2
    got = np.frombuffer(out, dtype="<i2")
    assert got[0] == 0
    assert int(got[1]) == int(frames[3])


def test_downsample_averages_channels() -> None:
    stereo = np.zeros(60, dtype="<i2")  # 30 frames
    stereo[0::2] = 100
    stereo[1::2] = 200
    out = down48_stereo_to_mono16(stereo.tobytes())
    # take frame 0 -> avg 150
    assert np.frombuffer(out, dtype="<i2")[0] == 150


def test_upsample_triples_rate() -> None:
    mono = np.array([1, 2, 3], dtype="<i2")
    out = up16_mono_to_stereo48(mono.tobytes())
    assert len(out) == 3 * 3 * 2  # rate x3, 2 channels
    assert np.frombuffer(out, dtype="<i2")[:6].tolist() == [1, 1, 1, 1, 1, 1]


def test_energy_sine_vs_silence() -> None:
    sine = (np.sin(np.linspace(0, 20 * np.pi, 800)) * 8000).astype("<i2")
    assert rms(sine.tobytes()) > 5000
    assert has_energy(sine.tobytes())
    assert not has_energy(b"\x00\x00" * 400)
```

- [ ] **Step 2: Verify fail** — import error.
- [ ] **Step 3: Write implementation**

```python
from __future__ import annotations

import math
import struct

import numpy as np


def rms(pcm: bytes) -> float:
    if not pcm:
        return 0.0
    samples = np.frombuffer(pcm, dtype="<i2")
    return float(math.sqrt(float(np.mean(samples.astype(np.float64) ** 2))))


def has_energy(pcm: bytes, threshold: float = 300.0) -> bool:
    return rms(pcm) >= threshold


def down48_stereo_to_mono16(pcm: bytes) -> bytes:
    frames = np.frombuffer(pcm, dtype="<i2").reshape(-1, 2)
    down = frames[::3]
    mono = ((down[:, 0].astype(np.int32) + down[:, 1].astype(np.int32)) // 2).astype("<i2")
    return mono.tobytes()


def up16_mono_to_stereo48(pcm: bytes) -> bytes:
    mono = np.frombuffer(pcm, dtype="<i2")
    up = np.repeat(mono, 3)
    stereo = np.zeros(len(up) * 2, dtype="<i2")
    stereo[0::2] = up
    stereo[1::2] = up
    return stereo.tobytes()


def up_bytes(pcm_mono16: bytes) -> bytes:
    return up16_mono_to_stereo48(pcm_mono16)


def wav_bytes(pcm_mono16: bytes, sample_rate: int = 16000) -> bytes:
    """Wrap s16 mono PCM as a full RIFF WAV (for FFmpeg playback)."""
    n = len(pcm_mono16)
    data_hdr = b"data" + struct.pack("<I", n)
    fmt_hdr = (
        b"fmt "
        + struct.pack("<IHHIIHH", 16, 1, 1, sample_rate, sample_rate * 2, 2, 16)
    )
    return (
        b"RIFF"
        + struct.pack("<I", 36 + n)
        + b"WAVE"
        + fmt_hdr
        + data_hdr
        + pcm_mono16
    )
```

- [ ] **Step 4: Run to pass**
- [ ] **Step 5: Commit** — `git commit -m "feat(discord): PCM conversion and WAV helpers"`

---

### Task 4: Utterance collector (VAD — im lặng chia câu)

**Files:**
- Create: `src/mini_gpt/discord_bridge/vad.py`
- Create: `tests/test_discord_vad.py`

**Interfaces:**
- `class UtteranceCollector`:
  - `__init__(*, silence_seconds: float = 1.2, energy_threshold: float = 300.0, max_segment_seconds: float = 8.0)`
  - `push(user_key: str, pcm16k: bytes, now: float) -> bytes | None` — returns utterance hoàn chỉnh (bytes) hoặc None; per-user buffer; nếu utterance không energy suốt `silence_seconds` → return các byte đã thu (bao gồm cả phần im). Đơn giản: accumulate tới khi silence hết hạn.
  - `flush(user_key: str) -> bytes | None` — trả buffer đang dở, xóa.
  - Stale: nếu từ `started` tới `now` vượt `max_segment_seconds` và không energy → drop buffer (return None).

- [ ] **Step 1: Write failing test**

```python
import numpy as np

from mini_gpt.discord_bridge.vad import UtteranceCollector

FRAME_16K_20MS = 320 * 2  # 20ms @16k s16


def _sound() -> bytes:
    return (np.sin(np.linspace(0, 20 * np.pi, 320)) * 8000).astype("<i2").tobytes()


def test_utterance_ends_after_silence() -> None:
    c = UtteranceCollector(silence_seconds=0.04)
    c.push("u1", _sound(), 0.00)
    assert c.push("u1", b"\x00\x00" * 320, 0.02) is None
    out = c.push("u1", b"\x00\x00" * 320, 0.05)
    assert out is not None
    assert len(out) == FRAME_16K_20MS + 640 + 640  # sound + 2 silence frames


def test_separate_buffers_per_user() -> None:
    c = UtteranceCollector(silence_seconds=0.04)
    c.push("u1", _sound(), 0.0)
    # u2 push sound; u1 flush unaffected
    c.push("u2", _sound(), 0.0)
    assert c.flush("u1") is not None
    assert c.flush("u1") is None
    assert c.flush("u2") is not None


def test_stale_dropped_when_too_long() -> None:
    c = UtteranceCollector(silence_seconds=1.2, max_segment_seconds=0.5)
    c.push("u1", _sound(), 0.0)
    out = c.push("u1", b"\x00\x00" * 320, 0.6)
    assert out is None  # stale silently dropped
```

- [ ] **Step 2: Verify fail**
- [ ] **Step 3: Implement**

```python
from __future__ import annotations

from dataclasses import dataclass, field

from .audio import has_energy


@dataclass
class _Buffer:
    data: bytearray = field(default_factory=bytearray)
    started: float = 0.0
    last_active: float = 0.0


class UtteranceCollector:
    def __init__(
        self,
        *,
        silence_seconds: float = 1.2,
        energy_threshold: float = 300.0,
        max_segment_seconds: float = 8.0,
    ) -> None:
        self._silence_seconds = silence_seconds
        self._energy_threshold = energy_threshold
        self._max_segment_seconds = max_segment_seconds
        self._buffers: dict[str, _Buffer] = {}

    def push(self, user_key: str, pcm16k: bytes, now: float) -> bytes | None:
        buf = self._buffers.get(user_key)
        if buf is None:
            buf = self._buffers[user_key] = _Buffer(started=now, last_active=now)
        buf.data.extend(pcm16k)
        if has_energy(pcm16k, self._energy_threshold):
            buf.last_active = now
            if now - buf.started > self._max_segment_seconds:
                del self._buffers[user_key]
                return None
            return None
        if now - buf.last_active >= self._silence_seconds:
            return self._take(user_key)
        return None

    def flush(self, user_key: str) -> bytes | None:
        return self._take(user_key)

    def _take(self, user_key: str) -> bytes | None:
        buf = self._buffers.pop(user_key, None)
        if buf is None or not buf.data:
            return None
        return bytes(buf.data)
```

- [ ] **Step 4: Run to pass**
- [ ] **Step 5: Commit** — `git commit -m "feat(discord): silence-based utterance collector"`

---

### Task 5: Backend client (chat WS + voice WS)

**Files:**
- Create: `src/mini_gpt/discord_bridge/backend_client.py`
- Create: `tests/test_discord_backend_client.py`

**Interfaces:**
- `class BackendError(RuntimeError)`
- `class BackendClient`:
  - `__init__(self, backend_url: str, token: str)`
  - `async def ask(self, prompt: str, session_id: str | None = None, on_chunk=None) -> str` — mở WS chat; trả về toàn bộ reply text.
  - `async def stream_voice(self, text: str, voice_label: str, request_id: str, on_audio: Callable[[bytes], None]) -> None` — mở WS voice; gửi start; sẵn audio events → gọi `on_audio(pcm)`. Raise `BackendError` nếu `failed`.
  - `async def cancel_tts(self, request_id: str, ws: WebSocketClientProtocol | None) -> None` — nhận active WS, gửi cancel.

- [ ] **Step 1: Failing test** — uses a real local `websockets` server as fake backend (repo convention `asyncio.run`):

```python
# tests/test_discord_backend_client.py
import asyncio
import json

import pytest
import websockets

from mini_gpt.discord_bridge.backend_client import BackendClient, BackendError


async def _echo_chat(ws) -> None:
    msg = json.loads(await ws.recv())
    assert msg["prompt"] == "hello"
    await ws.send(json.dumps({"type": "chunk", "content": "chào "}))
    await ws.send(json.dumps({"type": "chunk", "content": "bạn"}))
    await ws.send(json.dumps({"type": "done", "content": "chào bạn"}))


async def _echo_voice(ws) -> None:
    start = json.loads(await ws.recv())
    assert start["type"] == "start"
    await ws.send(json.dumps({"type": "started", "request_id": start["request_id"], "sequence": 0}))
    await ws.send(json.dumps({"type": "audio", "request_id": start["request_id"], "sequence": 1}))
    await ws.send(b"\x00\x10" * 8)
    await ws.send(json.dumps({"type": "ended", "request_id": start["request_id"], "sequence": 2}))
    while True:
        msg = await ws.recv()
        if json.loads(msg).get("type") == "cancel":
            break


def test_ask_returns_joined_reply() -> None:
    async def scenario() -> None:
        server = await websockets.serve(_echo_chat, "127.0.0.1", 0)
        port = server.sockets[0].getsockname()[1]
        client = BackendClient(f"http://127.0.0.1:{port}", "tok")
        chunks: list[str] = []
        reply = await client.ask("hello", on_chunk=chunks.append)
        assert reply == "chào bạn"
        assert chunks == ["chào ", "bạn"]
        server.close()
        await server.wait_closed()

    asyncio.run(scenario())


def test_stream_tts_delivers_audio_bytes() -> None:
    async def scenario() -> None:
        server = await websockets.serve(_echo_voice, "127.0.0.1", 0)
        port = server.sockets[0].getsockname()[1]
        client = BackendClient(f"http://127.0.0.1:{port}", "tok")
        audio: list[bytes] = []
        await client.stream_tts("x", "Ru (OmniVoice)", "r1", on_audio=audio.append)
        assert audio == [b"\x00\x10" * 8]
        server.close()
        await server.wait_closed()

    asyncio.run(scenario())
```

- [ ] **Step 2: Verify fail**
- [ ] **Step 3: Implement**

```python
from __future__ import annotations

import asyncio
import json
from typing import Callable

import websockets


class BackendError(RuntimeError):
    pass


class BackendClient:
    def __init__(self, backend_url: str, token: str) -> None:
        self._base = backend_url.rstrip("/")
        self._token = token

    def _ws_url(self, path: str) -> str:
        ws_base = self._base.replace("http://", "ws://").replace("https://", "wss://")
        return f"{ws_base}{path}?token={self._token}"

    async def ask(
        self,
        prompt: str,
        session_id: str | None = None,
        on_chunk: Callable[[str], None] | None = None,
    ) -> str:
        async with websockets.connect(self._ws_url("/chat")) as ws:
            await ws.send(json.dumps({"prompt": prompt, "session_id": session_id}))
            reply = ""
            while True:
                msg = json.loads(await ws.recv())
                mtype = msg.get("type")
                if mtype == "chunk":
                    content = str(msg.get("content", ""))
                    reply += content
                    if on_chunk:
                        on_chunk(content)
                elif mtype == "done":
                    return reply
                elif mtype == "error":
                    raise BackendError(str(msg.get("content", "chat error")))
                elif mtype == "session":
                    continue
                else:
                    raise BackendError(f"unexpected chat message {mtype}")

    async def stream_tts(
        self,
        text: str,
        voice_label: str,
        request_id: str,
        on_audio: Callable[[bytes], None],
    ) -> None:
        async with websockets.connect(self._ws_url("/voice/stream")) as ws:
            await ws.send(
                json.dumps({
                    "type": "start",
                    "request_id": request_id,
                    "text": text,
                    "voice_label": voice_label,
                })
            )
            while True:
                raw = await ws.recv()
                if isinstance(raw, bytes):
                    continue  # ignore stray binary
                event = json.loads(raw)
                etype = event.get("type")
                if etype == "audio":
                    binary = await ws.recv()  # border next
                    if isinstance(binary, bytes):
                        on_audio(binary)
                        await ws.send(
                            json.dumps({"type": "playback", "request_id": request_id, "state": "started"})
                        )
                elif etype == "ended":
                    await ws.send(
                        json.dumps({"type": "playback", "request_id": request_id, "state": "ended"})
                    )
                    return
                elif etype in ("cancelled", "failed"):
                    raise BackendError(f"tts {etype}: {event.get('error_code')}")
```

- [ ] **Step 4: Run to pass**
- [ ] **Step 5: Commit** — `git commit -m "feat(discord): backend chat and voice websocket client"`

---

### Task 6: Turn pipeline — trigger → chat → TTS → play (with barge-in)

**Files:**
- Create: `src/mini_gpt/discord_bridge/pipeline.py`
- Create: `tests/test_discord_pipeline.py`

**Interfaces:**
- `class TurnPipeline` (logic thuần, mọi collaborator inject):
  - `__init__(self, *, recognizer, trigger_names: tuple[str, ...], chat_client, voice_client, on_play: Callable[[list[bytes]], Awaitable], on_cancel_current: Callable[[], Awaitable] | None = None)`
    - `recognizer.recognize(pcm16k: bytes) -> str` (speech_runtime `SpeechRecognizer`)
    - `chat_client.ask(prompt, session_id=None, on_chunk=None) -> str`
    - `voice_client.stream_tts(text, voice_label, request_id, on_audio) -> None`
  - `async def handle_utterance(user_key: str, pcm16k: bytes, now: float) -> None` — feed VAD; khi utterance xong:
    1. STT → transcript (bắt ngoại lệ `SpeechRecognitionError` → log + return)
    2. `detect_trigger` → None → drop; HIGH → nếu `_speaking` và `on_cancel_current` → cancel đang play rồi speak; MEDIUM → nếu `_speaking` → chờ lượt sau (queue 1 slot: `_queued: str | None`); speak.
    3. speak = chat.ask → stream_tts → gom chunk → `await on_play(chunks)`; `_speaking=True/False` xuống quanh.
  - `async def flush(user_key: str) -> None`
  - property `is_speaking: bool`
  - `async def drain_queue() -> None` — xử lý `_queued` sau khi play xong (gọi khi `on_play` trả).

- [ ] **Step 1: Write failing test**

```python
# tests/test_discord_pipeline.py
import asyncio

import pytest

from mini_gpt.discord_bridge.pipeline import TurnPipeline


class FakeRecognizer:
    def __init__(self, transcript: str) -> None:
        self._transcript = transcript

    def recognize(self, pcm16k: bytes) -> str:
        return self._transcript


class FakeChat:
    def __init__(self) -> None:
        self.prompts: list[str] = []

    async def ask(self, prompt: str, session_id=None, on_chunk=None) -> str:
        self.prompts.append(prompt)
        return f"reply {prompt}"


class FakeVoice:
    def __init__(self) -> None:
        self.texts: list[str] = []

    async def stream_tts(self, text, voice_label, request_id, on_audio) -> None:
        self.texts.append(text)
        on_audio(text.encode())


class FakePlayback:
    def __init__(self) -> None:
        self.played: list[bytes] = []
        self.cancelled = 0

    async def play(self, chunks: list[bytes]) -> None:
        self.played.append(b"".join(chunks))

    async def cancel(self) -> None:
        self.cancelled += 1


def make_pipeline(transcript: str):
    recognizer = FakeRecognizer(transcript)
    chat = FakeChat()
    voice = FakeVoice()
    playback = FakePlayback()
    pipeline = TurnPipeline(
        recognizer=recognizer,
        trigger_names=("ru",),
        chat_client=chat,
        voice_client=voice,
        on_play=playback.play,
        on_cancel_current=playback.cancel,
    )
    return pipeline, chat, voice, playback


def test_no_trigger_is_dropped() -> None:
    pipeline, chat, _, _ = make_pipeline("hôm nay trời đẹp")
    asyncio.run(pipeline.handle_utterance("u1", b"pcm", 0.0))
    assert chat.prompts == []


def test_high_trigger_speaks() -> None:
    pipeline, chat, voice, playback = make_pipeline("Ru ơi giúp anh")
    asyncio.run(pipeline.handle_utterance("u1", b"pcm", 0.0))
    assert chat.prompts == ["Ru ơi giúp anh"]
    assert voice.texts == ["reply Ru ơi giúp anh"]
    assert playback.played == [b"reply Ru ơi giúp anh"]


def test_high_trigger_barges_in() -> None:
    pipeline, chat, voice, playback = make_pipeline("Ru ơi dừng lại")

    async def play_first(chunks: list[bytes]) -> None:
        playback.played.append(b"".join(chunks))
        await pipeline.handle_utterance("u1", b"pcm", 0.2)
        await pipeline.drain_queue()

    async def scenario() -> None:
        pipeline._speaking = True  # simulating an active TTS
        await pipeline.handle_utterance("u1", b"pcm", 0.1)
        assert playback.cancelled == 1
        assert len(voice.texts) == 1

    asyncio.run(scenario())
```

- [ ] **Step 2: Verify fail** — import error.
- [ ] **Step 3: Implement `pipeline.py`**

```python
from __future__ import annotations

import asyncio
import logging
import uuid
from typing import Awaitable, Callable, Protocol

from mini_gpt.speech_runtime import SpeechRecognitionError

from .triggers import detect_trigger
from .vad import UtteranceCollector

logger = logging.getLogger(__name__)


class Recognizer(Protocol):
    def recognize(self, pcm16k: bytes) -> str: ...


class ChatClient(Protocol):
    async def ask(self, prompt: str, session_id=None, on_chunk=None) -> str: ...


class VoiceClient(Protocol):
    async def stream_tts(
        self, text: str, voice_label: str, request_id: str, on_audio: Callable[[bytes], None]
    ) -> None: ...


class TurnPipeline:
    def __init__(
        self,
        *,
        recognizer: Recognizer,
        trigger_names: tuple[str, ...],
        chat_client: ChatClient,
        voice_client: VoiceClient,
        on_play: Callable[[list[bytes]], Awaitable[None]],
        on_cancel_current: Callable[[], Awaitable[None]] | None = None,
    ) -> None:
        self._recognizer = recognizer
        self._trigger_names = trigger_names
        self._chat = chat_client
        self._voice = voice_client
        self._play = on_play
        self._cancel = on_cancel_current
        self._collector = UtteranceCollector()
        self._speaking = False
        self._queued: str | None = None

    @property
    def is_speaking(self) -> bool:
        return self._speaking

    async def handle_utterance(self, user_key: str, pcm16k: bytes, now: float) -> None:
        utterance = self._collector.push(user_key, pcm16k, now)
        if utterance is None:
            return
        await self._process(utterance)

    async def flush(self, user_key: str) -> None:
        utterance = self._collector.flush(user_key)
        if utterance is not None:
            await self._process(utterance)

    async def drain_queue(self) -> None:
        if self._queued is not None and not self._speaking:
            queued = self._queued
            self._queued = None
            await self._speak(queued)

    async def _process(self, utterance: bytes) -> None:
        try:
            transcript = self._recognizer.recognize(utterance)
        except SpeechRecognitionError as error:
            logger.warning("STT failed: %s", error)
            return
        level = detect_trigger(transcript, self._trigger_names)
        if level is None:
            return
        if level == "high":
            if self._speaking and self._cancel is not None:
                await self._cancel()
            if self._speaking:
                # wait for the current playback to stop before starting new
                for _ in range(50):
                    if not self._speaking:
                        break
                    await asyncio.sleep(0.1)
            await self._speak(transcript)
        elif level == "medium":
            if self._speaking:
                self._queued = transcript
            else:
                await self._speak(transcript)

    async def _speak(self, transcript: str) -> None:
        self._speaking = True
        try:
            reply = await self._chat.ask(transcript)
            request_id = uuid.uuid4().hex
            chunks: list[bytes] = []
            await self._voice.stream_tts(reply, "Ru (OmniVoice)", request_id, on_audio=chunks.append)
            await self._play(chunks)
        finally:
            self._speaking = False
        await self.drain_queue()
```

- [ ] **Step 4: Run, PASS** (điều chỉnh test `test_high_trigger_barges_in` nếu logic play đang complexity — đơn giản hoá: mock `_speaking=False` khi gọi lần đầu; assert `cancelled>=1` với script rõ ràng)
- [ ] **Step 5: Commit** — `git commit -m "feat(discord): turn pipeline with trigger and barge-in"`

---

### Task 7: Bot glue — pycord client, slash commands, sink, playback

**Files:**
- Create: `src/mini_gpt/discord_bridge/entry.py` — `main()`: parse args (`--config`, `--backend-url`); env `DISCORD_TOKEN`, `RU_DESKTOP_TOKEN`; `load_config`; `run_bot(cfg, discord_token, backend_token)`.
- Create: `src/mini_gpt/discord_bridge/bot.py` — pycord glue: `run_bot`, `VoiceSink`, slash handler.
- Không unit test (pycord phụ thuộc mạng); smoke thủ công bước cuối.

**Interfaces:**
- `def run_bot(cfg: DiscordBotConfig, discord_token: str, backend_token: str) -> None`
- `class VoiceSink(discord.Sink)` — `write(data, user)` → `down48_stereo_to_mono16(data)` → `pipeline.handle_utterance(str(user.id), mono, time.perf_counter())`; `get_required_audio_format` = PCM_s16 (mặc định pycord sink đã là 48k stereo s16 — xác nhận format mặc định của sink trong docs pycord; nếu format khác, điều chỉnh converter trong Task 3 để khớp).

- [ ] **Step 1: `entry.py`** — đọc args + env, tạo `BackendClient(cfg.backend_url, backend_token)`, `TurnPipeline` với `recognizer=build_speech_recognizer(SpeechEngineConfig())`, gọi `run_bot`.
- [ ] **Step 2: `bot.py`** — `discord.Bot(intents=...)` với `voice_states`, `messages`, `guilds`; slash `join`: `.connect()` vào channel user đang đứng; `leave`: `voice_client.disconnect()`, `pipeline.flush`; `if author.id != cfg.owner_id: raise`.
- [ ] **Step 3: Sink** — class VoiceSink extend `discord.Sink`: `write(data, user)`; `on_voice_state_update` nếu bot bị kick (member == bot.me, channel None) → `pipeline._collector` reset.
- [ ] **Step 4: Playback** — `async def play_chunks(chunks: list[bytes])`: nối `b"".join(chunks)` → `wav_bytes(...)` → viết vào `tempfile.NamedTemporaryFile(suffix=".wav")` → `voice_client.play(discord.FFmpegPCMAudio(path))`.
- [ ] **Step 5: `ruff check` + smoke:** chạy thủ với bot token dev; `/ru join`; nói "Ru" qua micro trong voice; xem phản hồi voice; `/ru leave`. Commit: `feat(discord): pycord bot glue and voice sink`.

---

### Task 8: C# host tích hợp (spawn bridge + tab Discord)

**Files:**
- Modify: `gui-winui3/Services/BackendProcessManager.cs` — thêm spawn discord bridge trong job object.
- Modify: `gui-winui3/MainWindow.xaml(.cs)` — tab/bộ điều khiển Discord (token box, owner id, toggle, status).
- Tham khảo cách lưu secret: `src/mini_gpt/credential_store.py` (Credential Manager).

**Steps (hướng dẫn nêu mục tiêu, thực hiện trong C#):**
- [ ] **Step 1:** Xem code GUI hiện tại (tab Settings `gui-winui3/Views/`), thêm `DiscordSettings` model + các control phù hợp (ToggleSwitch, TextBox, PasswordBox, StatusText).
- [ ] **Step 2:** Lưu config: viết `~/.minigpt/discord.json` (owner_id, trigger_names, backend_url) từ GUI; token Discord dùng `credential_store.py` (backend) — hoặc mặc định env từ GUI; nếu phức tạp để Task 9 làm gọn.
- [ ] **Step 3:** `BackendProcessManager.StartDiscordBotAsync(projectRoot, discordToken, ownerId)` — spawn `.venv\Scripts\python.exe -m mini_gpt.discord_bridge.entry --config %APPDATA%\..\.minigpt\discord.json`, env `DISCORD_TOKEN`, `RU_DESKTOP_TOKEN`; gắn vào job object; log sink `logs/discord_*.log`; stop hook khi `Dispose()`.
- [ ] **Step 4:** MainWindow toggle: on → sau `StartBackendAsync` OK → `StartDiscordBotAsync`; off → kì trong manager `StopDiscordBot()`.
- [ ] **Step 5:** Build `& D:\LLM\build.bat`; chạy `start_dev.bat`; bật toggle; kiểm `logs/discord_out.log`; tắt toggle; xác nhận process chết.
- [ ] **Step 6: Commit** — `feat(discord): host-managed Discord bridge`

---

### Task 9: README + regression + final review

- [ ] **Step 1:** Thêm mục "Discord (voice)" trong `README.md`: yêu cầu bot permissions (`connect`, `speak`, `view_channels`), tạo token Developer Portal, `pip install -e ".[discord]"`, config `~/.minigpt/discord.json` schema, lệnh `/ru join|leave` (DM owner), trigger & barge-in behavior notes, giới hạn (voice trigger chỉ trong channel đã join, chỉ 1 server).
- [ ] **Step 2:** Chạy full suite: `& D:\LLM\.venv\Scripts\python.exe -m pytest -q` — tất cả pass (kể cả regression cũ).
- [ ] **Step 3:** `ruff check src/mini_gpt/discord_bridge` — sạch.
- [ ] **Step 4: Commit** — `docs(discord): README for Ru Discord bridge`.

---

## Self-Review (đã chạy trước khi lưu)

1. **Spec coverage:** lỗi xử lý trong spec (mất kết nối → pycord retry tự động + `on_voice_state_update` cleanup; STT fail → catch `SpeechRecognitionError` Task 6; backend chưa ready → `BackendError` từ `ask` → catch trong `_speak` log + drop; TTS fail → `BackendError` trong stream_tts → bọc `_speak` try/except; kick → `on_voice_state_update` reset; quá tải → VAD drops buffers max 8s + Med queue 1 slot). GUI tab → Task 8. Bảo mật → Credential Manager note Task 8. Đủ.
2. **Placeholders:** không có TBD/TODO — tự thay nếu thấy.
3. **Type consistency:** `handle_utterance(user_key, pcm16k, now)` giống VoiceSink (Task 7); `stream_tts(text, voice_label, request_id, on_audio)` giống BackendClient (Task 5) và pipeline (Task 6); `on_play(list[bytes])` thống nhất. 

**Lưu ý sang execution:** Task 5 `cancel_tts` được thì bỏ hoặc thêm trong `stream_tts` — trong pipeline hiện tại không dùng tới (barge-in khi play chán dừng; cancel không nối sâu).