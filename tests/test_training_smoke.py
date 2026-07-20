import numpy as np
import pytest
import torch

from mini_gpt.config import ModelConfig, TrainingConfig
from mini_gpt.data import TokenWindowDataset
from mini_gpt.train import run_training


def test_short_cpu_training_writes_checkpoint(tmp_path):
    tokens_path = tmp_path / "train.bin"
    np.asarray([index % 300 for index in range(80)], dtype=np.int32).tofile(tokens_path)
    train_dataset = TokenWindowDataset(tokens_path, context_length=8)
    validation_dataset = TokenWindowDataset(tokens_path, context_length=8)
    result = run_training(
        ModelConfig.tiny(vocab_size=300),
        TrainingConfig(
            total_steps=2,
            validation_interval=1,
            checkpoint_interval=2,
            log_interval=1,
            max_validation_batches=1,
            gradient_accumulation_steps=1,
            device="cpu",
            amp=False,
        ),
        train_dataset,
        validation_dataset,
        tmp_path / "checkpoints",
    )
    assert result["step"] == 2
    assert (tmp_path / "checkpoints" / "latest.pt").is_file()
    assert (tmp_path / "checkpoints" / "best.pt").is_file()


def test_initial_checkpoint_starts_a_new_local_training_run(tmp_path):
    tokens_path = tmp_path / "train.bin"
    np.asarray([index % 300 for index in range(80)], dtype=np.int32).tofile(tokens_path)
    dataset = TokenWindowDataset(tokens_path, context_length=8)
    model_config = ModelConfig.tiny(vocab_size=300)
    source_config = TrainingConfig(
        total_steps=2,
        validation_interval=1,
        checkpoint_interval=2,
        log_interval=1,
        max_validation_batches=1,
        gradient_accumulation_steps=1,
        device="cpu",
        amp=False,
    )
    source_dir = tmp_path / "source"
    run_training(model_config, source_config, dataset, dataset, source_dir)

    fine_tune_config = TrainingConfig(
        total_steps=1,
        validation_interval=1,
        checkpoint_interval=1,
        log_interval=1,
        max_validation_batches=1,
        gradient_accumulation_steps=1,
        device="cpu",
        amp=False,
    )
    fine_tune_dir = tmp_path / "fine-tune"
    result = run_training(
        model_config,
        fine_tune_config,
        dataset,
        dataset,
        fine_tune_dir,
        initial_checkpoint_path=source_dir / "latest.pt",
    )

    payload = torch.load(fine_tune_dir / "latest.pt", map_location="cpu", weights_only=False)
    assert result["step"] == 1
    assert payload["step"] == 1


def test_resume_and_initial_checkpoint_are_mutually_exclusive(tmp_path):
    tokens_path = tmp_path / "train.bin"
    np.asarray([index % 300 for index in range(80)], dtype=np.int32).tofile(tokens_path)
    dataset = TokenWindowDataset(tokens_path, context_length=8)
    config = TrainingConfig(
        total_steps=1,
        validation_interval=1,
        checkpoint_interval=1,
        log_interval=1,
        max_validation_batches=1,
        gradient_accumulation_steps=1,
        device="cpu",
        amp=False,
    )

    with pytest.raises(ValueError, match="mutually exclusive"):
        run_training(
            ModelConfig.tiny(vocab_size=300),
            config,
            dataset,
            dataset,
            tmp_path / "output",
            resume_path=tmp_path / "resume.pt",
            initial_checkpoint_path=tmp_path / "initial.pt",
        )
