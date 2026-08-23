"""Loopback port leases used by an isolated Harness session."""

from __future__ import annotations

import socket
import time
from dataclasses import dataclass


class PortConflictError(RuntimeError):
    """Raised when an explicitly requested loopback port is already bound."""


@dataclass(slots=True)
class PortLease:
    host: str
    port: int
    _socket: socket.socket | None

    @classmethod
    def acquire(cls, host: str = "127.0.0.1", port: int = 0) -> PortLease:
        listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        try:
            listener.bind((host, port))
            listener.listen(1)
            selected_port = int(listener.getsockname()[1])
        except OSError as error:
            listener.close()
            if port:
                raise PortConflictError(f"loopback port {port} is unavailable") from error
            raise
        return cls(host=host, port=selected_port, _socket=listener)

    def handoff(self) -> int:
        """Release the reservation immediately before the owned server starts."""

        self.close()
        return self.port

    def close(self) -> None:
        listener = self._socket
        self._socket = None
        if listener is not None:
            listener.close()


def wait_for_ports_closed(ports: list[int] | tuple[int, ...], timeout_seconds: float = 5.0) -> bool:
    """Return true once no listed TCP listener accepts a loopback connection."""

    pending = {int(port) for port in ports if int(port) > 0}
    deadline = time.monotonic() + max(0.0, timeout_seconds)
    while pending:
        for port in tuple(pending):
            try:
                with socket.create_connection(("127.0.0.1", port), timeout=0.2):
                    pass
            except OSError:
                pending.remove(port)
        if not pending or time.monotonic() >= deadline:
            break
        time.sleep(0.05)
    return not pending
