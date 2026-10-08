import type { ReplayStatus, SessionEvent } from "./events.ts";

export type { ReplayStatus, SessionEvent } from "./events.ts";

export class SessionReplay {
  readonly sessionId: string;
  generation: number;
  lastSequence: number;
  text = "";
  isTerminal = false;
  private readonly applied = new Map<number, string>();

  constructor(sessionId: string, generation: number, lastSeenSequence = 0) {
    this.sessionId = sessionId;
    this.generation = generation;
    this.lastSequence = lastSeenSequence;
  }

  replaceGeneration(generation: number, lastSeenSequence = 0): void {
    if (!Number.isSafeInteger(generation) || generation <= this.generation) return;
    this.generation = generation;
    this.lastSequence = lastSeenSequence;
    this.text = "";
    this.isTerminal = false;
    this.applied.clear();
  }

  apply(event: SessionEvent): ReplayStatus {
    if (!this.isValid(event)) return "invalid-event";
    if (event.sessionId !== this.sessionId) return "wrong-session";
    if (event.generation < this.generation) return "stale-generation";
    if (event.generation > this.generation) return "future-generation";

    const fingerprint = JSON.stringify([event.kind, event.text ?? null]);
    if (event.sequence <= this.lastSequence) {
      return this.applied.get(event.sequence) === fingerprint ? "duplicate" : "conflict";
    }
    if (this.isTerminal) return "after-terminal";
    if (event.sequence !== this.lastSequence + 1) return "gap";

    if (event.kind === "snapshot") this.text = event.text!;
    if (event.kind === "delta") this.text += event.text!;
    if (event.kind === "terminal") this.isTerminal = true;
    this.applied.set(event.sequence, fingerprint);
    this.lastSequence = event.sequence;
    return "applied";
  }

  private isValid(event: SessionEvent): boolean {
    if (!event || typeof event !== "object") return false;
    if (typeof event.sessionId !== "string" || event.sessionId.length === 0) return false;
    if (!Number.isSafeInteger(event.generation) || event.generation < 0) return false;
    if (!Number.isSafeInteger(event.sequence) || event.sequence < 1) return false;
    if (event.kind === "terminal") return event.text === undefined;
    return (event.kind === "delta" || event.kind === "snapshot") && typeof event.text === "string";
  }
}
