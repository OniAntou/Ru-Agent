from __future__ import annotations

import json
import logging
import os
import re
import sqlite3
import threading
import uuid
import weakref
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from enum import StrEnum
from hashlib import sha256
from pathlib import Path
from typing import List

# The production memory path is SQLite/lexical. Keep the legacy compatibility
# class disabled without importing multi-hundred-megabyte embedding stacks at
# backend startup.
faiss = None
SentenceTransformer = None

logger = logging.getLogger(__name__)

_MMAP_SIZE_BYTES = 300_000_000  # 300 MB — đủ cho hàng trăm nghìn dòng memory; 30 GB là vô lý
_USER_PREFIX = "User: "

# retrieve() scoring weights (tổng = 1.0)
_SCORE_WEIGHT_LEXICAL = 0.55
_SCORE_WEIGHT_SCOPE_MATCH = 0.15
_SCORE_WEIGHT_IMPORTANCE = 0.05
_SCORE_WEIGHT_CONFIDENCE = 0.03
_SCORE_WEIGHT_PINNED = 0.02
_RETRIEVE_MIN_SCORE = 0.18
_RETRIEVE_SCAN_LIMIT = 2000
# Memories not touched for this long are not scanned by lexical retrieval;
# they stay stored and become visible again when the user interacts with them.
_RETRIEVE_LOOKBACK_DAYS = 90

_memory_store_registry: weakref.WeakSet[MemoryStore] = weakref.WeakSet()


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
        self._database_lock = threading.RLock()
        self._db_conn = None
        try:
            self._db_conn = sqlite3.connect(self.database_path, check_same_thread=False, timeout=30)
            self._db_conn.row_factory = sqlite3.Row
            self._initialize_schema()
        except sqlite3.DatabaseError:
            # A corrupt or schema-lagging database must never take down the
            # whole backend: quarantine the bad file and rebuild in place.
            logger.exception("Memory database is corrupt; quarantining and rebuilding")
            self._quarantine_database()
            self._db_conn = sqlite3.connect(self.database_path, check_same_thread=False, timeout=30)
            self._db_conn.row_factory = sqlite3.Row
            self._initialize_schema()
        self._purge_expired_locked()

    def _quarantine_database(self) -> None:
        stamped = self.database_path.with_suffix(".db.corrupt")
        try:
            if stamped.is_file():
                stamped.write_bytes(self.database_path.read_bytes())
            else:
                os.replace(self.database_path, stamped)
        except OSError:
            logger.exception("Could not quarantine the corrupt memory database")
        for extra in (
            Path(f"{self.database_path}-wal"),
            Path(f"{self.database_path}-shm"),
            Path(f"{self.database_path}-journal"),
        ):
            extra.unlink(missing_ok=True)

    def _purge_expired_locked(self) -> None:
        """Delete expired, unpinned records on startup so the table cannot
        grow without bound."""
        try:
            with self._database_lock, self._db_conn:
                self._db_conn.execute(
                    "DELETE FROM memories WHERE status = 'active' AND pinned = 0 AND expires_at IS NOT NULL AND expires_at < ?",
                    (_serialize_datetime(_utc_now()),),
                )
        except sqlite3.Error:
            logger.exception("Could not purge expired memories")

    def _initialize_schema(self) -> None:
        with self._database_lock, self._db_conn:
            self._db_conn.executescript(
                f"""
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA mmap_size = {_MMAP_SIZE_BYTES};
                PRAGMA temp_store = MEMORY;
                
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
                CREATE INDEX IF NOT EXISTS idx_memories_status_expires ON memories (status, expires_at);
                CREATE TABLE IF NOT EXISTS legacy_memory_migrations (
                    source_hash TEXT PRIMARY KEY
                );
                """
            )

    def _require_open(self) -> None:
        if self._db_conn is None:
            raise RuntimeError("StructuredMemoryStore is closed")

    def vacuum_database(self) -> None:
        """Run VACUUM to defragment the database and reclaim disk space."""
        self._require_open()
        with self._database_lock:
            # VACUUM cannot be run inside a transaction.
            self._db_conn.isolation_level = None
            try:
                self._db_conn.execute("VACUUM")
            finally:
                self._db_conn.isolation_level = ""

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
        self._require_open()
        with self._database_lock:
            row = self._db_conn.execute("SELECT * FROM memories WHERE id = ?", (record_id,)).fetchone()
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
        self._require_open()
        
        if not content or not evidence:
            raise ValueError("memory content and evidence cannot be blank")
        if len(content) > 100000 or len(evidence) > 100000:
            raise ValueError("memory content and evidence cannot exceed 100,000 characters")
            
        if not 0 <= confidence <= 1 or not 0 <= importance <= 1:
            raise ValueError("memory confidence and importance must be between 0 and 1")

        now = _utc_now()
        record = self._build_record(
            kind, content, scope, evidence, confidence, importance, expires_at, now
        )
        with self._database_lock, self._db_conn:
            stored_id = self._upsert_locked(record, supersedes_id, now)
            if stored_id != record.id:
                # Content already stored: return the existing active record.
                row = self._db_conn.execute("SELECT * FROM memories WHERE id = ?", (stored_id,)).fetchone()
                if row is not None:
                    return self._record_from_row(row)
        return record

    @staticmethod
    def _build_record(
        kind: MemoryKind,
        content: str,
        scope: str | None,
        evidence: str,
        confidence: float,
        importance: float,
        expires_at: datetime | None,
        now: datetime,
    ) -> MemoryRecord:
        return MemoryRecord(
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

    def _upsert_locked(self, record: MemoryRecord, supersedes_id: str | None, now: datetime) -> str:
        """Insert *record*; caller must hold _database_lock with an open transaction.
        Returns the id of the record that is actually stored: the new one, or the
        existing active duplicate when the content already exists."""
        duplicate = None
        if supersedes_id is None:
            duplicate = self._db_conn.execute(
                """
                SELECT id FROM memories
                WHERE status = 'active'
                  AND kind = ? AND content = ? COLLATE NOCASE
                  AND (expires_at IS NULL OR expires_at > ?)
                  AND (scope IS ? OR scope = ?)
                LIMIT 1
                """,
                (
                    record.kind.value,
                    record.content.strip(),
                    _serialize_datetime(now),
                    None if record.scope is None else record.scope,
                    record.scope,
                ),
            ).fetchone()
        if duplicate is not None:
            return str(duplicate["id"])
        if supersedes_id is not None:
            row = self._db_conn.execute(
                "SELECT status FROM memories WHERE id = ?", (supersedes_id,)
            ).fetchone()
            if row is None or row["status"] != "active":
                raise ValueError("superseded memory does not exist or is not active")
            self._db_conn.execute(
                "UPDATE memories SET status = 'superseded', updated_at = ? WHERE id = ?",
                (_serialize_datetime(now), supersedes_id),
            )
        self._db_conn.execute(
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
        return record.id

    def count_active(self) -> int:
        self._require_open()
        now = _serialize_datetime(_utc_now())
        with self._database_lock:
            row = self._db_conn.execute(
                """
                SELECT COUNT(*) AS count FROM memories
                WHERE status = 'active' AND (expires_at IS NULL OR expires_at > ? OR pinned = 1)
                """,
                (now,),
            ).fetchone()
        return int(row["count"])

    def list_active(self, query: str = "") -> tuple[MemoryRecord, ...]:
        self._require_open()
        now = _serialize_datetime(_utc_now())
        with self._database_lock:
            rows = self._db_conn.execute(
                """
                SELECT * FROM memories
                WHERE status = 'active' AND (expires_at IS NULL OR expires_at > ? OR pinned = 1)
                ORDER BY pinned DESC, updated_at DESC
                """,
                (now,),
            ).fetchall()
        records = tuple(self._record_from_row(row) for row in rows)
        needle = query.strip().casefold()
        if not needle:
            return records
        return tuple(record for record in records if needle in f"{record.content} {record.scope or ''}".casefold())

    @staticmethod
    def _tokens(text: str) -> set[str]:
        return set(re.findall(r"\w+", text.casefold()))

    def retrieve(self, query: str, *, limit: int = 5, max_chars: int = 1200) -> tuple[MemoryRecord, ...]:
        self._require_open()
        query_tokens = self._tokens(query)
        if not query_tokens:
            return ()
        from ru_runtime.memory.extraction import _PATH_PATTERN
        query_scopes = re.findall(_PATH_PATTERN.pattern, query)
        ranked: list[tuple[float, MemoryRecord]] = []
        now = _serialize_datetime(_utc_now())
        lookback = _serialize_datetime(_utc_now() - timedelta(days=_RETRIEVE_LOOKBACK_DAYS))
        # Avoid a full table scan every turn: score pinned memories plus the
        # most recently updated candidates within a lookback window.
        with self._database_lock:
            rows = self._db_conn.execute(
                """
                SELECT * FROM memories
                WHERE status = 'active'
                  AND (expires_at IS NULL OR expires_at > ? OR pinned = 1)
                  AND (pinned = 1 OR updated_at >= ?)
                ORDER BY pinned DESC, updated_at DESC
                LIMIT ?
                """,
                (now, lookback, _RETRIEVE_SCAN_LIMIT),
            ).fetchall()
        records = tuple(self._record_from_row(row) for row in rows)
        for record in records:
            scope_match = bool(record.scope and any(scope.casefold() == record.scope.casefold() for scope in query_scopes))
            if query_scopes and record.scope and not scope_match:
                continue
            overlap = len(query_tokens & self._tokens(f"{record.content} {record.scope or ''}"))
            lexical_score = overlap / len(query_tokens)
            score = (
                _SCORE_WEIGHT_LEXICAL * lexical_score
                + _SCORE_WEIGHT_SCOPE_MATCH * float(scope_match)
                + _SCORE_WEIGHT_IMPORTANCE * record.importance
                + _SCORE_WEIGHT_CONFIDENCE * record.confidence
                + _SCORE_WEIGHT_PINNED * float(record.pinned)
            )
            if score >= _RETRIEVE_MIN_SCORE:
                ranked.append((score, record))
        ranked.sort(key=lambda item: (item[0], item[1].updated_at), reverse=True)
        return self._fit_records(
            [record for _, record in ranked], max_chars, limit=limit
        )

    @staticmethod
    def _render_record(record: MemoryRecord) -> str:
        return f"- [{record.kind.value} | {record.scope or 'global'}] {record.content}"

    @staticmethod
    def _fit_records(
        records: list[MemoryRecord],
        max_chars: int,
        *,
        limit: int | None = None,
    ) -> tuple[MemoryRecord, ...]:
        fitted: list[MemoryRecord] = []
        used_chars = 0
        for record in records:
            if limit is not None and len(fitted) >= limit:
                break
            line = StructuredMemoryStore._render_record(record)
            if used_chars + len(line) + (1 if fitted else 0) > max_chars:
                break
            fitted.append(record)
            used_chars += len(line) + (1 if len(fitted) > 1 else 0)
        return tuple(fitted)

    def render_context(self, records: tuple[MemoryRecord, ...], *, max_chars: int = 1200) -> str:
        fitted = self._fit_records(list(records), max_chars)
        return "\n".join(self._render_record(record) for record in fitted)

    def remember_turn(self, prompt: str, response: str) -> tuple[MemoryRecord, ...]:
        from ru_runtime.memory.extraction import extract_candidates

        candidates = extract_candidates(prompt, response)
        if not candidates:
            return ()
        self._require_open()
        now = _utc_now()
        records: list[MemoryRecord] = []
        with self._database_lock, self._db_conn:
            for candidate in candidates:
                record = self._build_record(
                    candidate.kind,
                    candidate.content,
                    candidate.scope,
                    candidate.evidence,
                    candidate.confidence,
                    candidate.importance,
                    None,
                    now,
                )
                stored_id = self._upsert_locked(record, None, now)
                if stored_id == record.id:
                    records.append(record)
        return tuple(records)

    def clear(self) -> None:
        self._require_open()
        with self._database_lock, self._db_conn:
            self._db_conn.execute("DELETE FROM memories")
            self._db_conn.execute("DELETE FROM legacy_memory_migrations")
        stores = list(_memory_store_registry)
        if stores:
            for store in stores:
                store.clear()
        elif self.index_path.is_file():
            self.index_path.unlink()

    def close(self) -> None:
        with self._database_lock:
            if self._db_conn is not None:
                self._db_conn.close()
                self._db_conn = None

    def migrate_legacy_transcripts(self, legacy_texts_path: str | Path) -> dict[str, int]:
        from ru_runtime.memory.extraction import extract_candidates

        legacy_path = Path(legacy_texts_path)
        self._require_open()
        if not legacy_path.is_file():
            return {"imported": 0, "rejected": 0}
        source = legacy_path.read_bytes()
        source_hash = sha256(source).hexdigest()
        try:
            blocks = json.loads(source.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            return {"imported": 0, "rejected": 1}

        imported = 0
        rejected = 0
        now = _utc_now()
        with self._database_lock, self._db_conn:
            already_imported = self._db_conn.execute(
                "SELECT 1 FROM legacy_memory_migrations WHERE source_hash = ?", (source_hash,)
            ).fetchone()
            if already_imported is not None:
                return {"imported": 0, "rejected": 0}
            for block in blocks if isinstance(blocks, list) else []:
                if (
                    not isinstance(block, str)
                    or "\nAssistant:" not in block
                    or not block.startswith(_USER_PREFIX)
                ):
                    rejected += 1
                    continue
                prompt, response = block[len(_USER_PREFIX):].split("\nAssistant:", 1)
                candidates = extract_candidates(prompt, response)
                if not candidates:
                    rejected += 1
                    continue
                for candidate in candidates:
                    record = self._build_record(
                        candidate.kind,
                        candidate.content,
                        candidate.scope,
                        candidate.evidence,
                        candidate.confidence,
                        candidate.importance,
                        None,
                        now,
                    )
                    stored_id = self._upsert_locked(record, None, now)
                    if stored_id == record.id:
                        imported += 1
            self._db_conn.execute(
                "INSERT OR IGNORE INTO legacy_memory_migrations (source_hash) VALUES (?)", (source_hash,)
            )
        return {"imported": imported, "rejected": rejected}


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
        self.model = None
        self.index = None
        self._lock = threading.RLock()

        if faiss is None or SentenceTransformer is None:
            logger.warning("faiss-cpu or sentence-transformers is not installed. MemoryStore disabled.")
            return

        self.index_path.parent.mkdir(parents=True, exist_ok=True)
        logger.info("Loading sentence-transformers/all-MiniLM-L6-v2 model (CPU)...")
        try:
            self.model = SentenceTransformer("all-MiniLM-L6-v2", device="cpu")
        except Exception as exc:
            logger.warning("Failed to load embedding model, MemoryStore disabled: %s", exc)
            return
        self.d = self.model.get_embedding_dimension()
        if self.texts_path.is_file():
            try:
                with open(self.texts_path, "r", encoding="utf-8") as file:
                    loaded = json.load(file)
                if isinstance(loaded, list):
                    self.texts = [str(item) for item in loaded]
                else:
                    logger.warning("memory_texts.json is not a list, resetting index")
                    self.texts = []
            except (json.JSONDecodeError, OSError) as exc:
                logger.warning("Failed to load memory texts, resetting index: %s", exc)
                self.texts = []
        if self.index_path.is_file():
            try:
                self.index = faiss.read_index(str(self.index_path))
                if self.index.ntotal != len(self.texts):
                    logger.warning("FAISS index size mismatch with texts list. Rebuilding index...")
                    self.index = None
            except Exception as exc:
                logger.warning("Failed to load FAISS index (%s), rebuilding: %s", self.index_path, exc)
                self.index = None
            if self.index is None:
                self._rebuild_index()
        else:
            self._rebuild_index()
        _memory_store_registry.add(self)

    def _rebuild_index(self) -> None:
        self.index = faiss.IndexFlatL2(self.d)
        if self.texts:
            self.index.add(self.model.encode(self.texts, convert_to_numpy=True))
        self._save()

    def _save(self) -> None:
        if self.index is not None:
            tmp_index = self.index_path.with_name(self.index_path.name + ".tmp")
            faiss.write_index(self.index, str(tmp_index))
            os.replace(tmp_index, self.index_path)
        tmp_texts = self.texts_path.with_name(self.texts_path.name + ".tmp")
        with open(tmp_texts, "w", encoding="utf-8") as file:
            json.dump(self.texts, file, ensure_ascii=False, indent=2)
        os.replace(tmp_texts, self.texts_path)

    def add_memory(self, text: str) -> None:
        if self.model is None or self.index is None:
            return
        with self._lock:
            text = text.strip()
            if not text or text in self.texts:
                return
            backup = faiss.clone_index(self.index)
            self.index.add(self.model.encode([text], convert_to_numpy=True))
            self.texts.append(text)
            try:
                self._save()
            except Exception:
                self.index = backup
                self.texts.pop()
                raise
            logger.info("Saved memory block (Total: %d)", len(self.texts))

    def search_memory(self, query: str, k: int = 2) -> List[str]:
        if self.model is None or self.index is None or self.index.ntotal == 0:
            return []
        with self._lock:
            _, indices = self.index.search(
                self.model.encode([query], convert_to_numpy=True), min(k, self.index.ntotal)
            )
            return [self.texts[index] for index in indices[0] if 0 <= index < len(self.texts)]

    def clear(self) -> None:
        with self._lock:
            self.texts = []
            if self.index is not None:
                self.index.reset()
                self._save()
            else:
                self.clear_artifacts(self.index_path, self.texts_path)

    def close(self) -> None:
        _memory_store_registry.discard(self)
        with self._lock:
            self.model = None
            self.index = None
