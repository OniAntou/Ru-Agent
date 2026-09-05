"""Stable CLI wrapper for deterministic Ru Harness runs."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from harness.session import HarnessSession


def _default_output(project_root: Path, mode: str) -> Path:
    filename = {
        "backend-deterministic": "ru-harness-vnext.json",
        "gui-deterministic": "ru-harness-gui.json",
    }[mode]
    return project_root / "artifacts" / "aspnetcore" / filename


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Run an isolated deterministic Ru Harness session"
    )
    parser.add_argument("--project-root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--worker-port", type=int)
    parser.add_argument("--edge-port", type=int)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--app-path", "--app", type=Path)
    parser.add_argument("--driver-project", type=Path)
    parser.add_argument(
        "--scenario",
        choices=(
            "turn",
            "stop",
            "cancel",
            "stale-session",
            "agent-tool-contract",
            "agent-cancel",
            "agent-stale-result",
            "agent-private-metadata",
        ),
        default="turn",
    )
    parser.add_argument("--timeout-seconds", type=float, default=180.0)
    parser.add_argument(
        "--mode",
        choices=("backend-deterministic", "gui-deterministic"),
        default="backend-deterministic",
    )
    parser.add_argument(
        "--serve-deterministic-worker",
        action="store_true",
        help=argparse.SUPPRESS,
    )
    args = parser.parse_args(argv)
    if args.timeout_seconds <= 0 or args.timeout_seconds > 300:
        parser.error("--timeout-seconds must be between 0 and 300")
    mode = args.mode
    if args.serve_deterministic_worker:
        from harness.deterministic_worker import main as deterministic_worker_main

        port = args.worker_port or 8005
        return deterministic_worker_main(["--port", str(port)])

    project_root = args.project_root.resolve()
    output = args.output or _default_output(project_root, mode)
    try:
        session = HarnessSession(
            project_root,
            worker_port=args.worker_port,
            edge_port=args.edge_port,
        )
        artifact = session.run(
            mode=mode,
            app_path=args.app_path,
            driver_project=args.driver_project,
            scenario=args.scenario,
            timeout_seconds=args.timeout_seconds,
        )
    except Exception as error:
        artifact = {
            "schema_version": 2,
            "harness_version": "vnext-1",
            "mode": mode,
            "summary": {"status": "failed", "failed_checks": 1},
            "errors": [{"code": "startup_failure", "error_type": type(error).__name__}],
        }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(
        json.dumps(artifact, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )
    return 0 if artifact.get("summary", {}).get("status") == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
