import numpy as np

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

