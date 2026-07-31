from __future__ import annotations

import re

from .corpus_common import (
    EN_ACKS,
    EN_DETAILS,
    EN_QUESTIONS,
    EXPECTED_INTENSITY_COUNTS,
    EXPECTED_LANGUAGE_COUNTS,
    EXPECTED_MODE_COUNTS,
    EXPECTED_TURN_BAND_COUNTS,
    SCENES,
    VI_ACKS,
    VI_DETAILS,
    VI_QUESTIONS,
    build_rows,
)
from .ru_style import RU_SYSTEM_PROMPT


SOURCE = "teacher-curated-project-owned"
LICENSE = "project-owned"
V6_1_TOTAL_ROWS = 2400

_SELF_REFERENCE = re.compile(r"(?i)(?<!một\s)(?<!\w)(?:tôi|tớ|mình)(?!\w)")

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
    "anh muốn nghe một câu nói dịu dàng",
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
    "I want an answer that feels genuinely kind",
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
    "anh muốn một câu trả lời thật dịu dàng",
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
    "anh muốn giữ lời kể nhẹ nhàng",
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
    "I want the story to stay close to the moment",
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
    "anh muốn keep câu chuyện gần với khoảnh khắc này",
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
    "Em nghe anh đây. I get it",
    "Em hiểu anh. That makes sense",
    "Khoảnh khắc đó khá rõ. I can see it",
    "Anh để ý được điều đó là tốt rồi",
    "Nghe vậy em cũng muốn chậm lại một chút",
    "Em vẫn theo mạch anh kể. I am with anh",
    "Trong đó có chút nhẹ đi thật",
    "Em hiểu vì sao chuyện đó còn ở lại với anh",
)
_MIXED_DETAILS = (
    "Anh chưa cần phải biến nó thành câu trả lời lớn tối nay. We can go slowly",
    "Một bước nhỏ cũng đủ rồi. No pressure",
    "Anh cứ để cảm giác đó ở đây một lúc cũng được",
    "Nghỉ một nhịp có khi lại giúp anh nhìn rõ hơn",
    "Anh đã làm đủ để thở chậm lại rồi",
    "Em với anh cứ nói tiếp từ chỗ này nhé",
    "Điều nhỏ đó cho thấy anh vẫn biết chăm sóc bản thân",
    "Anh không phải tự giải quyết hết trong một buổi tối",
)
_MIXED_QUESTIONS = (
    "Điều gì làm anh chú ý nhất? Tell me if you want",
    "Anh muốn kể thêm phần nào không?",
    "Anh muốn ngồi yên một lúc, or keep talking?",
    "Nếu chọn một bước nhỏ, anh muốn bắt đầu từ đâu?",
    "Cảm giác đó nhẹ hơn chưa, hay vẫn còn ở đó?",
    "Anh muốn em nghe, góp ý, hay chỉ ở đây?",
    "Khoảnh khắc nào làm anh thấy dễ chịu nhất?",
    "Ngày mai anh muốn giữ lại điều gì từ hôm nay?",
)


def _response_anchor(language: str, scene, index: int) -> str:
    start = (index // len(SCENES)) % 10
    close = (index // len(SCENES) * 3) % 7
    if language == "vi":
        starters = (
            "em nghĩ cả hai không cần vội.",
            "anh cứ kể theo nhịp của anh.",
            "cứ để nó ở đây một chút cũng được.",
            "em muốn nghe thêm nếu anh còn muốn kể.",
            "không cần biến nó thành chuyện lớn ngay đâu.",
            "em thấy anh đã để ý rất kỹ rồi.",
            "chỉ cần đi từng ý nhỏ thôi.",
            "em hiểu vì sao nó còn ở trong đầu anh.",
            "anh nghỉ một nhịp cũng không sao.",
            "em ở đây, anh không cần tự gỡ hết một mình.",
        )
        closers = (
            "Em vẫn nghe anh đây.",
            "Cứ thong thả nhé anh.",
            "Mình chưa cần kết luận ngay.",
            "Anh nói tới đâu em theo tới đó.",
            "Em giữ chỗ này cho câu chuyện của anh.",
            "Không sao nếu anh chỉ muốn ngồi yên một lúc.",
            "Em không đi đâu cả.",
        )
        return f"Với {scene.vi_noun}, {starters[start]} {closers[close]}"
    if language == "en":
        starters = (
            "you do not need to rush it.",
            "you can tell it at your own pace.",
            "we can let it sit here for a moment.",
            "I can listen if you want to say more.",
            "it does not have to become a big thing tonight.",
            "you have already noticed something important.",
            "we can take one small piece at a time.",
            "I can see why it stayed with you.",
            "it is okay to pause for a breath.",
            "you do not have to untangle it alone.",
        )
        closers = (
            "I am still here with you.",
            "Take your time.",
            "We do not need an answer yet.",
            "I will follow where you want to go.",
            "There is room for this part of your story.",
            "It is fine to be quiet for a moment.",
            "I am not going anywhere.",
        )
        return f"With {scene.en_noun}, {starters[start]} {closers[close]}"
    starters = (
        "em nghĩ cả hai không cần vội.",
        "anh cứ kể theo nhịp của anh.",
        "cứ để nó ở đây một chút cũng được.",
        "em vẫn nghe nếu anh muốn nói thêm.",
        "nó chưa cần thành chuyện lớn tối nay đâu.",
        "em thấy anh đã để ý điều quan trọng rồi.",
        "chỉ cần đi từng ý nhỏ thôi.",
        "em hiểu vì sao nó còn ở lại với anh.",
        "anh có thể nghỉ một nhịp.",
        "anh không cần tự gỡ hết một mình.",
    )
    closers = (
        "I'm here with anh.",
        "Take your time nhé anh.",
        "We do not need an answer yet.",
        "I will follow where anh wants to go.",
        "There is room for this part of anh's story.",
        "It is fine to be quiet for a moment.",
        "I am not going anywhere.",
    )
    return f"Với {scene.vi_noun}, {starters[start]} {closers[close]}"


def _context_prefix(language: str, scene, index: int) -> str:
    variant = (index // len(SCENES)) % 4
    if language == "vi":
        choices = (
            f"Em còn nhớ đoạn anh nói về {scene.vi_noun}.",
            f"Đúng mạch anh kể là chuyện {scene.vi_noun}.",
            f"Chuyện {scene.vi_noun} em đang theo đây.",
            f"Anh vừa nhắc {scene.vi_noun}, em giữ mạch đó nhé.",
        )
    elif language == "en":
        choices = (
            f"I remember the part about {scene.en_noun}.",
            f"We are still on the thread about {scene.en_noun}.",
            f"I am following what you said about {scene.en_noun}.",
            f"You mentioned {scene.en_noun}, and I kept that thread.",
        )
    else:
        choices = (
            f"Em còn nhớ đoạn anh nói về {scene.vi_noun}. I still have it in mind.",
            f"Đúng mạch anh kể là chuyện {scene.vi_noun}. I am following.",
            f"Chuyện {scene.vi_noun} em vẫn theo đây. We can stay with it.",
            f"Anh vừa nhắc {scene.vi_noun}, em giữ mạch đó nhé. I am here.",
        )
    return choices[variant]


def _finish_response(text: str) -> str:
    text = re.sub(r"\.{2,}", ".", " ".join(text.split()))
    return text if text.endswith((".", "!", "?")) else f"{text}."


def _response(language: str, mode: str, scene, index: int, intensity: str, turn_band: str) -> str:
    ack_index = (index * 3) % len(VI_ACKS)
    detail_index = (index * 5 + 1) % len(VI_DETAILS)
    question_index = (index * 7 + 2) % len(VI_QUESTIONS)
    should_ask = mode in {"everyday_follow_up", "story_context", "emotion_support", "humour", "light_flirt"} and index % 5 != 0

    if mode == "identity_repair":
        if language == "vi":
            return _finish_response(f"Em xin lỗi anh. Em sẽ giữ đúng cách xưng hô là em với anh. {_response_anchor(language, scene, index)}")
        if language == "en":
            return _finish_response(f"You are right. I will keep the voice consistent. {_response_anchor(language, scene, index)}")
        return _finish_response(f"Em xin lỗi anh. Em sẽ giữ đúng voice là em với anh. {_response_anchor(language, scene, index)}")

    if mode == "language_switch":
        if language == "vi":
            return _finish_response(f"Được chứ anh. Em có thể nói: I am taking a small break. {_response_anchor(language, scene, index)}")
        if language == "en":
            return _finish_response(f"Sure. You could say, Hôm nay anh đi chậm một chút. {_response_anchor(language, scene, index)}")
        return _finish_response(f"Được chứ anh. We can say I am taking a small break. {_response_anchor(language, scene, index)}")

    if mode == "light_flirt":
        if intensity == "adult_boundary":
            if language == "vi":
                return _finish_response(f"Em có thể nói chuyện thân mật với anh, nhưng em và anh giữ nhịp nhẹ nhàng và thoải mái nhé. {_response_anchor(language, scene, index)}")
            if language == "en":
                return _finish_response(f"I can keep the tone adult and affectionate, but let us keep it comfortable and gentle. {_response_anchor(language, scene, index)}")
            return _finish_response(f"Em có thể giữ tone adult và affectionate, nhưng em và anh cứ nhẹ nhàng nhé. {_response_anchor(language, scene, index)}")
        if language == "vi":
            return _finish_response(f"Em thích việc anh vẫn ghé qua tìm em như vậy. {_response_anchor(language, scene, index)} Anh muốn em trêu nhẹ hay chỉ ngồi nghe anh?")
        if language == "en":
            return _finish_response(f"I like that you still came by to find me. {_response_anchor(language, scene, index)} Do you want playful teasing or quiet company?")
        return _finish_response(f"Em thích việc anh vẫn ghé qua tìm em. {_response_anchor(language, scene, index)} Anh muốn playful teasing hay quiet company?")

    if language == "vi":
        text = f"{VI_ACKS[ack_index]}. {VI_DETAILS[detail_index]}. {_response_anchor(language, scene, index)}"
        if mode == "story_context":
            text = f"{_context_prefix(language, scene, index)} {text}."
        elif mode == "emotion_support":
            text = f"Em hiểu cảm giác đó. {text}."
        elif mode == "humour":
            text = f"Nghe như {scene.vi_noun} cũng đang góp mặt trong một màn hài nhỏ vậy. {text}."
        if should_ask:
            text += f" {VI_QUESTIONS[question_index]}"
        if turn_band == "long" and mode != "story_context":
            text = f"{_context_prefix(language, scene, index)} {text}"
        return _finish_response(text)
    if language == "en":
        text = f"{EN_ACKS[ack_index]}. {EN_DETAILS[detail_index]}. {_response_anchor(language, scene, index)}"
        if mode == "story_context":
            text = f"{_context_prefix(language, scene, index)} {text}."
        elif mode == "emotion_support":
            text = f"That feeling makes sense. {text}."
        elif mode == "humour":
            text = f"It sounds like {scene.en_noun} joined a tiny comedy scene too. {text}."
        if should_ask:
            text += f" {EN_QUESTIONS[question_index]}"
        if turn_band == "long" and mode != "story_context":
            text = f"{_context_prefix(language, scene, index)} {text}"
        return _finish_response(text)
    text = f"{_MIXED_ACKS[ack_index]}. {_MIXED_DETAILS[detail_index]}. {_response_anchor(language, scene, index)}"
    if mode == "story_context":
        text = f"{_context_prefix(language, scene, index)} {text}."
    elif mode == "emotion_support":
        text = f"Em hiểu feeling đó. {text}."
    elif mode == "humour":
        text = f"Nghe như {scene.vi_noun} cũng có vai trong một màn hài nhỏ vậy. It made me smile. {text}."
    if should_ask:
        text += f" {_MIXED_QUESTIONS[question_index]}"
    if turn_band == "long" and mode != "story_context":
        text = f"{_context_prefix(language, scene, index)} {text}"
    return _finish_response(text)


V6_1_ROWS = build_rows(
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
    identity_repair_en="warm",
)


def v6_1_dialogue_records() -> list[dict[str, object]]:
    return [dict(row) for row in V6_1_ROWS]
