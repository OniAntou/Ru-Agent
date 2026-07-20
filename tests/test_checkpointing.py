import torch

from mini_gpt.checkpointing import load_checkpoint, save_checkpoint
from mini_gpt.config import ModelConfig, TrainingConfig
from mini_gpt.model import MiniGPT


def test_checkpoint_round_trip_restores_model_and_step(tmp_path):
    model_config = ModelConfig.tiny(vocab_size=300)
    training_config = TrainingConfig(total_steps=2, device="cpu", amp=False)
    model = MiniGPT(model_config)
    optimizer = torch.optim.AdamW(model.parameters(), lr=1e-3)
    scheduler = torch.optim.lr_scheduler.LambdaLR(optimizer, lambda _: 1.0)
    original = {key: value.detach().clone() for key, value in model.state_dict().items()}
    path = tmp_path / "checkpoint.pt"

    save_checkpoint(path, model, optimizer, scheduler, 7, 1.25, model_config, training_config)
    with torch.no_grad():
        next(model.parameters()).add_(1.0)
    payload = load_checkpoint(path, model, optimizer, scheduler, device="cpu")

    assert payload["step"] == 7
    for key, value in original.items():
        assert torch.equal(model.state_dict()[key], value)

