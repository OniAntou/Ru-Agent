"""Bounded private-Broker probes used by deterministic Harness sessions."""

from __future__ import annotations

import asyncio
import json
from collections.abc import Mapping
from typing import Any

from websockets.asyncio.client import connect


async def probe_approval_lifecycle(
    *,
    base_url: str,
    token: str,
    tool_id: str,
    tool_version: str,
    arguments: Mapping[str, object],
) -> dict[str, object]:
    """Approve one private tool request, replay it, and close its session."""

    summary: dict[str, object] = {
        "approval_required": False,
        "approval_dispatch": False,
        "approval_replay": False,
        "session_closed": False,
        "terminal_status": "unknown",
        "replay_status": "unknown",
    }
    session_id = "harness-broker-approval"
    correlation_id = "harness-broker-approval-correlation"
    request_counter = 0

    def envelope(message_type: str, payload: Mapping[str, object]) -> str:
        nonlocal request_counter
        request_counter += 1
        return json.dumps(
            {
                "protocol_version": 1,
                "message_type": message_type,
                "request_id": f"harness-broker-{request_counter}",
                "session_id": session_id,
                "correlation_id": correlation_id,
                "deadline_ms": 10_000,
                "payload": dict(payload),
            }
        )

    async def exchange(
        socket: Any,
        message_type: str,
        payload: Mapping[str, object],
    ) -> dict[str, Any]:
        await socket.send(envelope(message_type, payload))
        raw = await asyncio.wait_for(socket.recv(), timeout=10.0)
        if not isinstance(raw, str):
            raise ValueError("broker_binary_reply")
        reply = json.loads(raw)
        if not isinstance(reply, dict):
            raise ValueError("broker_invalid_reply")
        if (
            reply.get("session_id") != session_id
            or reply.get("correlation_id") != correlation_id
            or not isinstance(reply.get("payload"), dict)
        ):
            raise ValueError("broker_context_mismatch")
        return reply

    uri = base_url.replace("http://", "ws://", 1) + "/agent"
    try:
        async with connect(
            uri,
            additional_headers={"Authorization": f"Bearer {token}"},
            proxy=None,
            open_timeout=10,
            close_timeout=2,
        ) as socket:
            hello = await exchange(socket, "hello", {})
            if hello.get("message_type") != "hello_ok":
                raise ValueError("broker_hello_failed")
            opened = await exchange(
                socket,
                "session_open",
                {"scopes": ["workspace.write"], "skill_ids": []},
            )
            if (
                opened.get("message_type") != "session_opened"
                or opened["payload"].get("status") != "succeeded"
            ):
                raise ValueError("broker_session_open_failed")
            try:
                pending = await exchange(
                    socket,
                    "tool_request",
                    {
                        "tool_id": tool_id,
                        "tool_version": tool_version,
                        "arguments": dict(arguments),
                    },
                )
                pending_payload = pending["payload"]
                output = pending_payload.get("output")
                approval_id = output.get("approval_id") if isinstance(output, dict) else None
                summary["approval_required"] = (
                    pending_payload.get("status") == "approval_required"
                    and isinstance(approval_id, str)
                    and bool(approval_id)
                )
                if not summary["approval_required"]:
                    raise ValueError("broker_approval_not_required")
                approved = await exchange(
                    socket,
                    "approval_response",
                    {"approval_id": approval_id, "approved": True},
                )
                terminal_status = approved["payload"].get("status")
                summary["terminal_status"] = terminal_status or "unknown"
                summary["approval_dispatch"] = terminal_status == "succeeded"
                replay = await exchange(
                    socket,
                    "approval_response",
                    {"approval_id": approval_id, "approved": True},
                )
                replay_payload = replay["payload"]
                summary["replay_status"] = replay_payload.get("status") or "unknown"
                summary["approval_replay"] = (
                    replay_payload.get("status") == "denied"
                    and replay_payload.get("error_code") == "approval_stale"
                )
            finally:
                closed = await exchange(socket, "session_close", {})
                summary["session_closed"] = (
                    closed.get("message_type") == "skill_result"
                    and closed["payload"].get("status") == "succeeded"
                )
    except Exception as error:
        summary["error_type"] = type(error).__name__
    return summary
