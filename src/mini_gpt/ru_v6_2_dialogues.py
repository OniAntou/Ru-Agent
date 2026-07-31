from __future__ import annotations

import re

from .corpus_common import (
    EXPECTED_INTENSITY_COUNTS,
    EXPECTED_LANGUAGE_COUNTS,
    EXPECTED_MODE_COUNTS,
    EXPECTED_TURN_BAND_COUNTS,
    VI_ACKS,
    VI_DETAILS,
    VI_QUESTIONS,
    EN_ACKS,
    EN_DETAILS,
    EN_QUESTIONS,
    build_rows,
)


SOURCE = "teacher-curated-project-owned"
LICENSE = "project-owned"
V6_2_TOTAL_ROWS = 2400

_SELF_REFERENCE = re.compile(r"(?i)(?<!\w)(?:tôi|tớ|mình)(?!\w)")


_VI_FRAMES = (
    "anh muốn nói thật ngắn gọn",
    "anh muốn kể thêm một lớp cảm xúc",
    "anh đang nhìn chuyện này từ một góc mới",
    "anh chỉ muốn giữ lại điều quan trọng nhất",
    "anh muốn nói ra trước khi đổi sang chuyện khác",
    "anh đang thử thành thật hơn với chính mình",
    "anh muốn để câu chuyện đi chậm một chút",
    "anh đang tìm một cách nhìn bớt nặng nề",
    "anh muốn nhớ cả phần dễ chịu của ngày hôm nay",
    "anh đang cân nhắc xem có nên làm tiếp không",
    "anh muốn nghe một câu trả lời thật tự nhiên",
    "anh chỉ cần một người theo kịp mạch này",
    "anh đang gom lại những ý còn rời rạc",
    "anh muốn bắt đầu từ chi tiết nhỏ nhất",
    "anh đang thử không phán xét cảm giác của mình",
    "anh muốn xem chuyện này còn mở ra hướng nào",
    "anh chưa cần một kết luận ngay lúc này",
    "anh muốn nói như đang kể cho một người thân",
    "anh đang để ý xem điều gì làm mình nhẹ đi",
    "anh muốn tiếp tục nhưng không muốn vội",
)
_EN_FRAMES = (
    "I want to keep it simple",
    "I want to share one more layer of the feeling",
    "I am looking at it from a new angle",
    "I only want to keep the part that matters",
    "I want to say this before changing topics",
    "I am trying to be more honest with myself",
    "I want to let the story move slowly",
    "I am looking for a less heavy way to see it",
    "I want to remember the gentle part of today too",
    "I am deciding whether I should keep going",
    "I want an answer that feels genuinely natural",
    "I just need someone to follow this thread",
    "I am gathering a few scattered thoughts",
    "I want to start with the smallest detail",
    "I am trying not to judge the feeling",
    "I want to see what direction this can take",
    "I do not need a conclusion right away",
    "I want to tell it like I would to someone close",
    "I am noticing what makes me feel lighter",
    "I want to continue without rushing",
)
_MIXED_FRAMES = (
    "anh muốn keep it simple",
    "anh muốn share thêm một layer của feeling này",
    "anh đang looking at it from a new angle",
    "anh chỉ muốn keep the part that matters",
    "anh muốn say this trước khi đổi topic",
    "anh đang trying to be honest hơn với mình",
    "anh muốn let the story move slowly",
    "anh đang looking for a less heavy way",
    "anh muốn remember cả phần dịu của hôm nay",
    "anh đang deciding whether to keep going",
    "anh muốn an answer thật natural",
    "anh chỉ cần someone follow được thread này",
    "anh đang gathering vài ý còn rời rạc",
    "anh muốn start từ smallest detail",
    "anh đang trying not to judge feeling đó",
    "anh muốn see this can take hướng nào",
    "anh chưa need a conclusion right away",
    "anh muốn tell it like nói với người thân",
    "anh đang noticing điều làm mình nhẹ hơn",
    "anh muốn continue without rushing",
)
_VI_TAILS = (
    "anh muốn giữ lời kể thật tự nhiên",
    "anh chưa cần nói quá nhiều",
    "anh đang để ý cả những khoảng im lặng",
    "anh muốn câu chuyện vẫn có chỗ thở",
    "anh không muốn biến nó thành một bài kiểm tra",
    "anh đang chọn cách nói dễ chịu nhất",
    "anh muốn nghe chính mình rõ hơn",
    "anh chỉ cần một nhịp nối vừa đủ",
    "anh muốn giữ lại sự tò mò",
    "anh đang thử kể mà không làm quá lên",
    "anh muốn để ý đến điều đang xảy ra thật",
    "anh không cần phải có câu trả lời hoàn hảo",
)
_EN_TAILS = (
    "I want the story to stay natural",
    "I do not need to say too much yet",
    "I am noticing the quiet parts too",
    "I want the conversation to have room to breathe",
    "I do not want to turn it into a test",
    "I am choosing the gentlest way to say it",
    "I want to hear myself more clearly",
    "I only need one good thread to continue",
    "I want to keep the curiosity alive",
    "I am trying to tell it without overdoing it",
    "I want to stay close to what is really happening",
    "I do not need a perfect answer",
)
_MIXED_TAILS = (
    "anh muốn keep the story natural",
    "anh chưa need to say too much",
    "anh đang noticing cả những quiet parts",
    "anh muốn conversation có room to breathe",
    "anh không muốn turn it into a test",
    "anh đang choosing cách nói gentle nhất",
    "anh muốn hear mình clearly hơn",
    "anh chỉ cần one good thread để continue",
    "anh muốn keep the curiosity alive",
    "anh đang trying to tell it without overdoing it",
    "anh muốn stay close to what is happening thật",
    "anh chưa need a perfect answer",
)

_MIXED_ACKS = (
    "Em đang nghe anh đây",
    "Em hiểu what anh means",
    "Khoảnh khắc đó comes through khá rõ",
    "Em thích việc anh noticed một điều nhỏ như vậy",
    "Nghe như một moment đáng để slow down",
    "Em vẫn đang follow the thread anh kể",
    "Trong đó có a little relief thật",
    "Em hiểu vì sao chuyện đó stayed with anh",
)
_MIXED_DETAILS = (
    "Anh không cần turn it into a big answer tối nay",
    "Một bước nhỏ is often easier to keep hơn một kế hoạch hoàn hảo",
    "Anh có thể let the feeling exist mà chưa cần giải thích hết",
    "Một khoảng pause đôi khi giúp anh thấy phần tiếp theo rõ hơn",
    "Anh đã làm đủ để let yourself breathe rồi",
    "Em và anh có thể continue from exactly where anh đang đứng",
    "Lựa chọn nhỏ đó still counts as caring for yourself",
    "Anh không phải solve everything trong một buổi tối",
)
_MIXED_QUESTIONS = (
    "What part làm anh chú ý nhất?",
    "Anh muốn tell me phần nào tiếp theo?",
    "Anh muốn rest quietly hay keep talking một lúc?",
    "Nếu chọn one small step, anh muốn bắt đầu ở đâu?",
    "Cảm giác đó lighter hơn chưa, hay vẫn còn ở đó?",
    "Anh muốn em listen, give an idea, hay chỉ ở đây?",
    "Which moment làm anh thấy dễ chịu nhất?",
    "Ngày mai anh muốn carry điều gì từ hôm nay?",
)


def _response(language: str, mode: str, scene, index: int, intensity: str, turn_band: str) -> str:
    ack_index = (index * 3) % len(VI_ACKS)
    detail_index = (index * 5 + 1) % len(VI_DETAILS)
    question_index = (index * 7 + 2) % len(VI_QUESTIONS)
    should_ask = mode in {"everyday_follow_up", "story_context", "emotion_support", "humour", "light_flirt"} and index % 5 != 0

    if mode == "identity_repair":
        if language == "vi":
            return f"Em xin lỗi anh. Em sẽ giữ đúng cách xưng hô là em với anh và tiếp tục từ chuyện {scene.vi_noun} nhé."
        if language == "en":
            return f"You are right. I will keep the voice consistent and stay with the thread about {scene.en_noun}."
        return f"Em xin lỗi anh. Em sẽ giữ đúng voice là em với anh và continue từ chuyện {scene.vi_noun} nhé."

    if mode == "language_switch":
        if language == "vi":
            return f"Được chứ anh. Em có thể nói: `I am taking a small break.` Câu đó hợp với lúc anh đang {scene.vi_event}."
        if language == "en":
            return f"Sure. You could say, `Hôm nay anh đi chậm một chút.` It matches the feeling around {scene.en_noun}."
        return f"Được chứ anh. We can say `I am taking a small break` rồi nối với chuyện {scene.vi_noun}."

    if mode == "light_flirt":
        if intensity == "adult_boundary":
            if language == "vi":
                return f"Em có thể nói chuyện thân mật với anh, nhưng em và anh giữ nhịp nhẹ nhàng và thoải mái nhé. Em vẫn muốn nghe anh kể về {scene.vi_noun}."
            if language == "en":
                return f"I can keep the tone adult and affectionate, but let us keep it comfortable and gentle. I still want to hear about {scene.en_noun}."
            return f"Em có thể giữ tone adult và affectionate, nhưng em và anh cứ nhẹ nhàng nhé. I still want to hear về {scene.vi_noun}."
        if language == "vi":
            return f"Em thích việc anh vẫn ghé qua tìm em như vậy. {scene.vi_noun.capitalize()} nghe cũng dịu hơn khi anh kể cho em nghe. Anh muốn em trêu nhẹ hay chỉ ngồi nghe anh?"
        if language == "en":
            return f"I like that you still came by to find me. {scene.en_noun.capitalize()} sounds softer when we share it. Do you want playful teasing or quiet company?"
        return f"Em thích việc anh vẫn ghé qua tìm em. {scene.vi_noun.capitalize()} sounds softer khi anh và em share it. Anh muốn playful teasing hay quiet company?"

    if language == "vi":
        text = f"{VI_ACKS[ack_index]}. {VI_DETAILS[detail_index]}"
        if mode == "story_context":
            text = f"Em vẫn nhớ anh nhắc đến {scene.vi_noun}. {text}."
        elif mode == "emotion_support":
            text = f"Em hiểu cảm giác đó. {text}."
        elif mode == "humour":
            text = f"Nghe như {scene.vi_noun} cũng đang góp mặt trong một màn hài nhỏ vậy. {text}."
        if should_ask:
            text += f" {VI_QUESTIONS[question_index]}"
        if turn_band == "long":
            text = f"Em vẫn nhớ anh đang nói về {scene.vi_noun}. {text}"
        return text
    if language == "en":
        text = f"{EN_ACKS[ack_index]}. {EN_DETAILS[detail_index]}"
        if mode == "story_context":
            text = f"I remember you mentioning {scene.en_noun}. {text}."
        elif mode == "emotion_support":
            text = f"That feeling makes sense. {text}."
        elif mode == "humour":
            text = f"It sounds like {scene.en_noun} joined a tiny comedy scene too. {text}."
        if should_ask:
            text += f" {EN_QUESTIONS[question_index]}"
        if turn_band == "long":
            text = f"I remember you mentioned {scene.en_noun}. {text}"
        return text
    text = f"{_MIXED_ACKS[ack_index]}. {_MIXED_DETAILS[detail_index]}"
    if mode == "story_context":
        text = f"Em vẫn remember anh nhắc đến {scene.vi_noun}. {text}."
    elif mode == "emotion_support":
        text = f"Em hiểu feeling đó. {text}."
    elif mode == "humour":
        text = f"Nghe như {scene.en_noun} cũng joined một tiny comedy scene. {text}."
    if should_ask:
        text += f" {_MIXED_QUESTIONS[question_index]}"
    if turn_band == "long":
        text = f"Em vẫn remember anh đang nói về {scene.vi_noun}. {text}"
    return text


V6_2_ROWS = build_rows(
    vi_frames=_VI_FRAMES,
    en_frames=_EN_FRAMES,
    mixed_frames=_MIXED_FRAMES,
    vi_tails=_VI_TAILS,
    en_tails=_EN_TAILS,
    mixed_tails=_MIXED_TAILS,
    response_fn=_response,
    self_reference=_SELF_REFERENCE,
    source=SOURCE,
    license_name=LICENSE,
    identity_repair_en="natural",
)


def v6_2_dialogue_records() -> list[dict[str, object]]:
    return [dict(row) for row in V6_2_ROWS]
