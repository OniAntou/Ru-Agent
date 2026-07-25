import json
import logging
from pathlib import Path
from typing import List

try:
    import faiss
    from sentence_transformers import SentenceTransformer
except ImportError:
    faiss = None
    SentenceTransformer = None

logger = logging.getLogger(__name__)

class MemoryStore:
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

        # Load texts
        if self.texts_path.is_file():
            with open(self.texts_path, "r", encoding="utf-8") as f:
                self.texts = json.load(f)
        
        # Load or create index
        if self.index_path.is_file():
            self.index = faiss.read_index(str(self.index_path))
            if self.index.ntotal != len(self.texts):
                logger.warning("FAISS index size mismatch with texts list. Rebuilding index...")
                self._rebuild_index()
        else:
            self._rebuild_index()

    def _rebuild_index(self):
        self.index = faiss.IndexFlatL2(self.d)
        if self.texts:
            embeddings = self.model.encode(self.texts, convert_to_numpy=True)
            self.index.add(embeddings)
        self._save()

    def _save(self):
        if self.index is not None:
            faiss.write_index(self.index, str(self.index_path))
        with open(self.texts_path, "w", encoding="utf-8") as f:
            json.dump(self.texts, f, ensure_ascii=False, indent=2)

    def add_memory(self, text: str):
        if self.model is None or self.index is None:
            return
        
        text = text.strip()
        if not text:
            return
            
        embedding = self.model.encode([text], convert_to_numpy=True)
        self.index.add(embedding)
        self.texts.append(text)
        self._save()
        logger.info(f"Saved memory block (Total: {len(self.texts)})")

    def search_memory(self, query: str, k: int = 2) -> List[str]:
        if self.model is None or self.index is None or self.index.ntotal == 0:
            return []
            
        query_embedding = self.model.encode([query], convert_to_numpy=True)
        distances, indices = self.index.search(query_embedding, min(k, self.index.ntotal))
        
        results = []
        for idx in indices[0]:
            if idx != -1 and idx < len(self.texts):
                results.append(self.texts[idx])
        return results
