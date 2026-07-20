import json
import sys
from types import SimpleNamespace

from mini_gpt.data_sources import download_wikipedia_sample, normalize_wikipedia_record


def test_wikipedia_record_filtering():
    assert normalize_wikipedia_record({"title": "A", "text": "short"}, "en", min_characters=10) is None
    assert normalize_wikipedia_record({"title": "A", "text": "long enough"}, "en", min_characters=5) == (
        "en",
        "A",
        "long enough",
    )


def test_download_wikipedia_sample_writes_bounded_utf8_files(tmp_path, monkeypatch):
    streams = {
        "20231101.en": [
            {"title": "English", "text": "This is an English article with enough content."},
            {"title": "Too short", "text": "x"},
        ],
        "20231101.vi": [
            {"title": "Tiếng Việt", "text": "Đây là một bài viết tiếng Việt đủ dài để kiểm tra."},
        ],
    }

    def fake_load_dataset(_name, config, split, streaming):
        assert split == "train"
        assert streaming is True
        return streams[config]

    monkeypatch.setitem(sys.modules, "datasets", SimpleNamespace(load_dataset=fake_load_dataset))
    manifest = download_wikipedia_sample(tmp_path / "raw", max_documents_per_language=1, min_characters=10)

    assert manifest["languages"]["en"]["accepted"] == 1
    assert manifest["languages"]["vi"]["accepted"] == 1
    assert "English" in (tmp_path / "raw" / "wikipedia_en.txt").read_text(encoding="utf-8")
    assert "Tiếng Việt" in (tmp_path / "raw" / "wikipedia_vi.txt").read_text(encoding="utf-8")
    json.loads((tmp_path / "raw" / "wikipedia_manifest.json").read_text(encoding="utf-8"))

