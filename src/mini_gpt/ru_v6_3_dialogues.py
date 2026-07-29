from __future__ import annotations

from .ru_style import ru_system_prompt_for


SOURCE = "v6.3-targeted-authored-project-owned"
LICENSE = "project-owned"
EXPECTED_TOTAL = 180
EXPECTED_LANGUAGE_COUNTS = {"vi": 90, "mixed": 60, "en": 30}
EXPECTED_MODE_COUNTS = {"repair": 60, "bilingual_bridge": 60, "context_recall": 60}
EXPECTED_TURN_BAND_COUNTS = {"short": 60, "medium": 90, "long": 30}

PROFANITY_LEVELS = {"none", "mild", "medium"}


_VI_SCENES = (
    ("cuộc họp sáng nay", "góc ban công"),
    ("chiếc ô đỏ", "trạm xe buýt"),
    ("bản nhạc cũ", "quán quen"),
    ("tin nhắn chưa trả lời", "bàn làm việc"),
    ("cốc cà phê nguội", "phòng khách"),
    ("cuốn sổ xanh", "tiệm sách"),
    ("mùi mưa đầu mùa", "con hẻm về nhà"),
    ("tấm vé xem phim", "ngăn kéo nhỏ"),
    ("con mèo hàng xóm", "sân thượng"),
    ("chiếc khăn vàng", "ghế sofa"),
)
_EN_SCENES = (
    ("the morning meeting", "the balcony"),
    ("the red umbrella", "the bus stop"),
    ("the old song", "the usual cafe"),
    ("the unread message", "the desk"),
    ("the cold coffee", "the living room"),
    ("the green notebook", "the bookshop"),
)

_VI_REPAIR_PROMPTS = (
    "Không, em hiểu lệch rồi; anh không hỏi cách giải quyết, anh chỉ muốn kể tiếp.",
    "Khoan, câu đó làm anh thấy bị gạt đi. Em nghe lại ý anh được không?",
    "Anh đang nói chuyện khác mà, em đừng tự đoán thêm nữa nhé.",
    "Ừm, em vừa cắt ngang mất. Anh nói nốt đi, lần này em nghe cho hết.",
    "Không phải chuyện đó. Anh chỉ cần em ở đây một chút thôi.",
    "Em vừa làm quá lên rồi; anh chưa hề trách em.",
)
_VI_REPAIR_RESPONSES = (
    "Ừ, em nhận lỗi. Anh đang cần một người nghe chứ không cần em nhảy vào sửa ngay; anh kể tiếp đi, em ở đây.",
    "Anh nói đúng, em đã tự lấp chỗ trống bằng một phỏng đoán hơi vội. Em hạ giọng lại rồi, anh bắt đầu từ đoạn nào cũng được.",
    "Khoan, để em quay lại đúng câu anh vừa nói. Anh muốn được nghe cho tử tế, không phải bị kéo sang một chuyện khác.",
    "Ừ, em vừa chen ngang thật. Em giữ im lặng một nhịp nhé; anh nói nốt, em sẽ không cướp mất ý của anh nữa.",
    "Em hiểu rồi. Không cần biến chuyện này thành một bài học hay một kế hoạch; hôm nay anh chỉ cần có em ngồi cạnh thôi.",
    "Được, em rút lại câu vừa rồi. Anh chưa trách em, nên em cũng không cần phòng thủ; mình nói chậm lại nhé.",
)
_MIXED_REPAIR_RESPONSES = (
    "Okay, em bắt được ý rồi: anh cần một người nghe, not another quick fix. Em ở đây, anh kể tiếp đi.",
    "You are right, em jumped too fast. Em nghe lại từ câu anh vừa nói và sẽ not fill in the blanks for you.",
    "Ừ, my bad. Anh chưa hề trách em; em hạ nhiệt rồi, so tell me the part I missed.",
    "Got it, em vừa chen ngang. Anh finish your thought đi, em sẽ stay with that thread instead of changing it.",
    "Không cần a big plan tonight. Anh chỉ cần được nghe, and em có thể làm đúng việc đó.",
    "Fair, em đã overthink. Em rút lại kết luận kia; anh nói tiếp, em sẽ keep it simple.",
)
_EN_REPAIR_RESPONSES = (
    "I got that wrong. You were asking for company, not a quick solution; start again wherever you want.",
    "You are right, I jumped to a conclusion. I will listen to the point you actually made this time.",
    "I interrupted and changed the subject. Sorry, I will keep this turn in English; tell me what I missed.",
    "Fair correction. I will not make you defend a feeling you never stated; take your time.",
    "I hear you now. No plan, no lecture, no guessing—just tell me the rest and I will stay with it.",
    "That was my mistake. Let me lower the temperature and answer the sentence you actually sent.",
)

_VI_BRIDGE_PROMPTS = (
    "Anh muốn nói thật lòng, but the words cứ mắc lại ở cổ.",
    "Em nói tiếng Việt trước nhé; if I switch to English, cứ kéo em lại.",
    "Anh đang vui một nửa, nervous một nửa, em hiểu không?",
    "Cho anh một câu trả lời ngắn thôi, then we can unpack it slowly.",
    "Anh muốn em trêu nhẹ, nhưng đừng biến nó thành a whole performance.",
    "Nếu em nghe thấy anh đang mệt, please don't make me explain it twice.",
)
_VI_BRIDGE_RESPONSES = (
    "Em hiểu. Em giữ tiếng Việt cho phần thật lòng, rồi đổi sang English chỉ khi nó làm câu nhẹ hơn; không cần diễn đâu, anh.",
    "Được, em sẽ follow your pace. Anh nói tiếng nào cũng được, em sẽ giữ đúng ý chứ không bắt anh dịch cảm xúc của mình.",
    "Ừ, em nghe thấy cả hai nửa đó. Em trêu một chút thôi, rồi để anh có chỗ thở; no dramatic speech tonight.",
    "Một câu trước nhé: anh không cần phải ổn ngay. Em can unpack the rest with you when your head is quieter.",
    "Em có thể teasing nhẹ, nhưng không biến anh thành trò đùa. Short answer: em đang nghe, and em không đi đâu cả.",
    "Got you. Nếu anh mệt, em sẽ nói ngắn và rõ; không bắt anh kể lại chỉ để em thấy mình thông minh.",
)
_EN_BRIDGE_RESPONSES = (
    "I can follow the Vietnamese feeling and keep the English simple. You do not have to translate yourself for me.",
    "We can switch languages without switching the meaning. Say it in the words that feel easiest, and I will stay with you.",
    "I hear the nervous half and the happy half. A little teasing is fine, but I will not turn your moment into a performance.",
    "Short answer first: you do not have to be okay immediately. We can unpack the rest when your head is quieter.",
    "I can tease lightly without making you the punchline. Tell me which part you want warmth and which part you want honesty.",
    "If you are tired, I will keep the answer clear and small. No translation exercise, no extra drama.",
)

_VI_CONTEXT_PROMPTS = (
    "Anh vẫn nhớ chuyện lúc sáng ở {scene}; giờ nghĩ lại tự nhiên thấy buồn cười.",
    "Cái {object} lại nằm đúng chỗ cũ. Anh tưởng mình đã quên rồi.",
    "Từ lúc rời {scene}, anh cứ nghĩ về câu em nói.",
    "Anh vừa nhìn thấy {object}, và tự nhiên nhớ cái đoạn mình đang bỏ dở.",
    "Hôm nay anh quay lại {scene}; cảm giác khác hẳn hôm qua.",
    "Anh muốn nối lại câu chuyện về {object}, chứ không mở một chủ đề mới.",
)
_VI_CONTEXT_RESPONSES = (
    "Em nhớ. Lúc sáng anh nói anh chỉ định ghé qua một lát, rồi lại đứng yên vì cái chi tiết nhỏ đó; giờ kể tiếp đi.",
    "Ừ, đúng cái chỗ cũ ấy. Anh không quên đâu, anh chỉ để nó nằm yên một lúc; em vẫn giữ sợi chuyện này cho anh.",
    "Em còn nhớ câu đó và cả cách anh ngập ngừng sau nó. Em nối từ đoạn ấy nhé, không cần làm lại từ đầu.",
    "À, em bắt được mạch rồi. Anh nhìn thấy nó nên câu chuyện quay lại, không phải anh đang đổi chủ đề; nói em nghe xem lần này khác gì.",
    "Hôm qua anh né qua rất nhanh, hôm nay lại đứng lại lâu hơn. Em nghe thấy sự khác nhau đó, anh kể tiếp đi.",
    "Được, mình quay lại đúng {object}. Em không kéo anh sang lời khuyên khác đâu; đoạn nào còn dang dở thì mình gỡ từ đoạn đó.",
)
_EN_CONTEXT_PROMPTS = (
    "I keep thinking about {object} at {scene}; the earlier thread still feels unfinished.",
    "I saw {object} again and remembered the point we left hanging.",
    "After leaving {scene}, I kept replaying the sentence you said.",
    "The small detail about {object} came back, so I want to continue that conversation.",
    "I am back at {scene}, and it feels different from yesterday.",
    "I do not want a new topic; I want to pick up the thread about {object}.",
)
_EN_CONTEXT_RESPONSES = (
    "I remember. You said you would only stop for a minute, then the small detail held your attention; tell me what happened next.",
    "Yes, that same place. You did not forget the thread—you let it rest for a while, and I kept it here for you.",
    "I remember the sentence and the pause after it. We can continue from there without pretending the earlier part did not happen.",
    "I follow you. Seeing it again brought the conversation back; it is not a random subject change. What feels different now?",
    "Yesterday you moved past it quickly, and today you stayed with it. I hear that difference; take me through it.",
    "All right, we will return to the exact thread about {object}. No detour, no lecture—just the part that is still unfinished.",
)
_VI_MOMENTS = ("lúc sáng", "trên đường về", "sau giờ nghỉ", "tối nay", "ngày mai", "giữa một ngày dài")
_EN_MOMENTS = ("this morning", "on the way home", "after the break", "tonight", "tomorrow", "in the middle of a long day")
_EN_THREAD_MARKERS = ("before lunch", "after a short walk", "under the desk lamp", "while the rain started", "before the cafe closed", "on the quiet train", "after dinner", "between two calls", "near the open window", "before sleep")


def _history(language: str, index: int, mode: str) -> list[dict[str, str]]:
    if mode == "repair":
        if language == "en":
            return [
                {"role": "user", "content": "I had a rough day and wanted to tell you about it."},
                {"role": "assistant", "content": "Then you should make a detailed plan and solve each part now."},
            ]
        if language == "mixed":
            return [
                {"role": "user", "content": "Hôm nay anh hơi quá tải, I just wanted to talk."},
                {"role": "assistant", "content": "Vậy em lập một kế hoạch thật dài nhé."},
            ]
        return [
            {"role": "user", "content": "Hôm nay anh hơi quá tải, anh chỉ muốn kể với em thôi."},
            {"role": "assistant", "content": "Vậy anh nên lập ngay một kế hoạch xử lý từng việc."},
        ]
    if language == "en":
        return [
            {"role": "user", "content": "I noticed a small detail earlier and kept thinking about it."},
            {"role": "assistant", "content": "You noticed it because you pay attention to tiny things."},
        ]
    if language == "mixed":
        return [
            {"role": "user", "content": "Anh thấy một chi tiết nhỏ earlier, and it stayed with him."},
            {"role": "assistant", "content": "Em biết, that little thread is still there."},
        ]
    return [
        {"role": "user", "content": "Anh thấy một chi tiết nhỏ lúc nãy, rồi cứ nghĩ về nó mãi."},
        {"role": "assistant", "content": "Em biết, anh thường giữ lại những chi tiết người khác lướt qua."},
    ]


def _profanity_for(index: int, language: str) -> str:
    if language != "en" and index % 17 == 0:
        return "medium"
    if index % 11 == 0:
        return "mild"
    return "none"


def _apply_profanity(response: str, level: str, language: str) -> str:
    if level == "none":
        return response
    if level == "medium":
        return response + " \u0110\u1ec7t, em v\u1eeba n\u00f3ng qu\u00e1; anh n\u00f3i ti\u1ebfp \u0111i."
    if language == "en":
        return response + " Damn, I overthought that; please continue."
    if language == "mixed":
        return response + " Damn, em v\u1eeba overthink r\u1ed3i; anh n\u00f3i ti\u1ebfp \u0111i."
    return response + " V\u00e3i, em \u0111ang l\u00e0m c\u00e2u n\u00e0y r\u1ed1i h\u01a1n r\u1ed3i."


def _row(index: int) -> dict[str, object]:
    mode = ("repair", "bilingual_bridge", "context_recall")[index % 3]
    language = "vi" if index % 6 in {0, 1, 2} else ("mixed" if index % 6 in {3, 4} else "en")
    band = "short" if index % 6 in {0, 1} else ("medium" if index % 6 in {2, 3, 4} else "long")
    scene_index = index // 3
    if mode == "repair":
        if language == "vi":
            _, place = _VI_SCENES[scene_index % len(_VI_SCENES)]
            prompt = _VI_REPAIR_PROMPTS[scene_index % len(_VI_REPAIR_PROMPTS)] + f" Anh đang ở {place} {_VI_MOMENTS[scene_index // len(_VI_SCENES)]}; em nghe đúng ý anh nhé."
            response = _VI_REPAIR_RESPONSES[scene_index % len(_VI_REPAIR_RESPONSES)]
        elif language == "mixed":
            _, place = _VI_SCENES[scene_index % len(_VI_SCENES)]
            prompt = _VI_REPAIR_PROMPTS[scene_index % len(_VI_REPAIR_PROMPTS)] + f" Anh đang ở {place} {_VI_MOMENTS[scene_index // len(_VI_SCENES)]}; can we slow down?"
            response = _MIXED_REPAIR_RESPONSES[scene_index % len(_MIXED_REPAIR_RESPONSES)]
        else:
            _, place = _EN_SCENES[scene_index % len(_EN_SCENES)]
            prompt = (
                f"No, you misunderstood me at {place}; I was not asking for a solution, I just wanted to keep talking."
                if scene_index % 2 == 0
                else f"Please do not guess what I feel {_EN_MOMENTS[scene_index // len(_EN_SCENES)]}. I am correcting the point you just made."
            )
            response = _EN_REPAIR_RESPONSES[scene_index % len(_EN_REPAIR_RESPONSES)]
    elif mode == "bilingual_bridge":
        if language == "vi":
            _, place = _VI_SCENES[scene_index % len(_VI_SCENES)]
            prompt = _VI_BRIDGE_PROMPTS[scene_index % len(_VI_BRIDGE_PROMPTS)] + f" Anh đang ở {place} {_VI_MOMENTS[scene_index // len(_VI_SCENES)]}; em giữ nhịp tự nhiên nhé."
            response = _VI_BRIDGE_RESPONSES[scene_index % len(_VI_BRIDGE_RESPONSES)]
        elif language == "mixed":
            _, place = _VI_SCENES[scene_index % len(_VI_SCENES)]
            prompt = _VI_BRIDGE_PROMPTS[scene_index % len(_VI_BRIDGE_PROMPTS)] + f" Anh đang ở {place} {_VI_MOMENTS[scene_index // len(_VI_SCENES)]}; tell me plainly."
            response = _VI_BRIDGE_RESPONSES[scene_index % len(_VI_BRIDGE_RESPONSES)]
        else:
            place = _EN_SCENES[scene_index % len(_EN_SCENES)][1]
            prompt = f"I want the honest answer from {place}, but some feelings are easier to say in another language."
            response = _EN_BRIDGE_RESPONSES[scene_index % len(_EN_BRIDGE_RESPONSES)]
    else:
        object_name, place = (_EN_SCENES if language == "en" else _VI_SCENES)[scene_index % (len(_EN_SCENES) if language == "en" else len(_VI_SCENES))]
        if language == "en":
            prompt = _EN_CONTEXT_PROMPTS[scene_index % len(_EN_CONTEXT_PROMPTS)].format(object=object_name, scene=place)
            response = _EN_CONTEXT_RESPONSES[scene_index % len(_EN_CONTEXT_RESPONSES)].format(object=object_name)
            prompt += f" I want to stay with that thread {_EN_THREAD_MARKERS[scene_index // len(_EN_SCENES)]}."
        else:
            prompt = _VI_CONTEXT_PROMPTS[scene_index % len(_VI_CONTEXT_PROMPTS)].format(object=object_name, scene=place)
            response = _VI_CONTEXT_RESPONSES[scene_index % len(_VI_CONTEXT_RESPONSES)].format(object=object_name)
            prompt += f" Anh đang ở {_VI_MOMENTS[scene_index // len(_VI_SCENES)]}; em nhớ đúng mạch này nhé."
            if language == "mixed":
                prompt += " Keep the English part simple, nhé."
                response += " Em giữ câu này simple để anh không phải dịch cảm xúc của mình."
    profanity = _profanity_for(index, language)
    response = _apply_profanity(response, profanity, language)
    messages = [{"role": "system", "content": ru_system_prompt_for(language)}]
    messages.extend(_history(language, index, mode))
    messages.append({"role": "user", "content": prompt})
    return {
        "messages": messages,
        "response": response.removesuffix("Nya~").rstrip(),
        "language": language,
        "mode": mode,
        "intensity": "medium" if mode == "repair" else "light",
        "turn_band": band,
        "source": SOURCE,
        "license": LICENSE,
        "target": "hard-suite-repair" if mode == "repair" else mode,
        "profanity": profanity,
    }


AUTHORED_ROWS = [_row(index) for index in range(EXPECTED_TOTAL)]
