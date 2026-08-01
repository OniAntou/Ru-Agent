import sys
import time
import types
import threading

import mini_gpt.search as search


def test_timeout_returns_without_non_daemon_worker(monkeypatch) -> None:
    class SlowDDGS:
        def text(self, query, max_results):
            time.sleep(1)
            return []

    monkeypatch.setitem(sys.modules, "ddgs", types.SimpleNamespace(DDGS=SlowDDGS))
    monkeypatch.setattr(search, "_SEARCH_TIMEOUT_SECONDS", 0.01)
    started = time.monotonic()

    assert search.perform_web_search("current news") == "Web search timed out."
    assert time.monotonic() - started < 0.2
    workers = [thread for thread in threading.enumerate() if thread.name.startswith("websearch")]
    assert workers and all(thread.daemon for thread in workers)
