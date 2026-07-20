from __future__ import annotations

import torch
from torch import nn
from torch.nn import functional as F

from .config import ModelConfig


class CausalSelfAttention(nn.Module):
    def __init__(self, config: ModelConfig) -> None:
        super().__init__()
        self.n_heads = config.n_heads
        self.head_dim = config.d_model // config.n_heads
        self.dropout = config.dropout
        self.qkv = nn.Linear(config.d_model, 3 * config.d_model)
        self.output = nn.Linear(config.d_model, config.d_model)

    def forward(self, hidden_states: torch.Tensor) -> torch.Tensor:
        batch_size, sequence_length, width = hidden_states.shape
        qkv = self.qkv(hidden_states).view(batch_size, sequence_length, 3, self.n_heads, self.head_dim)
        query, key, value = qkv.unbind(dim=2)
        query = query.transpose(1, 2)
        key = key.transpose(1, 2)
        value = value.transpose(1, 2)
        attended = F.scaled_dot_product_attention(
            query,
            key,
            value,
            dropout_p=self.dropout if self.training else 0.0,
            is_causal=True,
        )
        attended = attended.transpose(1, 2).contiguous().view(batch_size, sequence_length, width)
        return self.output(attended)


class MLP(nn.Module):
    def __init__(self, config: ModelConfig) -> None:
        super().__init__()
        self.input = nn.Linear(config.d_model, config.d_ff)
        self.output = nn.Linear(config.d_ff, config.d_model)
        self.dropout = nn.Dropout(config.dropout)

    def forward(self, hidden_states: torch.Tensor) -> torch.Tensor:
        return self.dropout(self.output(F.gelu(self.input(hidden_states))))


class TransformerBlock(nn.Module):
    def __init__(self, config: ModelConfig) -> None:
        super().__init__()
        self.norm_attention = nn.LayerNorm(config.d_model)
        self.attention = CausalSelfAttention(config)
        self.norm_mlp = nn.LayerNorm(config.d_model)
        self.mlp = MLP(config)

    def forward(self, hidden_states: torch.Tensor) -> torch.Tensor:
        hidden_states = hidden_states + self.attention(self.norm_attention(hidden_states))
        hidden_states = hidden_states + self.mlp(self.norm_mlp(hidden_states))
        return hidden_states


class MiniGPT(nn.Module):
    def __init__(self, config: ModelConfig) -> None:
        super().__init__()
        self.config = config
        self.token_embedding = nn.Embedding(config.vocab_size, config.d_model)
        self.position_embedding = nn.Embedding(config.context_length, config.d_model)
        self.blocks = nn.ModuleList(TransformerBlock(config) for _ in range(config.n_layers))
        self.final_norm = nn.LayerNorm(config.d_model)
        self.lm_head = nn.Linear(config.d_model, config.vocab_size, bias=False)
        if config.tie_embeddings:
            self.lm_head.weight = self.token_embedding.weight
        self.apply(self._initialize_weights)

    @staticmethod
    def _initialize_weights(module: nn.Module) -> None:
        if isinstance(module, (nn.Linear, nn.Embedding)):
            nn.init.normal_(module.weight, mean=0.0, std=0.02)
            if isinstance(module, nn.Linear) and module.bias is not None:
                nn.init.zeros_(module.bias)
        elif isinstance(module, nn.LayerNorm):
            nn.init.ones_(module.weight)
            nn.init.zeros_(module.bias)

    def forward(
        self,
        input_ids: torch.Tensor,
        targets: torch.Tensor | None = None,
    ) -> tuple[torch.Tensor, torch.Tensor | None]:
        _, sequence_length = input_ids.shape
        if sequence_length > self.config.context_length:
            raise ValueError("input sequence exceeds configured context_length")
        positions = torch.arange(sequence_length, device=input_ids.device).unsqueeze(0)
        hidden_states = self.token_embedding(input_ids) + self.position_embedding(positions)
        for block in self.blocks:
            hidden_states = block(hidden_states)
        logits = self.lm_head(self.final_norm(hidden_states))
        loss = None
        if targets is not None:
            loss = F.cross_entropy(logits.reshape(-1, logits.size(-1)), targets.reshape(-1))
        return logits, loss

    def parameter_count(self, trainable_only: bool = True) -> int:
        parameters = self.parameters()
        if trainable_only:
            return sum(parameter.numel() for parameter in parameters if parameter.requires_grad)
        return sum(parameter.numel() for parameter in self.parameters())

