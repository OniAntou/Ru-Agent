from mini_gpt.vts_emotion import EmotionTag, extract_reply_emotion


def test_valid_tag_wins_and_is_stripped():
    cleaned, tag = extract_reply_emotion("Vui quá đi! [[EMO:joy]]")
    assert cleaned == "Vui quá đi!"
    assert tag == EmotionTag.JOY


def test_unknown_tag_falls_back_to_heuristic_and_is_stripped():
    cleaned, tag = extract_reply_emotion("Em buồn quá [[EMO:xyz]]")
    assert cleaned == "Em buồn quá"
    assert tag == EmotionTag.SAD


def test_no_tag_uses_vietnamese_heuristic():
    _, tag = extract_reply_emotion("Trời ơi, ngạc nhiên thật!")
    assert tag == EmotionTag.SURPRISED
    _, tag = extract_reply_emotion("Em xấu hổ quá đi mất")
    assert tag == EmotionTag.SHY
    _, tag = extract_reply_emotion("Câu trả lời trung tính bình thường")
    assert tag == EmotionTag.NONE


def test_heuristic_normalizes_diacritics():
    _, tag = extract_reply_emotion("Anh nói vậy em giận anh đấy!")
    assert tag == EmotionTag.ANGRY


def test_marker_mid_sentence_is_removed_but_text_kept():
    cleaned, tag = extract_reply_emotion("Hôm nay [[EMO:sad]] em hơi mệt")
    assert cleaned == "Hôm nay em hơi mệt"
    assert tag == EmotionTag.SAD
