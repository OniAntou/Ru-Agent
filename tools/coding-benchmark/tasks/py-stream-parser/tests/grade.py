import importlib
import json
import os
import random
import sys
from pathlib import Path

ROOT = Path(os.environ.get("BENCHMARK_CANDIDATE_ROOT", "/app"))
sys.path.insert(0, str(ROOT))
Decoder = importlib.import_module("src.framing").JsonFrameDecoder


class Group:
    def __init__(self, name):
        self.name = name
        self.checks = 0
        self.failures = []

    def expect(self, condition, label):
        self.checks += 1
        if not condition and len(self.failures) < 8:
            self.failures.append(label[:180])


def wire(value):
    payload = json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    return len(payload).to_bytes(4, "big") + payload


def values(results):
    return [(r.index, r.value, r.error) for r in results]


def fragmentation(g):
    records = [{"n": 1, "text": "xin chào 🐈"}, [True, None, 4.5], "終わり"]
    stream = b"".join(wire(item) for item in records)
    expected = [(i, item, None) for i, item in enumerate(records)]
    for split in range(len(stream) + 1):
        d = Decoder()
        got = d.feed(stream[:split]) + d.feed(stream[split:])
        g.expect(values(got) == expected and d.finish() == [], f"two-chunk split {split}")
    d = Decoder()
    got = []
    for byte in stream:
        got.extend(d.feed(bytes([byte])))
    g.expect(values(got) == expected, "single-byte chunks")
    rng = random.Random(90210)
    for trial in range(40):
        d = Decoder()
        got = []
        pos = 0
        while pos < len(stream):
            step = rng.randint(1, 9)
            got.extend(d.feed(stream[pos:pos + step]))
            pos += step
        g.expect(values(got) == expected, f"generated chunking {trial}")


def framing_boundaries(g):
    records = [None, {}, [], 0, False, {"nested": [1, {"é": "💡"}]}]
    d = Decoder(128)
    got = d.feed(b"".join(wire(value) for value in records))
    g.expect(values(got) == [(i, value, None) for i, value in enumerate(records)], "zero length chunk is inert")
    g.expect(d.feed(b"") == [], "empty feed after complete stream")
    d = Decoder(6)
    payload = b'"1234"'
    got = d.feed(len(payload).to_bytes(4, "big") + payload)
    g.expect(values(got) == [(0, "1234", None)], "exact maximum is accepted")
    g.expect(d.finish() == [], "complete exact-bound frame finishes cleanly")


def utf8_json_validation(g):
    bad_utf8 = b"\xff"
    invalid_json = b"{broken}"
    stream = len(bad_utf8).to_bytes(4, "big") + bad_utf8 + len(invalid_json).to_bytes(4, "big") + invalid_json + wire({"ok": "✓"})
    d = Decoder()
    got = d.feed(stream)
    g.expect(values(got) == [(0, None, "invalid_utf8"), (1, None, "invalid_json"), (2, {"ok": "✓"}, None)], "strict UTF-8 and one JSON value")
    trailing = b"{} {}"
    got = Decoder().feed(len(trailing).to_bytes(4, "big") + trailing)
    g.expect(values(got) == [(0, None, "invalid_json")], "reject second JSON value")
    whitespace = b"  {\"v\":2}\r\n"
    got = Decoder().feed(len(whitespace).to_bytes(4, "big") + whitespace)
    g.expect(values(got) == [(0, {"v": 2}, None)], "allow surrounding JSON whitespace")


def malformed_recovery(g):
    payloads = [b"not-json", b"[1,]", b"\xf0\x28\x8c\x28"]
    stream = b"".join(len(p).to_bytes(4, "big") + p for p in payloads) + wire({"recovered": True})
    for chunk_size in (1, 2, 7, len(stream)):
        d = Decoder()
        out = []
        for start in range(0, len(stream), chunk_size):
            out.extend(d.feed(stream[start:start + chunk_size]))
        g.expect([r.index for r in out] == [0, 1, 2, 3], f"recovery indices at chunk {chunk_size}")
        g.expect([r.error for r in out] == ["invalid_json", "invalid_json", "invalid_utf8", None], f"recovery errors at chunk {chunk_size}")
        g.expect(out[-1].value == {"recovered": True}, f"later frame survives at chunk {chunk_size}")


def size_bounds(g):
    d = Decoder(4)
    oversized = (6).to_bytes(4, "big") + b"xxxxxx"
    got = d.feed(oversized + wire(7))
    g.expect(values(got) == [(0, None, "frame_too_large"), (1, 7, None)], "skip exactly oversized payload and resume")
    for invalid in (0, -1, True, 1.5, "4"):
        try:
            Decoder(invalid)
            raised = False
        except (TypeError, ValueError):
            raised = True
        g.expect(raised, f"invalid max rejected: {type(invalid).__name__}")
    try:
        Decoder().feed("text")
        raised = False
    except (TypeError, ValueError):
        raised = True
    g.expect(raised, "string chunk rejected")


def eof_lifecycle(g):
    for discard in (b"", b"ab"):
        d = Decoder(4)
        got = d.feed((6).to_bytes(4, "big") + discard)
        g.expect(values(got) == [(0, None, "frame_too_large")], "oversized header is rejected immediately")
        g.expect(d.finish() == [], "discard EOF does not reject the frame twice")
    d = Decoder(4)
    got = d.feed((6).to_bytes(4, "big") + b"xxxxxx" + b"\x00")
    g.expect(values(got) == [(0, None, "frame_too_large")]
             and values(d.finish()) == [(1, None, "truncated_frame")], "following partial header has its own index")
    for partial in (b"\x00", b"\x00\x00\x00\x04ab"):
        d = Decoder()
        g.expect(d.feed(partial) == [], "partial frame is not emitted")
        got = d.finish()
        g.expect(values(got) == [(0, None, "truncated_frame")], "partial frame reports truncation")
        g.expect(d.finish() == [], "second finish is empty")
        try:
            d.feed(b"")
            raised = False
        except RuntimeError:
            raised = True
        g.expect(raised, "feed after finish rejected")
    d = Decoder()
    g.expect(d.finish() == [], "empty stream finishes cleanly")
    g.expect(d.finish() == [], "clean finish is idempotent")


def main():
    groups = []
    for name, check in [("fragmentation", fragmentation), ("framing-boundaries", framing_boundaries), ("utf8-json-validation", utf8_json_validation), ("malformed-recovery", malformed_recovery), ("size-bounds", size_bounds), ("eof-lifecycle", eof_lifecycle)]:
        group = Group(name)
        try:
            check(group)
        except Exception as exc:
            group.failures.append(f"grader case raised {type(exc).__name__}")
        groups.append({"name": name, "passed": not group.failures, "checks": group.checks, "failures": group.failures})
    print(json.dumps({"groups": groups}, separators=(",", ":")))
    return 0 if all(group["passed"] for group in groups) else 1


if __name__ == "__main__":
    raise SystemExit(main())
