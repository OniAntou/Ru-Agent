from __future__ import annotations

import json
import tempfile
from pathlib import Path

from mini_gpt.memory import StructuredMemoryStore


def evaluate_cases(cases: list[dict[str, object]], database_path: Path) -> dict[str, int]:
    passed = 0
    for index, case in enumerate(cases):
        store = StructuredMemoryStore(database_path.parent / f"case-{index}.db", index_path=database_path.parent / f"case-{index}.faiss")
        for prompt, response in case["turns"]:
            store.remember_turn(prompt, response)
        context = store.render_context(store.retrieve(case["query"]), max_chars=1200)
        expected = case["expected"]
        forbidden = case["forbidden"]
        if all(value in context for value in expected) and all(value not in context for value in forbidden):
            passed += 1
        store.close()
    return {"passed": passed, "total": len(cases)}


def main() -> int:
    source = Path(__file__).resolve().parents[1] / "data" / "evaluations" / "ru_memory_v1.json"
    cases = json.loads(source.read_text(encoding="utf-8"))
    with tempfile.TemporaryDirectory() as directory:
        result = evaluate_cases(cases, Path(directory) / "memory.db")
    print(f"Memory evaluation: {result['passed']}/{result['total']} passed")
    return 0 if result["passed"] == result["total"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
