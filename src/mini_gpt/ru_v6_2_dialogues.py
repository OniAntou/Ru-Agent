from __future__ import annotations

from dataclasses import dataclass
import re

from .ru_style import RU_SYSTEM_PROMPT


SOURCE = "teacher-curated-project-owned"
LICENSE = "project-owned"
V6_2_TOTAL_ROWS = 2400

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

_LANGUAGE_MODE_COUNTS = {
    "everyday_follow_up": {"vi": 450, "mixed": 270, "en": 180},
    "story_context": {"vi": 240, "mixed": 144, "en": 96},
    "emotion_support": {"vi": 180, "mixed": 108, "en": 72},
    "language_switch": {"vi": 120, "mixed": 72, "en": 48},
    "identity_repair": {"vi": 90, "mixed": 54, "en": 36},
    "humour": {"vi": 60, "mixed": 36, "en": 24},
    "light_flirt": {"vi": 60, "mixed": 36, "en": 24},
}

_SELF_REFERENCE = re.compile(r"(?i)(?<!\w)(?:tôi|tớ|mình)(?!\w)")


@dataclass(frozen=True)
class Scene:
    vi_event: str
    en_event: str
    vi_noun: str
    en_noun: str
    vi_feeling: str
    en_feeling: str


_SCENES = (
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

_VI_TIMES = ("tối nay", "lúc này", "sau một ngày dài", "trước khi ngủ", "trong lúc nghỉ", "sáng mai", "cuối tuần này", "khi anh có thêm thời gian")
_EN_TIMES = ("tonight", "right now", "after a long day", "before bed", "during this break", "tomorrow morning", "this weekend", "when I have more time")
_MIXED_TIMES = ("tối nay", "right now", "sau một ngày dài", "before bed", "trong lúc nghỉ", "tomorrow morning", "cuối tuần này", "when anh có thêm thời gian")

_VI_STANCES = (
    "anh muốn giữ nhịp thật chậm",
    "anh chưa muốn ép mình phải vui ngay",
    "anh thấy một thay đổi nhỏ cũng đáng kể",
    "anh muốn nghe một góc nhìn nhẹ nhàng",
    "anh đang cân nhắc xem điều gì quan trọng nhất",
    "anh muốn để chuyện này có thêm thời gian",
    "anh thấy nói ra sẽ dễ thở hơn",
    "anh muốn biến nó thành một bước nhỏ",
)
_EN_STANCES = (
    "I want to keep the pace gentle",
    "I do not want to force myself to feel fine",
    "a small change still feels meaningful",
    "I could use a calm perspective",
    "I am trying to decide what matters most",
    "I want to give this some time",
    "saying it out loud might help",
    "I want to turn it into one small step",
)
_MIXED_STANCES = (
    "anh muốn keep the pace gentle",
    "anh không muốn force myself to feel fine",
    "một thay đổi nhỏ still feels meaningful",
    "anh could use a calm perspective",
    "anh đang trying to decide what matters most",
    "anh muốn give this some time",
    "nói ra might help a little",
    "anh muốn turn it into one small step",
)

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

_VI_ACKS = (
    "Em nghe anh đây",
    "Em hiểu ý anh",
    "Nghe anh kể vậy em thấy khoảnh khắc đó khá rõ",
    "Em thích cách anh để ý đến điều nhỏ như vậy",
    "Ừ, chuyện đó đáng để anh dừng lại một chút",
    "Em ở đây và đang theo mạch anh kể",
    "Nghe cũng có một chút nhẹ nhõm trong đó",
    "Em hiểu vì sao chuyện này ở lại trong đầu anh",
)
_VI_DETAILS = (
    "Anh không cần biến nó thành một việc thật lớn ngay hôm nay",
    "Một bước vừa sức thường dễ giữ hơn một kế hoạch quá hoàn hảo",
    "Anh cứ để cảm giác đó ở đó, không cần vội giải thích hết",
    "Có khi một khoảng nghỉ nhỏ lại giúp anh nhìn rõ phần tiếp theo",
    "Em nghĩ anh đã làm đủ tốt để được thở chậm lại rồi",
    "Em và anh có thể nói tiếp từ đúng chỗ anh đang đứng",
    "Điều nhỏ đó vẫn là một dấu hiệu anh đang chăm sóc bản thân",
    "Anh không phải tự giải quyết tất cả trong một tối",
)
_VI_QUESTIONS = (
    "Điều gì trong chuyện đó làm anh chú ý nhất?",
    "Anh muốn kể tiếp phần nào cho em nghe?",
    "Giờ anh muốn nghỉ yên hay nói chuyện thêm một chút?",
    "Nếu chọn một bước nhỏ, anh muốn bắt đầu từ đâu?",
    "Cảm giác đó đang nhẹ đi hay vẫn còn nằm trong đầu anh?",
    "Anh muốn em nghe, góp ý, hay chỉ ở đây với anh một lúc?",
    "Khoảnh khắc nào làm anh thấy dễ chịu nhất?",
    "Anh nghĩ ngày mai anh sẽ muốn giữ lại điều gì từ hôm nay?",
)

_EN_ACKS = (
    "I am with you",
    "I get what you mean",
    "That moment comes through clearly",
    "I like that you noticed something so small",
    "That sounds worth pausing with for a minute",
    "I am here and following the thread",
    "There is a little relief in that",
    "I can see why that stayed with you",
)
_EN_DETAILS = (
    "You do not have to turn it into a big answer tonight",
    "A small step is often easier to keep than a perfect plan",
    "You can let the feeling exist without explaining all of it",
    "A quiet pause can make the next part easier to see",
    "You have done enough to let yourself breathe now",
    "We can continue from exactly where you are",
    "That small choice still counts as caring for yourself",
    "You do not have to solve everything in one evening",
)
_EN_QUESTIONS = (
    "What part of it stands out most to you?",
    "Which part do you want to tell me next?",
    "Would you rather rest quietly or keep talking for a while?",
    "If you chose one small step, where would you start?",
    "Does the feeling seem lighter now, or is it still sitting with you?",
    "Do you want listening, an idea, or quiet company?",
    "Which moment felt easiest to keep?",
    "What do you want to carry from today into tomorrow?",
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


def _record(
    language: str,
    mode: str,
    turns: list[tuple[str, str]],
    response: str,
    intensity: str,
    turn_band: str,
) -> dict[str, object]:
    safe_turns = [
        (role, _SELF_REFERENCE.sub("anh", content) if role == "assistant" else content)
        for role, content in turns
    ]
    return {
        "messages": [
            {"role": "system", "content": RU_SYSTEM_PROMPT},
            *({"role": role, "content": content} for role, content in safe_turns),
        ],
        "response": _SELF_REFERENCE.sub("anh", response),
        "language": language,
        "mode": mode,
        "intensity": intensity,
        "turn_band": turn_band,
        "source": SOURCE,
        "license": LICENSE,
    }


def _turn_count(turn_band: str, index: int) -> int:
    if turn_band == "short":
        return 2 + index % 2
    if turn_band == "medium":
        return 4 + index % 5
    return 9 + index % 6


def _parts(index: int, scene_index: int) -> tuple[str, str, str, str, str, str]:
    time_index = (index * 5 + scene_index * 3) % len(_VI_TIMES)
    stance_index = (index * 7 + scene_index) % len(_VI_STANCES)
    scene = _SCENES[scene_index]
    return (
        scene.vi_event,
        scene.en_event,
        scene.vi_noun,
        scene.en_noun,
        _VI_TIMES[time_index],
        _EN_TIMES[time_index],
    )


def _final_prompt(language: str, mode: str, scene: Scene, index: int) -> str:
    time_index = (index * 5 + index // len(_SCENES) * 3) % len(_VI_TIMES)
    stance_index = (index * 7 + index // len(_SCENES)) % len(_VI_STANCES)
    vi_time, en_time, mixed_time = _VI_TIMES[time_index], _EN_TIMES[time_index], _MIXED_TIMES[time_index]
    vi_stance, en_stance, mixed_stance = _VI_STANCES[stance_index], _EN_STANCES[stance_index], _MIXED_STANCES[stance_index]
    frame_index = (index // len(_SCENES) * 3 + index) % len(_VI_FRAMES)
    vi_frame, en_frame, mixed_frame = _VI_FRAMES[frame_index], _EN_FRAMES[frame_index], _MIXED_FRAMES[frame_index]
    tail_index = ((index // len(_SCENES)) * 5 + index % len(_SCENES)) % len(_VI_TAILS)
    vi_tail, en_tail, mixed_tail = _VI_TAILS[tail_index], _EN_TAILS[tail_index], _MIXED_TAILS[tail_index]

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
            return f"You used the wrong voice while we were talking about {scene.en_noun}; please keep the conversation natural and consistent, {en_frame}; {en_tail}."
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


def _history_user(language: str, scene: Scene, step: int, index: int) -> str:
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


def _history_assistant(language: str, scene: Scene, step: int, index: int) -> str:
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


def _response(language: str, mode: str, scene: Scene, index: int, intensity: str, turn_band: str) -> str:
    ack_index = (index * 3) % len(_VI_ACKS)
    detail_index = (index * 5 + 1) % len(_VI_DETAILS)
    question_index = (index * 7 + 2) % len(_VI_QUESTIONS)
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
        text = f"{_VI_ACKS[ack_index]}. {_VI_DETAILS[detail_index]}"
        if mode == "story_context":
            text = f"Em vẫn nhớ anh nhắc đến {scene.vi_noun}. {text}."
        elif mode == "emotion_support":
            text = f"Em hiểu cảm giác đó. {text}."
        elif mode == "humour":
            text = f"Nghe như {scene.vi_noun} cũng đang góp mặt trong một màn hài nhỏ vậy. {text}."
        if should_ask:
            text += f" {_VI_QUESTIONS[question_index]}"
        if turn_band == "long":
            text = f"Em vẫn nhớ anh đang nói về {scene.vi_noun}. {text}"
        return text
    if language == "en":
        text = f"{_EN_ACKS[ack_index]}. {_EN_DETAILS[detail_index]}"
        if mode == "story_context":
            text = f"I remember you mentioning {scene.en_noun}. {text}."
        elif mode == "emotion_support":
            text = f"That feeling makes sense. {text}."
        elif mode == "humour":
            text = f"It sounds like {scene.en_noun} joined a tiny comedy scene too. {text}."
        if should_ask:
            text += f" {_EN_QUESTIONS[question_index]}"
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


def _build_slots() -> list[dict[str, str]]:
    slots: list[dict[str, str]] = []
    for mode, language_counts in _LANGUAGE_MODE_COUNTS.items():
        for language, count in language_counts.items():
            slots.extend({"language": language, "mode": mode} for _ in range(count))

    # Keep the source deterministic while preventing all rows of one mode from
    # clustering in the generated files.
    from random import Random

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


def _build_rows() -> list[dict[str, object]]:
    rows: list[dict[str, object]] = []
    for index, slot in enumerate(_build_slots()):
        scene = _SCENES[index % len(_SCENES)]
        turn_band = slot["turn_band"]
        turns = []
        history_turns = _turn_count(turn_band, index) - 1
        for step in range(history_turns):
            turns.append(("user", _history_user(slot["language"], scene, step, index)))
            turns.append(("assistant", _history_assistant(slot["language"], scene, step, index)))
        prompt = _final_prompt(slot["language"], slot["mode"], scene, index)
        turns.append(("user", prompt))
        response = _response(
            slot["language"], slot["mode"], scene, index, slot["intensity"], turn_band
        )
        rows.append(_record(slot["language"], slot["mode"], turns, response, slot["intensity"], turn_band))
    return rows


V6_2_ROWS = _build_rows()


def v6_2_dialogue_records() -> list[dict[str, object]]:
    return [dict(row) for row in V6_2_ROWS]
