from __future__ import annotations

from harness.ports import PortLease, wait_for_ports_closed


def test_port_lease_uses_free_loopback_port_and_releases_it() -> None:
    lease = PortLease.acquire()
    try:
        assert lease.host == "127.0.0.1"
        assert lease.port > 0
    finally:
        port = lease.port
        lease.close()

    assert wait_for_ports_closed([port]) is True
