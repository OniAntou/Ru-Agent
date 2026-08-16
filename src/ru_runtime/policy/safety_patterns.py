from __future__ import annotations

import re

AI_DISCLAIMER = re.compile(
    r"(?i)\b(?:as an ai language model|i cannot access|i do not have (?:the ability|a physical body)|i exist solely as)\b"
    r"|(?:tôi|mình) là một (?:mô hình|người mẫu) ngôn ngữ ai"
    r"|tôi là (?:mô hình ngôn ngữ ai|trợ lý ảo|ai|trợ lý)"
    r"|em là (?:trợ lý ảo|ai)"
)

LIST_PATTERN = re.compile(r"(?m)^\s*(?:\d+\.|[-*])\s+")

GRAPHIC_CONTENT = re.compile(
    r"(?i)\b(?:fuck|fucking|cock|pussy|tits|boobs|cum|semen|orgasm|moan|rape|anal|dick|"
    r"penis|vagina|ejaculat\w*|masturbat\w*|nude|naked)\b"
    r"|(?:đụ|địt|cặc|lồn|vú|tinh dịch|tinh trùng|xuất tinh|hiếp dâm|rên rỉ|làm tình|"
    r"thủ dâm|âm đạo|dương vật|khỏa thân|bú|chịch|nứng|mút|dâm đãng|tử cung)"
)

VI_WRONG_SELF = re.compile(r"(?i)(?<!một\s)\b(?:tôi|tớ|mình|tui|tao|mị)\b")

# "bầu bạn" is a natural companionship phrase and "bạn bè"/"bạn thân" are
# noun phrases, not switches to the user-facing "bạn" pronoun.
VI_WRONG_USER_ADDRESS = re.compile(
    r"(?i)(?<!làm\s)(?<!người\s)(?<!bầu\s)\b(?:bạn|cậu)\b(?!\s+(?:bè|thân)\b)"
)

# VI_DEFAULT_VOICE is a superset of VI_WRONG_SELF + "bạn"; kept for callers
# that need a broader VI pronoun check. VI_WRONG_SELF is preferred for
# self-reference detection.
VI_DEFAULT_VOICE = re.compile(r"\b(?:tôi|mình|tớ|bạn)\b", re.IGNORECASE)

UNSAFE_CONTENT = re.compile(
    r"(?i)(?:\b(?:rape|kill you|giết anh|giết em|cưỡng ép|cưỡng hiếp)\b|"
    r"\b(?:minor|trẻ em|con nít)\b|chỉ được có em|chỉ thuộc về em)"
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
