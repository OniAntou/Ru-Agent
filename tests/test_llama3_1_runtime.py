import pytest

from mini_gpt import llama3_1_runtime as runtime


class FakeTensor:
    shape = (1, 2)

    def to(self, _device):
        return self

    def new_ones(self, _shape):
        return self


class FakeInput(dict):
    def __init__(self):
        super().__init__({"input_ids": FakeTensor(), "attention_mask": FakeTensor()})

    def to(self, _device):
        return self


class FakeTokenizer:
    def apply_chat_template(self, messages, **_kwargs):
        self.messages = messages
        return FakeInput()

    def decode(self, token_ids, skip_special_tokens=True):
        assert skip_special_tokens is True
        assert token_ids == [7]
        return "reply"


class FakeModel:
    def __init__(self):
        self.kwargs = None

    def generate(self, **kwargs):
        self.kwargs = kwargs
        return [[1, 2, 7]]


def test_generate_llama_response_passes_interactive_generation_settings():
    base_path = "fake-base"
    tokenizer = FakeTokenizer()
    model = FakeModel()
    runtime._GENERATION_CACHE.clear()
    runtime._GENERATION_CACHE[(base_path, None)] = (tokenizer, model)

    result = runtime.generate_llama_response_hf(
        base_path,
        (("system", "system"), ("user", "hello")),
        max_new_tokens=48,
        temperature=0.8,
        top_k=12,
        do_sample=True,
    )

    assert result == "reply"
    assert model.kwargs["max_new_tokens"] == 48
    assert model.kwargs["temperature"] == 0.8
    assert model.kwargs["top_k"] == 12
    assert model.kwargs["do_sample"] is True
    assert model.kwargs["repetition_penalty"] == 1.15


def test_generate_llama_response_omits_sampling_only_settings_for_greedy_generation():
    base_path = "fake-base"
    tokenizer = FakeTokenizer()
    model = FakeModel()
    runtime._GENERATION_CACHE.clear()
    runtime._GENERATION_CACHE[(base_path, None)] = (tokenizer, model)

    runtime.generate_llama_response_hf(
        base_path,
        (("system", "system"), ("user", "hello")),
        do_sample=False,
    )

    assert model.kwargs["do_sample"] is False
    assert "temperature" not in model.kwargs
    assert "top_k" not in model.kwargs


def test_dependency_status_lists_missing_runtime_packages(monkeypatch):
    monkeypatch.setattr(
        runtime,
        "find_spec",
        lambda name: None if name == "peft" else object(),
    )

    assert runtime.dependency_status() == ("peft",)


def test_cache_query_uses_the_same_resolved_adapter_key(tmp_path):
    adapter = tmp_path / "adapter"
    adapter.mkdir()
    runtime._GENERATION_CACHE.clear()
    runtime._GENERATION_CACHE[("base", str(adapter.resolve()))] = (object(), object())

    assert runtime.is_llama_generation_cached("base", adapter)
    assert not runtime.is_llama_generation_cached("other-base", adapter)


@pytest.mark.parametrize(
    ("name", "value"),
    (("max_new_tokens", 0), ("temperature", 0), ("top_k", -1)),
)
def test_generate_llama_response_validates_generation_settings(name, value):
    with pytest.raises(ValueError, match=name.replace("_", " ")):
        runtime.generate_llama_response_hf(
            "fake-base",
            (("system", "system"), ("user", "hello")),
            **{name: value},
        )
