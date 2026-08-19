from __future__ import annotations

from ru_runtime.voice.vietnamese_tts_normalizer import (
    int_to_vietnamese_words,
    normalize_vietnamese_tts_text,
)


def test_int_to_vietnamese_words_units_and_tens():
    assert int_to_vietnamese_words(0) == "không"
    assert int_to_vietnamese_words(1) == "một"
    assert int_to_vietnamese_words(5) == "năm"
    assert int_to_vietnamese_words(10) == "mười"
    assert int_to_vietnamese_words(11) == "mười một"
    assert int_to_vietnamese_words(15) == "mười lăm"
    assert int_to_vietnamese_words(20) == "hai mươi"
    assert int_to_vietnamese_words(21) == "hai mươi mốt"
    assert int_to_vietnamese_words(24) == "hai mươi tư"
    assert int_to_vietnamese_words(25) == "hai mươi lăm"


def test_int_to_vietnamese_words_hundreds_and_thousands():
    assert int_to_vietnamese_words(100) == "một trăm"
    assert int_to_vietnamese_words(105) == "một trăm lẻ năm"
    assert int_to_vietnamese_words(115) == "một trăm mười lăm"
    assert int_to_vietnamese_words(1000) == "một nghìn"
    assert int_to_vietnamese_words(2026) == "hai nghìn không trăm hai mươi sáu"


def test_normalize_time_expressions():
    assert normalize_vietnamese_tts_text("21:24") == "hai mươi mốt giờ hai mươi tư phút"
    assert normalize_vietnamese_tts_text("8:05") == "tám giờ lẻ năm phút"
    assert normalize_vietnamese_tts_text("12:00") == "mười hai giờ đúng"


def test_normalize_date_expressions():
    assert normalize_vietnamese_tts_text("19/08/2026") == "ngày mười chín tháng tám năm hai nghìn không trăm hai mươi sáu"


def test_normalize_percentages_and_units():
    assert normalize_vietnamese_tts_text("79%") == "bảy mươi chín phần trăm"
    assert normalize_vietnamese_tts_text("15.7 GB") == "mười lăm phẩy bảy ghi-ga-bai"


def test_normalize_full_sentence():
    raw = "Bây giờ là 21:24 ngày 19/08/2026, pin laptop còn 80% nha anh."
    normalized = normalize_vietnamese_tts_text(raw)
    assert "hai mươi mốt giờ hai mươi tư phút" in normalized
    assert "ngày mười chín tháng tám năm hai nghìn không trăm hai mươi sáu" in normalized
    assert "tám mươi phần trăm" in normalized
