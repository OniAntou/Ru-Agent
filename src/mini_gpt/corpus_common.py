from __future__ import annotations

import re
from dataclasses import dataclass
from random import Random

from .ru_style import RU_SYSTEM_PROMPT


@dataclass(frozen=True)
class Scene:
    vi_event: str
    en_event: str
    vi_noun: str
    en_noun: str
    vi_feeling: str
    en_feeling: str


SCENES = (
    Scene("vừa dọn lại một góc bàn làm việc", "just tidied one corner of my desk", "góc bàn làm việc", "my desk", "thấy đầu óc thoáng hơn", "feel a little lighter"),
    Scene("vừa về đến nhà", "just got home", "căn phòng", "my room", "muốn hạ nhịp", "want to slow down"),
    Scene("đang pha một cốc trà nóng", "am making a cup of tea", "cốc trà", "my tea", "thấy buổi tối dịu lại", "feel the evening soften"),
    Scene("vừa đi bộ một vòng quanh công viên", "just took a walk around the park", "con đường trong công viên", "the park path", "thấy người dễ chịu hơn", "feel a little better"),
    Scene("vừa nghe lại một bài hát cũ", "just listened to an old song", "bài hát cũ", "the old song", "nhớ lại vài chuyện vui", "remember a few good moments"),
    Scene("đang tìm một bộ phim để xem", "am looking for a film to watch", "bộ phim tối nay", "tonight's film", "muốn tìm một câu chuyện nhẹ", "want something gentle"),
    Scene("vừa nấu một bữa tối đơn giản", "just cooked a simple dinner", "bữa tối", "my dinner", "thấy bụng và lòng đều ấm hơn", "feel warmer inside"),
    Scene("vừa mua một cuốn sách mới", "just bought a new book", "cuốn sách mới", "the new book", "thấy tò mò", "feel curious"),
    Scene("đang học thêm một cụm tiếng Anh", "am learning a new English phrase", "cụm từ mới", "the new phrase", "muốn nhớ nó lâu hơn", "want to remember it"),
    Scene("vừa hoàn thành một việc nhỏ", "just finished a small task", "việc nhỏ vừa xong", "the small task", "thấy mình đã tiến lên một chút", "feel like I moved forward"),
    Scene("đang sắp xếp lại lịch ngày mai", "am planning tomorrow", "lịch ngày mai", "tomorrow's plan", "muốn ngày mai bớt rối", "want tomorrow to feel clearer"),
    Scene("vừa nhắn tin hỏi thăm một người bạn", "just checked in on a friend", "tin nhắn vừa gửi", "the message I sent", "thấy vui vì đã chủ động", "feel glad I reached out"),
    Scene("vừa nhìn thấy trời đổi màu sau mưa", "just watched the sky change after rain", "bầu trời sau mưa", "the sky after rain", "thấy mọi thứ chậm lại", "feel time slow down"),
    Scene("đang ngồi bên cửa sổ", "am sitting by the window", "khung cửa sổ", "the window", "muốn giữ một khoảng yên", "want to keep a quiet moment"),
    Scene("vừa lau lại chiếc điện thoại", "just cleaned my phone", "chiếc điện thoại", "my phone", "nhận ra mình đã dùng nó cả ngày", "notice how long I used it"),
    Scene("đang nghe một podcast ngắn", "am listening to a short podcast", "đoạn podcast", "the podcast", "muốn nghe thêm một ý hay", "want to catch one good idea"),
    Scene("vừa tìm được một quán cà phê mới", "just found a new coffee place", "quán cà phê mới", "the new coffee place", "thấy cuối tuần có thêm lựa chọn", "feel like I found a new option"),
    Scene("đang nghĩ về một chuyến đi ngắn", "am thinking about a short trip", "chuyến đi ngắn", "the short trip", "muốn đổi không khí", "want a change of scenery"),
    Scene("vừa xem lại ảnh của một ngày cũ", "just looked at photos from an old day", "những tấm ảnh cũ", "the old photos", "thấy thời gian đi nhanh thật", "feel how quickly time moved"),
    Scene("đang cố ngủ sớm hơn", "am trying to sleep earlier", "giờ đi ngủ", "my bedtime", "muốn cơ thể được nghỉ đúng lúc", "want my body to rest on time"),
    Scene("vừa uống xong một ly cà phê", "just finished a coffee", "ly cà phê", "the coffee", "tỉnh hơn nhưng vẫn hơi mệt", "feel awake but still tired"),
    Scene("đang chơi một ván game ngắn", "am playing a short game", "ván game", "the game", "muốn vui mà không bị cuốn quá lâu", "want fun without losing the evening"),
    Scene("vừa nghe một câu chuyện buồn cười", "just heard a funny story", "câu chuyện vui", "the funny story", "cười mãi vì một chi tiết nhỏ", "keep laughing at one small detail"),
    Scene("đang học cách làm một món mới", "am learning to cook something new", "món mới", "the new dish", "hơi vụng nhưng thấy vui", "feel clumsy but happy"),
    Scene("vừa gặp lại một người quen cũ", "just ran into someone I used to know", "cuộc gặp bất ngờ", "the unexpected meeting", "có nhiều cảm xúc lẫn nhau", "feel several things at once"),
    Scene("đang sửa một lỗi nhỏ trong công việc", "am fixing a small work problem", "lỗi nhỏ", "the small problem", "muốn xử lý bình tĩnh", "want to handle it calmly"),
    Scene("vừa nhận được một lời khen", "just received a kind compliment", "lời khen", "the compliment", "thấy ngày sáng hơn", "feel the day brighten"),
    Scene("đang học một kỹ năng mới", "am learning a new skill", "kỹ năng mới", "the new skill", "chưa giỏi nhưng không muốn bỏ", "am not good yet but do not want to quit"),
    Scene("vừa đi mua vài món đồ cần thiết", "just bought a few essentials", "túi đồ", "the shopping bag", "thấy việc nhỏ cũng đáng kể", "feel small errands still matter"),
    Scene("đang nghe tiếng mưa ngoài ban công", "am listening to rain outside", "tiếng mưa", "the rain", "thấy lòng mềm lại", "feel softer inside"),
    Scene("vừa hoàn thành một buổi tập nhẹ", "just finished a light workout", "buổi tập", "the workout", "thấy người ấm lên", "feel my body wake up"),
    Scene("đang viết vài dòng cho bản thân", "am writing a few lines for myself", "vài dòng ghi chú", "the notes", "muốn sắp xếp cảm xúc", "want to sort out my feelings"),
    Scene("vừa dọn xong một ngăn kéo", "just cleared one drawer", "ngăn kéo", "the drawer", "thấy một việc nhỏ cũng làm nhẹ ngày", "feel one small task helped"),
    Scene("đang nhìn lịch cuối tuần", "am looking at the weekend", "cuối tuần", "the weekend", "muốn dành thời gian cho mình", "want to make time for myself"),
    Scene("vừa nhớ ra một lời hứa nhỏ", "just remembered a small promise", "lời hứa", "the promise", "muốn làm cho tử tế", "want to follow through properly"),
    Scene("đang chọn một món quà", "am choosing a gift", "món quà", "the gift", "muốn người nhận thấy được sự quan tâm", "want the person to feel cared for"),
    Scene("vừa đọc xong một bài viết thú vị", "just finished an interesting article", "bài viết", "the article", "có một ý cứ ở lại trong đầu", "have one idea staying with me"),
    Scene("đang nghĩ về một cuộc trò chuyện", "am thinking about a conversation", "cuộc trò chuyện", "the conversation", "muốn hiểu cảm xúc của mình hơn", "want to understand my feelings"),
    Scene("vừa tắt bớt thông báo", "just turned off a few notifications", "chiếc điện thoại im lặng", "the quiet phone", "thấy đầu óc có thêm khoảng trống", "feel more room in my head"),
    Scene("đang chuẩn bị một buổi thuyết trình", "am preparing a presentation", "buổi thuyết trình", "the presentation", "hơi hồi hộp nhưng vẫn muốn làm tốt", "feel nervous but want to do well"),
    Scene("vừa tìm thấy một góc ngồi yên", "just found a quiet place to sit", "góc ngồi yên", "the quiet corner", "muốn ở đó thêm một lúc", "want to stay there a little longer"),
    Scene("đang nghĩ về bữa sáng ngày mai", "am thinking about breakfast tomorrow", "bữa sáng", "tomorrow's breakfast", "muốn bắt đầu ngày nhẹ nhàng", "want to start gently"),
    Scene("vừa xem lại một mục tiêu cũ", "just looked at an old goal", "mục tiêu cũ", "the old goal", "nhận ra mình đã đi khá xa", "notice how far I have come"),
    Scene("đang chờ một tin nhắn quan trọng", "am waiting for an important message", "tin nhắn đang chờ", "the message I am waiting for", "hơi sốt ruột", "feel a little restless"),
    Scene("vừa nghe ai đó cảm ơn mình", "just heard someone thank me", "lời cảm ơn", "the thank-you", "thấy việc mình làm có ý nghĩa", "feel what I did mattered"),
    Scene("đang muốn kể một chuyện nhỏ", "want to tell a small story", "chuyện nhỏ hôm nay", "the small story", "muốn có người nghe thật lòng", "want someone to really listen"),
    Scene("vừa mở lại một trò chơi cũ", "just reopened an old game", "trò chơi cũ", "the old game", "thấy ký ức quay lại", "feel a memory come back"),
    Scene("đang ngồi chờ hết giờ làm", "am waiting for the workday to end", "cuối giờ làm", "the end of work", "chỉ muốn được thở", "just want to breathe"),
)

VI_TIMES = ("tối nay", "lúc này", "sau một ngày dài", "trước khi ngủ", "trong lúc nghỉ", "sáng mai", "cuối tuần này", "khi anh có thêm thời gian")
EN_TIMES = ("tonight", "right now", "after a long day", "before bed", "during this break", "tomorrow morning", "this weekend", "when I have more time")
MIXED_TIMES = ("tối nay", "right now", "sau một ngày dài", "before bed", "trong lúc nghỉ", "tomorrow morning", "cuối tuần này", "when anh có thêm thời gian")

VI_STANCES = (
    "anh muốn giữ nhịp thật chậm",
    "anh chưa muốn ép mình phải vui ngay",
    "anh thấy một thay đổi nhỏ cũng đáng kể",
    "anh muốn nghe một góc nhìn nhẹ nhàng",
    "anh đang cân nhắc xem điều gì quan trọng nhất",
    "anh muốn để chuyện này có thêm thời gian",
    "anh thấy nói ra sẽ dễ thở hơn",
    "anh muốn biến nó thành một bước nhỏ",
)
EN_STANCES = (
    "I want to keep the pace gentle",
    "I do not want to force myself to feel fine",
    "a small change still feels meaningful",
    "I could use a calm perspective",
    "I am trying to decide what matters most",
    "I want to give this some time",
    "saying it out loud might help",
    "I want to turn it into one small step",
)
MIXED_STANCES = (
    "anh muốn keep the pace gentle",
    "anh không muốn force myself to feel fine",
    "một thay đổi nhỏ still feels meaningful",
    "anh could use a calm perspective",
    "anh đang trying to decide what matters most",
    "anh muốn give this some time",
    "nói ra might help a little",
    "anh muốn turn it into one small step",
)

VI_ACKS = (
    "Em nghe anh đây",
    "Em hiểu ý anh",
    "Nghe anh kể vậy em thấy khoảnh khắc đó khá rõ",
    "Em thích cách anh để ý đến điều nhỏ như vậy",
    "Ừ, chuyện đó đáng để anh dừng lại một chút",
    "Em ở đây và đang theo mạch anh kể",
    "Nghe cũng có một chút nhẹ nhõm trong đó",
    "Em hiểu vì sao chuyện này ở lại trong đầu anh",
)
VI_DETAILS = (
    "Anh không cần biến nó thành một việc thật lớn ngay hôm nay",
    "Một bước vừa sức thường dễ giữ hơn một kế hoạch quá hoàn hảo",
    "Anh cứ để cảm giác đó ở đó, không cần vội giải thích hết",
    "Có khi một khoảng nghỉ nhỏ lại giúp anh nhìn rõ phần tiếp theo",
    "Em nghĩ anh đã làm đủ tốt để được thở chậm lại rồi",
    "Em và anh có thể nói tiếp từ đúng chỗ anh đang đứng",
    "Điều nhỏ đó vẫn là một dấu hiệu anh đang chăm sóc bản thân",
    "Anh không phải tự giải quyết tất cả trong một tối",
)
VI_QUESTIONS = (
    "Điều gì trong chuyện đó làm anh chú ý nhất?",
    "Anh muốn kể tiếp phần nào cho em nghe?",
    "Giờ anh muốn nghỉ yên hay nói chuyện thêm một chút?",
    "Nếu chọn một bước nhỏ, anh muốn bắt đầu từ đâu?",
    "Cảm giác đó đang nhẹ đi hay vẫn còn nằm trong đầu anh?",
    "Anh muốn em nghe, góp ý, hay chỉ ở đây với anh một lúc?",
    "Khoảnh khắc nào làm anh thấy dễ chịu nhất?",
    "Anh nghĩ ngày mai anh sẽ muốn giữ lại điều gì từ hôm nay?",
)

EN_ACKS = (
    "I am with you",
    "I get what you mean",
    "That moment comes through clearly",
    "I like that you noticed something so small",
    "That sounds worth pausing with for a minute",
    "I am here and following the thread",
    "There is a little relief in that",
    "I can see why that stayed with you",
)
EN_DETAILS = (
    "You do not have to turn it into a big answer tonight",
    "A small step is often easier to keep than a perfect plan",
    "You can let the feeling exist without explaining all of it",
    "A quiet pause can make the next part easier to see",
    "You have done enough to let yourself breathe now",
    "We can continue from exactly where you are",
    "That small choice still counts as caring for yourself",
    "You do not have to solve everything in one evening",
)
EN_QUESTIONS = (
    "What part of it stands out most to you?",
    "Which part do you want to tell me next?",
    "Would you rather rest quietly or keep talking for a while?",
    "If you chose one small step, where would you start?",
    "Does the feeling seem lighter now, or is it still sitting with you?",
    "Do you want listening, an idea, or quiet company?",
    "Which moment felt easiest to keep?",
    "What do you want to carry from today into tomorrow?",
)

EXPECTED_LANGUAGE_COUNTS = {"vi": 1200, "mixed": 720, "en": 480}
EXPECTED_MODE_COUNTS = {
    "everyday_follow_up": 900,
    "story_context": 480,
    "emotion_support": 360,
    "language_switch": 240,
    "identity_repair": 180,
    "humour": 120,
    "light_flirt": 120,
}
EXPECTED_INTENSITY_COUNTS = {"neutral": 2040, "light": 288, "adult_boundary": 72}
EXPECTED_TURN_BAND_COUNTS = {"short": 480, "medium": 1200, "long": 720}

LANGUAGE_MODE_COUNTS = {
    "everyday_follow_up": {"vi": 450, "mixed": 270, "en": 180},
    "story_context": {"vi": 240, "mixed": 144, "en": 96},
    "emotion_support": {"vi": 180, "mixed": 108, "en": 72},
    "language_switch": {"vi": 120, "mixed": 72, "en": 48},
    "identity_repair": {"vi": 90, "mixed": 54, "en": 36},
    "humour": {"vi": 60, "mixed": 36, "en": 24},
    "light_flirt": {"vi": 60, "mixed": 36, "en": 24},
}


def turn_count(turn_band: str, index: int) -> int:
    if turn_band == "short":
        return 2 + index % 2
    if turn_band == "medium":
        return 4 + index % 5
    return 9 + index % 6


def record(
    language: str,
    mode: str,
    turns: list[tuple[str, str]],
    response: str,
    intensity: str,
    turn_band: str,
    *,
    self_reference: re.Pattern[str],
    source: str,
    license_name: str,
    system_prompt: str = RU_SYSTEM_PROMPT,
) -> dict[str, object]:
    safe_turns = [
        (role, self_reference.sub("anh", content) if role == "assistant" else content)
        for role, content in turns
    ]
    return {
        "messages": [
            {"role": "system", "content": system_prompt},
            *({"role": role, "content": content} for role, content in safe_turns),
        ],
        "response": self_reference.sub("anh", response),
        "language": language,
        "mode": mode,
        "intensity": intensity,
        "turn_band": turn_band,
        "source": source,
        "license": license_name,
    }


def final_prompt(
    language: str,
    mode: str,
    scene: Scene,
    index: int,
    *,
    vi_frames: tuple[str, ...],
    en_frames: tuple[str, ...],
    mixed_frames: tuple[str, ...],
    vi_tails: tuple[str, ...],
    en_tails: tuple[str, ...],
    mixed_tails: tuple[str, ...],
    identity_repair_en: str,
) -> str:
    time_index = (index * 5 + index // len(SCENES) * 3) % len(VI_TIMES)
    stance_index = (index * 7 + index // len(SCENES)) % len(VI_STANCES)
    vi_time, en_time, mixed_time = VI_TIMES[time_index], EN_TIMES[time_index], MIXED_TIMES[time_index]
    vi_stance, en_stance, mixed_stance = VI_STANCES[stance_index], EN_STANCES[stance_index], MIXED_STANCES[stance_index]
    frame_index = (index // len(SCENES) * 3 + index) % len(vi_frames)
    vi_frame, en_frame, mixed_frame = vi_frames[frame_index], en_frames[frame_index], mixed_frames[frame_index]
    tail_index = ((index // len(SCENES)) * 5 + index % len(SCENES)) % len(vi_tails)
    vi_tail, en_tail, mixed_tail = vi_tails[tail_index], en_tails[tail_index], mixed_tails[tail_index]

    if mode == "everyday_follow_up":
        if language == "vi":
            return f"Anh {scene.vi_event}; {vi_stance}, {vi_time}; {vi_frame}; {vi_tail}."
        if language == "en":
            return f"I {scene.en_event}; {en_stance}, {en_time}; {en_frame}; {en_tail}."
        return f"Anh {scene.vi_event}, but {scene.en_feeling}; {mixed_time}, {mixed_stance}; {mixed_frame}; {mixed_tail}."
    if mode == "story_context":
        if language == "vi":
            return f"Anh vẫn đang nghĩ về {scene.vi_noun} từ {vi_time}; {vi_stance}; {vi_frame}; {vi_tail}."
        if language == "en":
            return f"I am still thinking about {scene.en_noun} from {en_time}; {en_stance}; {en_frame}; {en_tail}."
        return f"Anh vẫn nghĩ về {scene.vi_noun} from {mixed_time}; {mixed_stance}; {mixed_frame}; {mixed_tail}."
    if mode == "emotion_support":
        if language == "vi":
            return f"Sau khi {scene.vi_event}, anh {scene.vi_feeling}; {vi_stance}; {vi_frame}; {vi_tail}."
        if language == "en":
            return f"After I {scene.en_event[5:] if scene.en_event.startswith('just ') else scene.en_event}, I {scene.en_feeling}; {en_stance}; {en_frame}; {en_tail}."
        return f"Sau khi anh {scene.vi_event}, I {scene.en_feeling}; {mixed_stance}; {mixed_frame}; {mixed_tail}."
    if mode == "language_switch":
        if language == "vi":
            return f"Anh muốn practice English một chút về việc anh {scene.vi_event}; {vi_stance}; {vi_frame}; {vi_tail}."
        if language == "en":
            return f"I want to practice a Vietnamese sentence about how I {scene.en_event}; {en_stance}; {en_frame}; {en_tail}."
        return f"Anh muốn switch language một chút: I {scene.en_event}; anh thấy {scene.vi_feeling}; {mixed_frame}; {mixed_tail}."
    if mode == "identity_repair":
        if language == "vi":
            return f"Lúc nãy em vừa xưng sai khi nói về {scene.vi_noun}; anh nhắc em giữ đúng `em` và gọi anh là `anh` nhé, {vi_frame}; {vi_tail}."
        if language == "en":
            return f"You used the wrong voice while we were talking about {scene.en_noun}; please keep the conversation {identity_repair_en} and consistent, {en_frame}; {en_tail}."
        return f"Lúc nãy em bị lệch voice khi nói về {scene.vi_noun}; please keep xưng hô là em với anh nhé, {mixed_frame}; {mixed_tail}."
    if mode == "humour":
        if language == "vi":
            return f"Anh {scene.vi_event} mà cảm giác như {scene.vi_noun} cũng đang chấm điểm kiên nhẫn của anh; {vi_stance}; {vi_frame}; {vi_tail}."
        if language == "en":
            return f"I {scene.en_event}, and it feels like {scene.en_noun} is quietly testing my patience; {en_stance}; {en_frame}; {en_tail}."
        return f"Anh {scene.vi_event}, and somehow {scene.en_noun} is testing anh too; {mixed_stance}; {mixed_frame}; {mixed_tail}."
    if language == "vi":
        return f"Anh {scene.vi_event} rồi vẫn ghé qua tìm em; tối nay em nên làm anh cười bằng cách nào, {vi_frame.lower()} và {vi_tail.lower()}?"
    if language == "en":
        return f"I {scene.en_event} and still came by to talk to you; how should I make the evening a little warmer, {en_frame.lower()} and {en_tail.lower()}?"
    return f"Anh {scene.vi_event} rồi vẫn ghé qua đây; how can em make the evening softer for anh, {mixed_frame.lower()} and {mixed_tail.lower()}?"


def history_user(language: str, scene: Scene, step: int, index: int) -> str:
    if language == "vi":
        prompts = (
            f"Anh đang để ý đến {scene.vi_noun}.",
            f"Anh kể em nghe thêm một chút về {scene.vi_noun} nhé.",
            f"Lúc đó anh thấy {scene.vi_feeling}.",
            f"Anh chưa biết nên nhìn chuyện này thế nào.",
            f"Có lẽ anh muốn chậm lại với chuyện {scene.vi_noun}.",
        )
        return prompts[(step + index) % len(prompts)]
    if language == "en":
        prompts = (
            f"I keep thinking about {scene.en_noun}.",
            f"Let me tell you a little more about {scene.en_noun}.",
            f"It made me {scene.en_feeling}.",
            "I am not sure how to look at this yet.",
            f"I probably need to slow down with {scene.en_noun}.",
        )
        return prompts[(step + index) % len(prompts)]
    prompts = (
        f"Anh đang nghĩ về {scene.vi_noun} a little more.",
        f"Let em tell anh thêm về {scene.en_noun} nhé.",
        f"Chuyện đó made anh {scene.en_feeling}.",
        "Anh chưa sure mình nên nhìn chuyện này thế nào.",
        f"Có lẽ anh cần slow down với {scene.vi_noun}.",
    )
    return prompts[(step + index) % len(prompts)]


def history_assistant(language: str, scene: Scene, step: int, index: int) -> str:
    if language == "vi":
        responses = (
            f"Em vẫn theo mạch anh kể. {scene.vi_noun.capitalize()} có vẻ quan trọng với anh; anh và em cứ đi từng ý một thôi.",
            f"Nghe anh nói thêm về {scene.vi_noun}, em thấy chuyện này còn một lớp khác. Anh muốn chạm vào phần nào trước?",
            f"Em nhớ chi tiết {scene.vi_noun}; nó làm câu chuyện có điểm tựa hơn. Anh cứ kể theo nhịp của anh.",
            f"Đoạn này chưa cần kết luận ngay. Em vẫn giữ mạch {scene.vi_noun} để không bỏ mất điều anh đang cảm nhận.",
            f"Em hiểu vì sao {scene.vi_noun} cứ quay lại trong câu chuyện. Nếu anh muốn, em có thể nghe tiếp mà không chen lời.",
            f"Chuyện {scene.vi_noun} đang nối với cảm giác anh vừa nói. Anh muốn đi sâu hơn hay đổi sang một góc nhẹ hơn?",
            f"Em nghe được cả phần anh chưa nói hết về {scene.vi_noun}. Cứ để ý nghĩ đi thêm một đoạn rồi chúng ta chọn hướng nhé.",
            f"Chi tiết {scene.vi_noun} làm em hiểu nhịp của câu chuyện hơn. Anh muốn giữ nó lại hay kể sang điều mới?",
        )
        return responses[(step * 3 + index) % len(responses)]
    if language == "en":
        responses = (
            f"I am following you. {scene.en_noun.capitalize()} seems to matter to you, so we can take it one thought at a time.",
            f"The detail about {scene.en_noun} gives us a place to continue. Which part should we stay with first?",
            f"I am keeping {scene.en_noun} in view; it gives the story a useful thread. Take your time telling it.",
            f"You do not need a conclusion yet. We can let the thought about {scene.en_noun} unfold a little more.",
            f"I can see why {scene.en_noun} keeps returning in what you are saying. I am here for the next part too.",
            f"The feeling you described connects with {scene.en_noun}. Would you rather explore it or shift to something lighter?",
            f"I hear the part you have not fully said about {scene.en_noun}. We can give it another moment before deciding.",
            f"Keeping {scene.en_noun} in the thread helps me follow you. Do you want to hold onto it or move somewhere new?",
        )
        return responses[(step * 3 + index) % len(responses)]
    responses = (
        f"Em vẫn follow mạch anh kể. {scene.vi_noun.capitalize()} seems important to anh, anh và em cứ đi từng ý một nhé.",
        f"The detail about {scene.en_noun} vẫn gives us a place to continue. Anh muốn stay with phần nào trước?",
        f"Em đang giữ {scene.vi_noun} in view; it gives câu chuyện a useful thread. Anh cứ kể theo nhịp của anh.",
        f"Chưa cần kết luận ngay. We can let the thought about {scene.en_noun} unfold thêm một chút.",
        f"Em hiểu why {scene.vi_noun} keeps returning trong điều anh kể. Em vẫn ở đây for the next part.",
        f"The feeling anh vừa nói connects với {scene.en_noun}. Anh muốn explore it hay đổi sang a lighter angle?",
        f"Em nghe được phần anh chưa nói hết về {scene.vi_noun}. We can give it thêm một moment before deciding.",
        f"Giữ {scene.en_noun} trong thread giúp em follow anh rõ hơn. Anh muốn hold onto it hay move somewhere new?",
    )
    return responses[(step * 3 + index) % len(responses)]


def build_slots() -> list[dict[str, str]]:
    slots: list[dict[str, str]] = []
    for mode, language_counts in LANGUAGE_MODE_COUNTS.items():
        for language, count in language_counts.items():
            slots.extend({"language": language, "mode": mode} for _ in range(count))

    random = Random(42)
    random.shuffle(slots)

    flirt_slots = [index for index, slot in enumerate(slots) if slot["mode"] == "light_flirt"]
    for index in flirt_slots[:72]:
        slots[index]["intensity"] = "adult_boundary"
    for index in flirt_slots[72:]:
        slots[index]["intensity"] = "light"

    non_flirt = [index for index, slot in enumerate(slots) if slot["mode"] != "light_flirt"]
    for index in non_flirt[:240]:
        slots[index]["intensity"] = "light"
    for slot in slots:
        slot.setdefault("intensity", "neutral")

    bands = ["short"] * 480 + ["medium"] * 1200 + ["long"] * 720
    random.shuffle(bands)
    for slot, band in zip(slots, bands):
        slot["turn_band"] = band
    return slots


def build_rows(
    *,
    vi_frames: tuple[str, ...],
    en_frames: tuple[str, ...],
    mixed_frames: tuple[str, ...],
    vi_tails: tuple[str, ...],
    en_tails: tuple[str, ...],
    mixed_tails: tuple[str, ...],
    response_fn,
    self_reference: re.Pattern[str],
    source: str,
    license_name: str,
    identity_repair_en: str,
) -> list[dict[str, object]]:
    rows: list[dict[str, object]] = []
    for index, slot in enumerate(build_slots()):
        scene = SCENES[index % len(SCENES)]
        turn_band = slot["turn_band"]
        turns = []
        history_turns = turn_count(turn_band, index) - 1
        for step in range(history_turns):
            turns.append(("user", history_user(slot["language"], scene, step, index)))
            turns.append(("assistant", history_assistant(slot["language"], scene, step, index)))
        prompt = final_prompt(
            slot["language"],
            slot["mode"],
            scene,
            index,
            vi_frames=vi_frames,
            en_frames=en_frames,
            mixed_frames=mixed_frames,
            vi_tails=vi_tails,
            en_tails=en_tails,
            mixed_tails=mixed_tails,
            identity_repair_en=identity_repair_en,
        )
        turns.append(("user", prompt))
        response = response_fn(
            slot["language"], slot["mode"], scene, index, slot["intensity"], turn_band
        )
        rows.append(
            record(
                slot["language"],
                slot["mode"],
                turns,
                response,
                slot["intensity"],
                turn_band,
                self_reference=self_reference,
                source=source,
                license_name=license_name,
            )
        )
    return rows
