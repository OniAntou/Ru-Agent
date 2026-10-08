import { join } from "node:path";
import { pathToFileURL } from "node:url";

const candidateRoot = process.env.BENCHMARK_CANDIDATE_ROOT;
if (!candidateRoot) throw new Error("BENCHMARK_CANDIDATE_ROOT is required");
const { SessionReplay } = await import(pathToFileURL(join(candidateRoot, "src", "sessionReplay.ts")).href);

type GradeGroup = { name: string; passed: boolean; checks: number; failures: string[] };
type SessionEvent = {
  sessionId: string;
  generation: number;
  sequence: number;
  kind: "delta" | "snapshot" | "terminal";
  text?: string;
};

function event(sequence: number, kind: SessionEvent["kind"], text?: string, generation = 4, sessionId = "session-a"): SessionEvent {
  return { sessionId, generation, sequence, kind, ...(text === undefined ? {} : { text }) };
}

function check(name: string, run: () => void): GradeGroup {
  try {
    run();
    return { name, passed: true, checks: 1, failures: [] };
  } catch (error) {
    const message = error instanceof Error ? `${error.name}: ${error.message}` : String(error);
    return { name, passed: false, checks: 1, failures: [message] };
  }
}

function expect(condition: boolean, message: string): void {
  if (!condition) throw new Error(message);
}

const groups: GradeGroup[] = [
  check("duplicate_delivery_is_idempotent", () => {
    const replay = new SessionReplay("session-a", 4);
    const first = event(1, "delta", "hello");
    expect(replay.apply(first) === "applied", "first delivery should apply");
    expect(replay.apply(first) === "duplicate", "repeat delivery should be identified as duplicate");
    expect(replay.text === "hello" && replay.lastSequence === 1, "duplicate must not change text or cursor");
    expect(replay.apply(event(1, "delta", "changed")) === "conflict", "same cursor with different content should conflict");
  }),
  check("gaps_do_not_advance_the_cursor", () => {
    const replay = new SessionReplay("session-a", 4);
    expect(replay.apply(event(1, "delta", "a")) === "applied", "sequence 1 should apply");
    expect(replay.apply(event(3, "delta", "c")) === "gap", "sequence 3 should report the missing event");
    expect(replay.lastSequence === 1 && replay.text === "a", "gap must not mutate cursor or text");
    expect(replay.apply(event(2, "delta", "b")) === "applied", "missing sequence should apply");
    expect(replay.apply(event(3, "delta", "c")) === "applied", "later sequence should apply after the gap closes");
    expect(replay.text === "abc" && replay.lastSequence === 3, "replay should restore ordered output");
  }),
  check("generation_replacement_rejects_old_events", () => {
    const replay = new SessionReplay("session-a", 4);
    replay.apply(event(1, "delta", "old"));
    replay.replaceGeneration(5);
    expect(replay.text === "" && replay.lastSequence === 0 && !replay.isTerminal, "replacement should reset generation state");
    expect(replay.apply(event(2, "delta", "late", 4)) === "stale-generation", "old generation should be rejected");
    expect(replay.apply(event(1, "delta", "new", 6)) === "future-generation", "future generation should be rejected");
    expect(replay.lastSequence === 0 && replay.text === "", "rejected generations must not mutate state");
    expect(replay.apply(event(1, "delta", "new", 5)) === "applied" && replay.text === "new", "current generation should remain usable");
  }),
  check("terminal_event_closes_the_generation", () => {
    const replay = new SessionReplay("session-a", 4);
    replay.apply(event(1, "delta", "done"));
    const terminal = event(2, "terminal");
    expect(replay.apply(terminal) === "applied" && replay.isTerminal, "terminal event should close the generation");
    expect(replay.apply(terminal) === "duplicate", "terminal redelivery should be idempotent");
    expect(replay.apply(event(3, "delta", " late")) === "after-terminal", "later events should not be applied");
    expect(replay.text === "done" && replay.lastSequence === 2, "closed generation should retain terminal state");
  }),
  check("ordered_delta_and_snapshot_projection", () => {
    const replay = new SessionReplay("session-a", 4);
    expect(replay.apply(event(1, "snapshot", "draft")) === "applied", "snapshot should apply");
    expect(replay.apply(event(2, "delta", " updated")) === "applied", "delta should apply");
    expect(replay.apply(event(3, "snapshot", "authoritative")) === "applied", "later snapshot should apply");
    expect(replay.text === "authoritative" && replay.lastSequence === 3, "snapshot replaces projected text");
  }),
  check("malformed_events_are_rejected_without_mutation", () => {
    const replay = new SessionReplay("session-a", 4);
    replay.apply(event(1, "delta", "safe"));
    expect(replay.apply({ ...event(2, "delta", "bad"), sequence: 0 }) === "invalid-event", "invalid sequence should be rejected");
    expect(replay.apply({ ...event(2, "delta", "bad"), text: undefined }) === "invalid-event", "delta without text should be rejected");
    expect(replay.text === "safe" && replay.lastSequence === 1, "rejected events should not mutate state");
  }),
  check("wrong_session_events_do_not_mutate_state", () => {
    const replay = new SessionReplay("session-a", 4);
    replay.apply(event(1, "delta", "safe"));
    expect(replay.apply(event(2, "delta", "wrong", 4, "session-b")) === "wrong-session", "other session should be rejected");
    expect(replay.text === "safe" && replay.lastSequence === 1, "wrong session must not mutate state");
  }),
];

console.log(JSON.stringify({ groups }));
process.exitCode = groups.every((group) => group.passed) ? 0 : 1;
