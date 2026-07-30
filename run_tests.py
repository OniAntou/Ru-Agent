"""Quick test runner for round 2 fixes."""
import subprocess
import sys

result = subprocess.run(
    [
        sys.executable, "-m", "pytest",
        "tests/test_openai_runtime.py",
        "tests/test_memory_extraction.py",
        "tests/test_reply_orchestration.py",
        "-v",
        "--tb=short",
    ],
    cwd=r"D:\LLM",
    capture_output=True,
    text=True,
    timeout=120,
)

print("STDOUT:")
print(result.stdout)
if result.stderr:
    print("STDERR:")
    print(result.stderr)
print(f"EXIT CODE: {result.returncode}")
