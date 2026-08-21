from __future__ import annotations

import pytest

from ru_runtime.persistence.diagnostic_context import (
    generate_correlation_id,
    is_valid_correlation_id,
    sanitize_correlation_id,
    get_correlation_id,
    set_correlation_id,
    correlation_scope,
)


def test_generate_correlation_id():
    cid = generate_correlation_id()
    assert cid.startswith("c_")
    assert 8 <= len(cid) <= 64
    assert is_valid_correlation_id(cid)


@pytest.mark.parametrize(
    "candidate",
    [
        "c_1234567890abcdef",
        "corr_abc-def_123",
        "req_12345678",
    ],
)
def test_is_valid_correlation_id_accepts(candidate: str):
    assert is_valid_correlation_id(candidate) is True


@pytest.mark.parametrize(
    "candidate",
    [
        None,
        "",
        "   ",
        "short",
        "has spaces 12345",
        "has$special#chars!",
        "Bearer sk-1234567890abcdef1234567890",
        "c_" + "a" * 100,
    ],
)
def test_is_valid_correlation_id_rejects(candidate: str | None):
    assert is_valid_correlation_id(candidate) is False


def test_sanitize_correlation_id():
    valid = "c_abcdef1234567890"
    assert sanitize_correlation_id(valid) == valid

    oversized = "c_" + "a" * 100
    sanitized = sanitize_correlation_id(oversized)
    assert sanitized != oversized
    assert is_valid_correlation_id(sanitized)

    invalid = "invalid with spaces"
    sanitized = sanitize_correlation_id(invalid)
    assert sanitized != invalid
    assert is_valid_correlation_id(sanitized)


def test_correlation_scope():
    assert get_correlation_id() is None

    cid1 = generate_correlation_id()
    with correlation_scope(cid1):
        assert get_correlation_id() == cid1

        cid2 = generate_correlation_id()
        with correlation_scope(cid2):
            assert get_correlation_id() == cid2

        assert get_correlation_id() == cid1

    assert get_correlation_id() is None
