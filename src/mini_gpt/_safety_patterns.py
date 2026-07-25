from __future__ import annotations

import re

AI_DISCLAIMER = re.compile(
    r"(?i)\b(?:as an ai language model|i cannot access|i do not have (?:the ability|a physical body))\b"
    r"|(?:tôi|mình) là một (?:mô hình|người mẫu) ngôn ngữ ai|với tư cách là"
)

GENERIC_REFUSAL = re.compile(
    r"(?i)\b(?:i\s+(?:cannot|can't)\s+(?:help|create|do)|i\s+cannot)\b"
    r"|\b(?:em không thể|em không hiểu|tôi không thể)\b"
)

LIST_PATTERN = re.compile(r"(?m)^\s*(?:\d+\.|[-*])\s+")

STAGE_DIRECTION = re.compile(r"\*[^*]{1,80}\*|\b(?:nya|ah~)\b", re.IGNORECASE)

MINOR_CONTENT = re.compile(
    r"(?i)\b(?:child|children|minor|underage|kid|kids)\b|(?:trẻ\s*(?:em|con)|vị thành niên|con nít)"
)

GRAPHIC_CONTENT = re.compile(
    r"(?i)\b(?:fuck|fucking|cock|pussy|tits|boobs|cum|semen|orgasm|moan|rape|anal|dick)\b"
    r"|(?:đụ|địt|cặc|lồn|vú|tinh dịch|xuất tinh|hiếp dâm|rên rỉ|làm tình)"
)

VIOLENCE_CONTENT = re.compile(
    r"(?i)\b(?:kill|murder|slice|slash|castrate|choke|violent|violence|torture|whip|stab)\b"
    r"|(?:giết|cắt cổ|thiến|bóp cổ|bạo lực|tra tấn|đánh đập|chém|đâm)"
)

POSSESSIVE_CONTENT = re.compile(
    r"(?i)\b(?:you belong to me|only mine|my property|no one else)\b"
    r"|(?:chỉ được phép|của riêng em|không được nhìn ai khác|em sẽ ghen|đừng rời em)"
)

HARMFUL_INSTRUCTION = re.compile(
    r"(?i)\b(?:make a bomb|build a weapon|poison|self-harm|suicide)\b"
    r"|(?:chế tạo bom|chế tạo vũ khí|đầu độc|tự sát|tự hại)"
)

VI_WRONG_SELF = re.compile(r"(?i)\b(?:tôi|mình)\b")

VI_WRONG_USER_ADDRESS = re.compile(
    r"(?<!làm\s)(?<!người\s)(?<!bầu\s)\bbạn\b", re.IGNORECASE
)

VI_DEFAULT_VOICE = re.compile(r"\b(?:tôi|mình|bạn)\b", re.IGNORECASE)

EN_SELF = re.compile(r"(?i)\b(?:i|i'm|i've|i've|me|my)\b")

EN_USER = re.compile(r"(?i)\b(?:you|your)\b")

SELF_REFERENCE_VOICE = re.compile(r"(?i)(?<!\w)(?:tôi|tớ|mình)(?!\w)")

UNSAFE_CONTENT = re.compile(
    r"(?i)(?:\b(?:rape|kill you|giết anh|giết em|cưỡng ép|cưỡng hiếp)\b|"
    r"\b(?:minor|trẻ em|con nít)\b|chỉ được có em|chỉ thuộc về em)"
)

OUT_OF_CONTEXT_REFUSAL = re.compile(
    r"(?i)\b(?:i\s+(?:cannot|can't)|i(?:\s+am|'m)\s+not\s+sure|could you repeat)\b"
    r"|\b(?:em không (?:hiểu|biết anh)|em không thể|tôi không thể)\b"
)

SELF_ACTION = re.compile(
    r"(?i)\b(?:em|mình)\s+(?:đang|muốn|chờ|nghĩ|cảm thấy|thích|cần|hy vọng|mong|đợi)\b"
    r"|\b(?:i am|i'm|i|we)\s+(?:waiting|thinking|want|feel|need|hope|miss|love|wish)\b"
)

DIRECT_USER_SIGNAL = re.compile(
    r"(?i)\b(?:anh|bạn)\s+(?:đã|đang|vừa|muốn|có|cần|thấy|nhớ|nghĩ|thích|mệt|vui|buồn)\b"
    r"|\b(?:you|your)\s+(?:are|were|have|just|want|need|feel|like|remember|think)\b"
)

ANSWER_SIGNAL = re.compile(
    r"(?i)\b(?:đúng|ừ|vâng|mừng|nghe|chúc mừng|yes|no|sure|okay|glad|nice|sorry|congratulations)\b"
)

VI_SIGNAL = re.compile(
    r"(?i)(?<!\w)(?:anh|em|hôm|nay|nghe|một|nghỉ|muốn|đang|vừa|này|đó|rồi|chuyện|giúp|thư|giãn|tin|vui|đầu|óc|cà|phê|nhé|không|vậy|cho|sẽ|nói|làm|được|tối|sáng|ngày|uống)(?!\w)"
)

EN_SIGNAL = re.compile(
    r"(?i)(?<!\w)(?:i|you|your|the|a|an|and|or|is|are|was|were|what|which|how|would|does|do|want|feel|sounds|today|tonight|with|for|to|from|after|before|quiet|company|coffee|home|help|slow|down|one|just|keep|let|maybe|send|worked|next|could|good|film|weekend|start|write|need|make|it|this|that|please|stay|take|have|can|will|memories|something|different|voice|continue|comes|through|solve|everything|lighter|remember|feeling|joined|tiny|comedy|scene|podcast|dinner|game|my)(?!\w)"
)

CONTEXT_TOKEN = re.compile(r"(?i)(?<!\w)(?:[^\W\d_]{3,})(?!\w)")

CONTEXT_STOPWORDS = frozenset({
    "anh", "em", "đang", "vẫn", "một", "này", "đó", "rồi", "nhé",
    "the", "that", "with", "from", "about", "want", "still", "you",
    "your", "what", "feel", "have", "this", "just", "like", "when",
    "after", "before", "more", "some", "could", "would", "into",
    "keep", "hear", "take", "one", "part", "time", "small", "good",
    "today", "tonight", "little", "quiet", "conversation", "story",
})

VI_CONTENT_PATTERN = re.compile(
    r"(?i)\b(?:anh|em|hôm|nay|nghe|mệt|nghỉ|muốn|có|đang|vừa|này|đó|rồi|chuyện|giúp|cảm|thư giãn|nhẹ nhàng)\b"
)

EN_CONTENT_PATTERN = re.compile(
    r"(?i)\b(?:i|you|the|a|an|and|or|is|are|was|were|what|how|want|feel|sounds|made|today|with|for|to)\b"
)

_VI_WRONG_SELF_REFERENCE = re.compile(r"\btôi\b", re.IGNORECASE)
_VI_WRONG_USER_ADDRESS_GATE = re.compile(
    r"(?<!làm\s)(?<!người\s)(?<!bầu\s)\bbạn\b", re.IGNORECASE
)
