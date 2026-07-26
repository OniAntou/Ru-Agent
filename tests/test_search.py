import builtins

from mini_gpt import search


def test_search_reports_missing_optional_dependency_without_request(monkeypatch):
    original_import = builtins.__import__

    def reject_ddgs(name, *args, **kwargs):
        if name == "ddgs":
            raise ImportError("DDGS unavailable")
        return original_import(name, *args, **kwargs)

    monkeypatch.setattr(search, "DDGS", None, raising=False)
    monkeypatch.setattr(builtins, "__import__", reject_ddgs)

    assert search.perform_web_search("weather") == (
        "Online search is unavailable because DDGS is not installed."
    )
