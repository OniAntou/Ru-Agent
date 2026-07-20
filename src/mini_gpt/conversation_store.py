from __future__ import annotations

import json
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path
from uuid import uuid4


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
        self.path.parent.mkdir(parents=True, exist_ok=True)
        with self.path.open("a", encoding="utf-8", newline="\n") as handle:
            handle.write(json.dumps(record.to_dict(), ensure_ascii=False) + "\n")
        return record

    def read_all(self) -> list[dict[str, str]]:
        if not self.path.is_file():
            return []
        return [
            json.loads(line)
            for line in self.path.read_text(encoding="utf-8").splitlines()
            if line.strip()
        ]
