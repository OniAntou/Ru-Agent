import logging
import threading
from pathlib import Path
from mini_gpt.headless_runner import BACKEND_KILO, BACKEND_OPENCODE, ModelRunner
from mini_gpt.memory import StructuredMemoryStore, MemoryStore
from mini_gpt.credential_store import CredentialStore

logger = logging.getLogger(__name__)
PROJECT_ROOT = Path(__file__).parent.parent.parent

class MiniGPTCore:
    def __init__(self):
        self.runner = ModelRunner()
        self.memory_index_path = PROJECT_ROOT / "data" / "conversations" / "memory_index.faiss"
        self.memory_texts_path = PROJECT_ROOT / "data" / "conversations" / "memory_texts.json"
        self.memory_db_path = PROJECT_ROOT / "data" / "conversations" / "memory.db"
        self.memory_store = None
        self._memory_store_lock = threading.Lock()

        self.opencode_cred = CredentialStore("mini_gpt", "api_key")
        self.kilo_cred = CredentialStore("mini_gpt", "kilo_gateway_api_key")
        self.gemini_vision_cred = CredentialStore("mini_gpt", "gemini_vision_api_key")

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

    def close(self) -> None:
        with self._memory_store_lock:
            if self.memory_store is not None:
                self.memory_store.close()
                self.memory_store = None
        self.runner.release_gpu()

    def remember(self, prompt: str, reply: str):
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
        share_memory_with_cloud: bool = False,
    ):
        hosted = self.runner.backend in (BACKEND_OPENCODE, BACKEND_KILO)
        context = None
        if not hosted or share_memory_with_cloud:
            store = self.get_memory_store()
            records = store.retrieve(prompt)
            context = store.render_context(records)

        return self.runner.generate_reply(
            prompt=prompt,
            max_new_tokens=max_new_tokens,
            temperature=temperature,
            top_k=top_k,
            memory_context=context,
            allow_web_search=allow_web_search,
            turns_history=turns_history or (),
            on_stream_delta=on_stream_delta,
            on_stream_reset=on_stream_reset,
            vts_instruction=vts_instruction,
        )
