from __future__ import annotations

import json
import logging
import threading
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path
from uuid import uuid4

from ru_runtime.persistence.atomic_write import atomic_append_jsonl

logger = logging.getLogger(__name__)


@dataclass(frozen=True, slots=True)
class ConversationExample:
    id: str
    created_at_utc: str
    prompt: str
    response: str
    checkpoint: str

    def to_dict(self) -> dict[str, str]:
        return asdict(self)


class ConversationStore:
    def __init__(self, path: str | Path) -> None:
        self.path = Path(path)
        self._lock = threading.Lock()

    def save(self, prompt: str, response: str, checkpoint: str | Path) -> ConversationExample:
        clean_prompt = prompt.strip()
        clean_response = response.strip()
        clean_checkpoint = str(checkpoint).strip()
        if not clean_prompt:
            raise ValueError("prompt cannot be empty")
        if not clean_response:
            raise ValueError("response cannot be empty")
        if not clean_checkpoint:
            raise ValueError("checkpoint cannot be empty")

        record = ConversationExample(
            id=str(uuid4()),
            created_at_utc=datetime.now(timezone.utc).isoformat(),
            prompt=clean_prompt,
            response=clean_response,
            checkpoint=clean_checkpoint,
        )
        line = json.dumps(record.to_dict(), ensure_ascii=False)
        with self._lock:
            atomic_append_jsonl(self.path, line)
        return record

    def read_all(self) -> list[dict[str, str]]:
        with self._lock:
            if not self.path.is_file():
                return []
            records: list[dict[str, str]] = []
            try:
                lines = self.path.read_text(encoding="utf-8").splitlines()
            except FileNotFoundError:
                return []
            for line_number, line in enumerate(lines, 1):
                line = line.strip()
                if not line:
                    continue
                try:
                    records.append(json.loads(line))
                except json.JSONDecodeError as exc:
                    logger.warning(
                        "Skipping malformed JSONL line %d in %s: %s", line_number, self.path, exc
                    )
            return records
