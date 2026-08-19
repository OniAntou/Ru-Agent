import logging
import os
import threading
from pathlib import Path
from ru_runtime.generation.runner import BACKEND_KILO, BACKEND_OPENCODE, ModelRunner
from ru_runtime.memory.store import StructuredMemoryStore, MemoryStore
from ru_runtime.persistence.credential_store import CredentialStore
from ru_runtime.vts.emotion import MoodTracker, extract_reply_emotion

logger = logging.getLogger(__name__)
PROJECT_ROOT = Path(__file__).resolve().parents[4]
_configured_data_root = os.environ.get("RU_DATA_ROOT", "").strip()
MEMORY_DATA_ROOT = (
    Path(_configured_data_root)
    if _configured_data_root
    else PROJECT_ROOT / "data" / "conversations"
)

class RuRuntimeCore:
    def __init__(self):
        self.runner = ModelRunner()
        self.mood_tracker = MoodTracker()
        self.memory_index_path = MEMORY_DATA_ROOT / "memory_index.faiss"
        self.memory_texts_path = MEMORY_DATA_ROOT / "memory_texts.json"
        self.memory_db_path = MEMORY_DATA_ROOT / "memory.db"
        self.memory_store = None
        self._memory_store_lock = threading.RLock()

        self.opencode_cred = CredentialStore("Ru", "api_key")
        self.kilo_cred = CredentialStore("Ru", "kilo_gateway_api_key")
        self.gemini_vision_cred = CredentialStore("Ru", "gemini_vision_api_key")
        self.custom_cred = CredentialStore("Ru", "custom_api_key")

    def get_memory_store(self):
        with self._memory_store_lock:
            if self.memory_store is None:
                self.memory_db_path.parent.mkdir(parents=True, exist_ok=True)
                self.memory_store = StructuredMemoryStore(self.memory_db_path, index_path=self.memory_index_path)
                try:
                    self.memory_store.migrate_legacy_transcripts(self.memory_texts_path)
                except Exception:
                    # A broken legacy file must never block the backend; it is
                    # skipped and can be re-run later.
                    logger.exception("Legacy transcript migration failed; skipping")
            return self.memory_store

    def clear_memory(self):
        with self._memory_store_lock:
            if self.memory_store is not None:
                self.memory_store.close()
                self.memory_store = None
            MemoryStore.clear_artifacts(self.memory_index_path, self.memory_texts_path)
            for path in (
                self.memory_db_path,
                Path(f"{self.memory_db_path}-wal"),
                Path(f"{self.memory_db_path}-shm"),
            ):
                path.unlink(missing_ok=True)

    def list_memory_records(self):
        with self._memory_store_lock:
            return self.get_memory_store().list_active()

    def close(self) -> None:
        with self._memory_store_lock:
            if self.memory_store is not None:
                self.memory_store.close()
                self.memory_store = None
        self.runner.release_gpu()

    def remember(self, prompt: str, reply: str):
        with self._memory_store_lock:
            try:
                store = self.get_memory_store()
                store.remember_turn(prompt, reply)
            except Exception as error:
                logger.warning("Memory write failed (%s); retrying with a fresh store", error)
                if self.memory_store is not None:
                    try:
                        self.memory_store.close()
                    except Exception:
                        pass
                    self.memory_store = None
                store = self.get_memory_store()
                store.remember_turn(prompt, reply)

    def generate_reply_with_memory(
        self,
        prompt: str,
        max_new_tokens=1024,
        temperature=0.7,
        top_k=50,
        allow_web_search=False,
        turns_history=None,
        on_stream_delta=None,
        on_stream_reset=None,
        vts_instruction: str = "",
        share_memory_with_cloud: bool = True,
    ):
        hosted = self.runner.backend in (BACKEND_OPENCODE, BACKEND_KILO)
        context = None
        if not hosted or share_memory_with_cloud:
            with self._memory_store_lock:
                store = self.get_memory_store()
                records = store.retrieve(prompt)
                context = store.render_context(records)

        self.mood_tracker.observe_user_input(prompt)
        mood_desc = self.mood_tracker.state.describe_for_prompt()

        reply = self.runner.generate_reply(
            prompt=prompt,
            max_new_tokens=max_new_tokens,
            temperature=temperature,
            top_k=top_k,
            memory_context=context,
            mood_context=mood_desc,
            allow_web_search=allow_web_search,
            turns_history=turns_history or (),
            on_stream_delta=on_stream_delta,
            on_stream_reset=on_stream_reset,
            vts_instruction=vts_instruction,
        )

        _, emotion = extract_reply_emotion(reply)
        self.mood_tracker.update_from_reply_emotion(emotion)
        return reply
