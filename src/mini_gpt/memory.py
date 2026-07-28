from __future__ import annotations

import json
import logging
import sqlite3
import uuid
from dataclasses import dataclass
from datetime import UTC, datetime
from enum import StrEnum
from pathlib import Path
from typing import List

try:
    import faiss
    from sentence_transformers import SentenceTransformer
except ImportError:
    faiss = None
    SentenceTransformer = None

logger = logging.getLogger(__name__)


class MemoryKind(StrEnum):
    PROFILE = "profile"
    PREFERENCE = "preference"
    PROJECT = "project"
    DECISION = "decision"
    SESSION_SUMMARY = "session_summary"


@dataclass(frozen=True, slots=True)
class MemoryRecord:
    id: str
    kind: MemoryKind
    content: str
    scope: str | None
    evidence: str
    confidence: float
    importance: float
    created_at: datetime
    updated_at: datetime
    accessed_at: datetime | None
    expires_at: datetime | None
    status: str
    pinned: bool


def _utc_now() -> datetime:
    return datetime.now(UTC)


def _serialize_datetime(value: datetime | None) -> str | None:
    if value is None:
        return None
    if value.tzinfo is None:
        value = value.replace(tzinfo=UTC)
    return value.astimezone(UTC).isoformat()


def _parse_datetime(value: str | None) -> datetime | None:
    return datetime.fromisoformat(value) if value else None


class StructuredMemoryStore:
    def __init__(self, database_path: str | Path, *, index_path: str | Path):
        self.database_path = Path(database_path)
        self.index_path = Path(index_path)
        self.database_path.parent.mkdir(parents=True, exist_ok=True)
        self._connection = sqlite3.connect(self.database_path)
        self._connection.row_factory = sqlite3.Row
        self._initialize_schema()

    def _initialize_schema(self) -> None:
        with self._connection:
            self._connection.executescript(
                """
                CREATE TABLE IF NOT EXISTS memories (
                    id TEXT PRIMARY KEY,
                    kind TEXT NOT NULL CHECK(kind IN ('profile', 'preference', 'project', 'decision', 'session_summary')),
                    content TEXT NOT NULL,
                    scope TEXT,
                    evidence TEXT NOT NULL,
                    confidence REAL NOT NULL CHECK(confidence >= 0 AND confidence <= 1),
                    importance REAL NOT NULL CHECK(importance >= 0 AND importance <= 1),
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    accessed_at TEXT,
                    expires_at TEXT,
                    status TEXT NOT NULL CHECK(status IN ('active', 'superseded', 'deleted')),
                    pinned INTEGER NOT NULL DEFAULT 0
                );
                CREATE VIRTUAL TABLE IF NOT EXISTS memories_fts USING fts5(id UNINDEXED, content, scope);
                CREATE INDEX IF NOT EXISTS idx_memories_active_scope ON memories(status, kind, scope);
                """
            )

    @staticmethod
    def _record_from_row(row: sqlite3.Row) -> MemoryRecord:
        return MemoryRecord(
            id=row["id"],
            kind=MemoryKind(row["kind"]),
            content=row["content"],
            scope=row["scope"],
            evidence=row["evidence"],
            confidence=row["confidence"],
            importance=row["importance"],
            created_at=_parse_datetime(row["created_at"]),
            updated_at=_parse_datetime(row["updated_at"]),
            accessed_at=_parse_datetime(row["accessed_at"]),
            expires_at=_parse_datetime(row["expires_at"]),
            status=row["status"],
            pinned=bool(row["pinned"]),
        )

    def get(self, record_id: str) -> MemoryRecord | None:
        row = self._connection.execute("SELECT * FROM memories WHERE id = ?", (record_id,)).fetchone()
        return self._record_from_row(row) if row is not None else None

    def upsert(
        self,
        kind: MemoryKind,
        content: str,
        scope: str | None,
        evidence: str,
        confidence: float,
        importance: float,
        *,
        expires_at: datetime | None = None,
        supersedes_id: str | None = None,
    ) -> MemoryRecord:
        content = content.strip()
        evidence = evidence.strip()
        if not content or not evidence:
            raise ValueError("memory content and evidence cannot be blank")
        if not 0 <= confidence <= 1 or not 0 <= importance <= 1:
            raise ValueError("memory confidence and importance must be between 0 and 1")
        if supersedes_id is not None and self.get(supersedes_id) is None:
            raise ValueError("superseded memory does not exist")

        now = _utc_now()
        record = MemoryRecord(
            id=str(uuid.uuid4()),
            kind=kind,
            content=content,
            scope=scope.strip() if scope else None,
            evidence=evidence,
            confidence=confidence,
            importance=importance,
            created_at=now,
            updated_at=now,
            accessed_at=None,
            expires_at=expires_at,
            status="active",
            pinned=False,
        )
        with self._connection:
            if supersedes_id is not None:
                self._connection.execute(
                    "UPDATE memories SET status = 'superseded', updated_at = ? WHERE id = ?",
                    (_serialize_datetime(now), supersedes_id),
                )
            self._connection.execute(
                """
                INSERT INTO memories (
                    id, kind, content, scope, evidence, confidence, importance,
                    created_at, updated_at, accessed_at, expires_at, status, pinned
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                """,
                (
                    record.id,
                    record.kind.value,
                    record.content,
                    record.scope,
                    record.evidence,
                    record.confidence,
                    record.importance,
                    _serialize_datetime(record.created_at),
                    _serialize_datetime(record.updated_at),
                    None,
                    _serialize_datetime(record.expires_at),
                    record.status,
                    0,
                ),
            )
            self._connection.execute(
                "INSERT INTO memories_fts (id, content, scope) VALUES (?, ?, ?)",
                (record.id, record.content, record.scope or ""),
            )
        return record

    def count_active(self) -> int:
        now = _serialize_datetime(_utc_now())
        row = self._connection.execute(
            """
            SELECT COUNT(*) AS count FROM memories
            WHERE status = 'active' AND (expires_at IS NULL OR expires_at > ? OR pinned = 1)
            """,
            (now,),
        ).fetchone()
        return int(row["count"])


class MemoryStore:
    @staticmethod
    def clear_artifacts(index_path: str | Path, texts_path: str | Path) -> None:
        for path in (Path(index_path), Path(texts_path)):
            if path.is_file():
                path.unlink()

    def __init__(self, index_path: str | Path, texts_path: str | Path):
        self.index_path = Path(index_path)
        self.texts_path = Path(texts_path)
        self.texts: List[str] = []

        if faiss is None or SentenceTransformer is None:
            logger.warning("faiss-cpu or sentence-transformers is not installed. MemoryStore disabled.")
            self.model = None
            self.index = None
            return

        self.index_path.parent.mkdir(parents=True, exist_ok=True)
        logger.info("Loading sentence-transformers/all-MiniLM-L6-v2 model (CPU)...")
        self.model = SentenceTransformer("all-MiniLM-L6-v2", device="cpu")
        self.d = self.model.get_embedding_dimension()
        if self.texts_path.is_file():
            with open(self.texts_path, "r", encoding="utf-8") as file:
                self.texts = json.load(file)
        if self.index_path.is_file():
            self.index = faiss.read_index(str(self.index_path))
            if self.index.ntotal != len(self.texts):
                logger.warning("FAISS index size mismatch with texts list. Rebuilding index...")
                self._rebuild_index()
        else:
            self._rebuild_index()

    def _rebuild_index(self) -> None:
        self.index = faiss.IndexFlatL2(self.d)
        if self.texts:
            self.index.add(self.model.encode(self.texts, convert_to_numpy=True))
        self._save()

    def _save(self) -> None:
        if self.index is not None:
            faiss.write_index(self.index, str(self.index_path))
        with open(self.texts_path, "w", encoding="utf-8") as file:
            json.dump(self.texts, file, ensure_ascii=False, indent=2)

    def add_memory(self, text: str) -> None:
        if self.model is None or self.index is None:
            return
        text = text.strip()
        if not text:
            return
        self.index.add(self.model.encode([text], convert_to_numpy=True))
        self.texts.append(text)
        self._save()
        logger.info("Saved memory block (Total: %d)", len(self.texts))

    def search_memory(self, query: str, k: int = 2) -> List[str]:
        if self.model is None or self.index is None or self.index.ntotal == 0:
            return []
        _, indices = self.index.search(
            self.model.encode([query], convert_to_numpy=True), min(k, self.index.ntotal)
        )
        return [self.texts[index] for index in indices[0] if 0 <= index < len(self.texts)]
