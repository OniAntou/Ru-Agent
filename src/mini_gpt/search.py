from __future__ import annotations

import hashlib
import logging
import queue
import re
import threading

logger = logging.getLogger(__name__)
_SEARCH_TIMEOUT_SECONDS = 15
_MAX_RESULTS = 5
_TITLE_LIMIT = 200
_BODY_LIMIT = 500
_HTML_TAG_PATTERN = re.compile(r"<[^>]+>")
_WHITESPACE_PATTERN = re.compile(r"\s+")


def _sanitize_web_text(text: str, limit: int) -> str:
    """Strip HTML tags and control characters, collapse whitespace, cap length."""
    if not text:
        return ""
    cleaned = _HTML_TAG_PATTERN.sub(" ", text)
    cleaned = "".join(ch for ch in cleaned if ord(ch) >= 32 or ch == "\t")
    cleaned = _WHITESPACE_PATTERN.sub(" ", cleaned).strip()
    return cleaned[:limit]


def perform_web_search(query: str, max_results: int = 3) -> str:
    if not query or not query.strip():
        return "No search query provided."

    try:
        from ddgs import DDGS
    except ImportError:
        return "Online search is unavailable because DDGS is not installed."

    max_results = max(1, min(int(max_results), _MAX_RESULTS))
    query_hash = hashlib.sha256(query.encode()).hexdigest()[:12]
    completed: queue.Queue[tuple[bool, object]] = queue.Queue(maxsize=1)

    def search_worker() -> None:
        try:
            completed.put((True, DDGS().text(query, max_results=max_results)))
        except Exception as error:
            completed.put((False, error))

    worker = threading.Thread(
        target=search_worker,
        name=f"websearch-{query_hash}",
        daemon=True,
    )
    worker.start()
    worker.join(_SEARCH_TIMEOUT_SECONDS)
    if worker.is_alive():
        logger.warning("Web search timed out (hash=%s)", query_hash)
        return "Web search timed out."
    ok, result = completed.get_nowait()
    if not ok:
        logger.warning("Web search error (hash=%s): %s", query_hash, result)
        return "An error occurred during web search."
    results = result

    if not results:
        return "No web results found."
    formatted = [
        f"Result {i}:\nTitle: {_sanitize_web_text(res.get('title', 'No title'), _TITLE_LIMIT)}\n"
        f"Snippet: {_sanitize_web_text(res.get('body', 'No content'), _BODY_LIMIT)}"
        for i, res in enumerate(results, 1)
    ]
    return "\n\n".join(formatted)
