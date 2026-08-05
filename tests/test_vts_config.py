import pytest

from mini_gpt.vts_config import FLOAT_FIELDS, MotionConfig


def test_defaults_match_policy_ranges():
    config = MotionConfig.defaults()
    assert config.sway_amplitude_idle == 10.0
    assert config.sway_amplitude_speaking == 16.0
    assert config.micro_action_min_delay == 8.0
    assert config.micro_action_max_delay == 22.0
    assert config.pose_min_interval == 90.0
    assert config.pose_max_interval == 130.0
    assert config.breath_amplitude == 1.5
    assert config.breath_rate == 0.15
    for name in FLOAT_FIELDS:
        assert hasattr(config, name)


def test_round_trip_to_and_from_dict():
    config = MotionConfig(sway_amplitude_idle=18.0, head_scale=150.0)
    restored = MotionConfig.from_dict(config.to_dict())
    assert restored == config


def test_from_dict_rejects_out_of_range():
    with pytest.raises(ValueError, match="sway_amplitude_idle"):
        MotionConfig.from_dict({**MotionConfig.defaults().to_dict(), "sway_amplitude_idle": 999.0})


def test_from_dict_rejects_non_numeric():
    with pytest.raises(ValueError, match="breath_rate"):
        MotionConfig.from_dict({**MotionConfig.defaults().to_dict(), "breath_rate": "nhanh"})


def test_from_dict_rejects_missing_field():
    data = MotionConfig.defaults().to_dict()
    del data["eye_scale"]
    with pytest.raises(ValueError, match="eye_scale"):
        MotionConfig.from_dict(data)


def test_from_dict_enforces_min_max_ordering():
    data = MotionConfig.defaults().to_dict()
    data["micro_action_min_delay"] = 30.0
    data["micro_action_max_delay"] = 10.0
    with pytest.raises(ValueError, match="micro_action_max_delay"):
        MotionConfig.from_dict(data)
    data = MotionConfig.defaults().to_dict()
    data["pose_min_interval"] = 500.0
    data["pose_max_interval"] = 50.0
    with pytest.raises(ValueError, match="pose_max_interval"):
        MotionConfig.from_dict(data)