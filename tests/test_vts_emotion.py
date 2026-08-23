from ru_runtime.vts.emotion import EmotionTag, extract_reply_emotion


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


def test_nuanced_vietnamese_emotions():
    _, tag = extract_reply_emotion("Hứ, không thèm nói chuyện với anh nữa đâu! [[EMO:pouting]]")
    assert tag == EmotionTag.POUTING

    _, tag = extract_reply_emotion("Dỗi rồi, bắt đền anh đấy")
    assert tag == EmotionTag.POUTING

    _, tag = extract_reply_emotion("Anh nhớ ngủ sớm giữ gìn sức khỏe nha, thương anh nhiều")
    assert tag == EmotionTag.CARING

    _, tag = extract_reply_emotion("Lêu lêu anh gà thế, bị em lừa rồi nha")
    assert tag == EmotionTag.PLAYFUL


def test_mood_tracker_lifecycle_and_comfort():
    from ru_runtime.vts.emotion import MoodTag, MoodTracker

    tracker = MoodTracker()
    assert tracker.state.current_mood == MoodTag.NEUTRAL

    # Ru replies with pouting
    tracker.update_from_reply_emotion(EmotionTag.POUTING)
    assert tracker.state.current_mood == MoodTag.POUTING
    assert tracker.state.turns_remaining == 2
    prompt_desc = tracker.state.describe_for_prompt()
    assert prompt_desc is not None
    assert "dỗi" in prompt_desc

    # User says neutral words -> mood remains pouting with 1 turn decay
    tracker.observe_user_input("Hôm nay trời đẹp nhỉ")
    tracker.update_from_reply_emotion(EmotionTag.NONE)
    assert tracker.state.current_mood == MoodTag.POUTING
    assert tracker.state.turns_remaining == 1

    # User comforts / apologizes -> mood switches to cheerful
    tracker.observe_user_input("Thôi mà anh xin lỗi em thương em ngoan nha")
    assert tracker.state.current_mood == MoodTag.CHEERFUL


def test_mood_tracker_vulnerable_user():
    from ru_runtime.vts.emotion import MoodTag, MoodTracker

    tracker = MoodTracker()
    tracker.observe_user_input("Hôm nay anh mệt mỏi và áp lực quá em ơi")
    assert tracker.state.current_mood == MoodTag.CARING
    prompt_desc = tracker.state.describe_for_prompt()
    assert prompt_desc is not None
    assert "quan tâm" in prompt_desc
