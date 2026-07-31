from __future__ import annotations

import concurrent.futures
import hashlib
import logging
import re

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
    # One executor per call so a hung DDGS request cannot permanently consume
    # a shared pool; the worker thread is left to finish in the background.
    executor = concurrent.futures.ThreadPoolExecutor(max_workers=1, thread_name_prefix="websearch")
    try:
        future = executor.submit(lambda: DDGS().text(query, max_results=max_results))
        results = future.result(timeout=_SEARCH_TIMEOUT_SECONDS)
    except concurrent.futures.TimeoutError:
        logger.warning("Web search timed out (hash=%s)", query_hash)
        return "Web search timed out."
    except Exception as exc:
        logger.warning("Web search error (hash=%s): %s", query_hash, exc)
        return "An error occurred during web search."
    finally:
        executor.shutdown(wait=False, cancel_futures=True)

    if not results:
        return "No web results found."
    formatted = [
        f"Result {i}:\nTitle: {_sanitize_web_text(res.get('title', 'No title'), _TITLE_LIMIT)}\n"
        f"Snippet: {_sanitize_web_text(res.get('body', 'No content'), _BODY_LIMIT)}"
        for i, res in enumerate(results, 1)
    ]
    return "\n\n".join(formatted)
