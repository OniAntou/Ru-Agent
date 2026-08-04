import sys
import time
import types

import mini_gpt.search as search


def test_timeout_returns_quickly_and_reuses_a_bounded_pool(monkeypatch) -> None:
    class SlowDDGS:
        def text(self, query, max_results):
            time.sleep(1)
            return []

    monkeypatch.setitem(sys.modules, "ddgs", types.SimpleNamespace(DDGS=SlowDDGS))
    monkeypatch.setattr(search, "_SEARCH_TIMEOUT_SECONDS", 0.01)
    started = time.monotonic()

    assert search.perform_web_search("current news") == "Web search timed out."
    assert time.monotonic() - started < 0.2
    assert search._SEARCH_EXECUTOR is not None
    assert search._SEARCH_EXECUTOR._max_workers == search._SEARCH_EXECUTOR_MAX_WORKERS
