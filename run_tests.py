"""Quick test runner for round 2 fixes."""
import subprocess
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parent
DEFAULT_TIMEOUT_SECONDS = 600


def run_tests(targets: list[str] | None = None, *, timeout: int = DEFAULT_TIMEOUT_SECONDS) -> int:
    result = subprocess.run(
        [
            sys.executable, "-m", "pytest",
            *(targets if targets is not None else ["tests/"]),
            "-v",
            "--tb=short",
        ],
        cwd=str(PROJECT_ROOT),
        capture_output=True,
        text=True,
        timeout=timeout,
    )

    print("STDOUT:")
    print(result.stdout)
    if result.stderr:
        print("STDERR:")
        print(result.stderr)
    print(f"EXIT CODE: {result.returncode}")
    return result.returncode


if __name__ == "__main__":
    sys.exit(run_tests(sys.argv[1:]))
