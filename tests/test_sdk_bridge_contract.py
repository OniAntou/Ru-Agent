"""The TypeScript SDK and its test bridge must match the RPC methods the CLI bridge serves.

The SDK refuses a bridge that does not advertise every method it requires. When the
bridge dropped `run.reconcile`, the SDK kept requiring it and every `RuAgent.start()`
failed its handshake; nothing compared the two lists.
"""

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def _quoted(block: str) -> list[str]:
    return re.findall(r'"([a-z]+\.[a-z]+|initialize)"', block)


def _bridge_methods() -> list[str]:
    source = (ROOT / "backend-dotnet/RuAgent.Cli/Rpc/RpcDispatcher.cs").read_text(encoding="utf-8")
    block = re.search(r"MethodNames\s*=\s*\[(.*?)\];", source, re.S)
    assert block, "RpcDispatcher.MethodNames not found"
    return _quoted(block.group(1))


def _sdk_required() -> list[str]:
    source = (ROOT / "sdk/ru-agent/src/client.ts").read_text(encoding="utf-8")
    block = re.search(r"REQUIRED_BRIDGE_METHODS\s*=\s*\[(.*?)\]\s*as const", source, re.S)
    assert block, "REQUIRED_BRIDGE_METHODS not found"
    return _quoted(block.group(1))


def _fake_bridge_methods() -> list[str]:
    source = (ROOT / "sdk/ru-agent/test/fixtures/fake-bridge.mjs").read_text(encoding="utf-8")
    block = re.search(r"const METHODS\s*=\s*\[(.*?)\];", source, re.S)
    assert block, "fake bridge METHODS not found"
    return _quoted(block.group(1))


def test_every_method_the_sdk_requires_is_served_by_the_bridge() -> None:
    missing = sorted(set(_sdk_required()) - set(_bridge_methods()))
    assert not missing, f"SDK requires methods the bridge does not serve: {missing}"


def test_every_transport_method_the_sdk_calls_is_served_by_the_bridge() -> None:
    called = set()
    for path in (ROOT / "sdk/ru-agent/src").glob("*.ts"):
        called.update(re.findall(r'request(?:<[^>]*>)?\(\s*"([a-z]+\.[a-z]+)"', path.read_text(encoding="utf-8")))
    missing = sorted(called - set(_bridge_methods()))
    assert called and not missing, f"SDK calls methods the bridge does not serve: {missing}"


def test_the_sdk_test_bridge_advertises_exactly_the_real_bridge_methods() -> None:
    assert _fake_bridge_methods() == _bridge_methods()
