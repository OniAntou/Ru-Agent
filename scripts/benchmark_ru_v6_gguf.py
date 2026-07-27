from __future__ import annotations

import json
import sys
from pathlib import Path
from time import perf_counter

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.llama_cpp_runtime import LlamaCppRuntime  # noqa: E402
from mini_gpt.ru_style import RU_SYSTEM_PROMPT  # noqa: E402


PROMPTS = (
    "Chào anh, em là Ru.",
    "Anh đang mệt một chút, em nói chuyện với anh nha.",
)


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    runtime = LlamaCppRuntime(project_root=PROJECT_ROOT)
    try:
        for index, prompt in enumerate(PROMPTS):
            started = perf_counter()
            reply = runtime.generate(
                (("system", RU_SYSTEM_PROMPT), ("user", prompt)),
                max_new_tokens=128,
                temperature=0.7,
                top_k=20,
            )
            print(
                json.dumps(
                    {
                        "phase": "cold" if index == 0 else "warm",
                        "prompt": prompt,
                        "seconds": round(perf_counter() - started, 2),
                        "reply": reply,
                    },
                    ensure_ascii=False,
                )
            )
    finally:
        runtime.stop()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
