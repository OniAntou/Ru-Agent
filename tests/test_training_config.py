from pathlib import Path

from mini_gpt.llama3_1_training import LlamaTrainingConfig, training_dataset_fingerprint


def test_v6_3_training_config_is_parseable_and_relative() -> None:
    root = Path(__file__).parents[1]
    config = LlamaTrainingConfig.from_json(root / "configs" / "ru_llama3_1_v6_3.json")

    assert config.training_status == "not_started"
    assert not Path(config.base_model_path).is_absolute()
    assert not Path(config.dataset_dir).is_absolute()
    assert not Path(config.adapter_dir).is_absolute()


def test_training_dataset_fingerprint_covers_every_input_file(tmp_path: Path) -> None:
    for name, content in (
        ("train.jsonl", '{"row": 1}\n'),
        ("val.jsonl", '{"row": 2}\n'),
        ("manifest.json", '{"version": "v1"}\n'),
    ):
        (tmp_path / name).write_text(content, encoding="utf-8")

    first = training_dataset_fingerprint(tmp_path)
    (tmp_path / "train.jsonl").write_text('{"row": 9}\n', encoding="utf-8")
    second = training_dataset_fingerprint(tmp_path)

    assert set(first["files"]) == {"train.jsonl", "val.jsonl", "manifest.json"}
    assert first["aggregate_sha256"] != second["aggregate_sha256"]
