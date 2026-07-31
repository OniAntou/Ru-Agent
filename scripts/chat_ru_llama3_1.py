from __future__ import annotations

import argparse
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT / "src"))

from mini_gpt.llama3_1_runtime import generate_llama_response_hf  # noqa: E402
from mini_gpt.ru_style import RU_SYSTEM_PROMPT  # noqa: E402


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Interactive chat with Ru Llama 3.1 QLoRA adapter")
    parser.add_argument("--base-model", default=str(PROJECT_ROOT / "models" / "llama3.1-8b-instruct-hf"))
    parser.add_argument("--adapter-dir", default=str(PROJECT_ROOT / "checkpoints" / "ru-llama3.1-8b-instruct-v6"))
    parser.add_argument("--prompt", type=str, help="Single prompt mode")
    parser.add_argument("--max-history-turns", type=int, default=20, help="Keep at most this many user/assistant turns")
    return parser


def trim_history(messages: list[tuple[str, str]], max_turns: int) -> list[tuple[str, str]]:
    if max_turns <= 0:
        return messages[:1]
    turns = messages[1:]
    if len(turns) > max_turns * 2:
        turns = turns[-(max_turns * 2):]
    return [messages[0], *turns]


def main() -> int:
    args = build_parser().parse_args()
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")

    if args.prompt:
        messages = (("system", RU_SYSTEM_PROMPT), ("user", args.prompt))
        try:
            response = generate_llama_response_hf(args.base_model, messages, args.adapter_dir)
        except Exception as error:
            print(f"Generation failed: {type(error).__name__}: {error}", file=sys.stderr)
            return 1
        print(f"\n--- User ---\n{args.prompt}\n\n--- Ru (Llama 3.1 8B v6) ---\n{response}\n")
        return 0

    print("=== Ru Llama 3.1 8B Interactive Chat ===")
    print("Go ahead and type your prompt (type 'exit' or 'quit' to stop):\n")

    history: list[tuple[str, str]] = [("system", RU_SYSTEM_PROMPT)]

    while True:
        try:
            user_input = input("You: ").strip()
        except (EOFError, KeyboardInterrupt):
            print("\nExiting chat...")
            break

        if not user_input:
            continue
        if user_input.lower() in ("exit", "quit"):
            print("Exiting chat...")
            break

        history.append(("user", user_input))
        messages = trim_history(history, args.max_history_turns)
        try:
            response = generate_llama_response_hf(args.base_model, tuple(messages), args.adapter_dir)
        except Exception as error:
            print(f"Generation failed: {type(error).__name__}: {error}", file=sys.stderr)
            print("Continuing chat without this reply...\n")
            history.pop()
            continue
        history = [*messages, ("assistant", response)]
        print(f"Ru: {response}\n")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
