import type { ReplayStatus, SessionEvent } from "./events.ts";

export type { ReplayStatus, SessionEvent } from "./events.ts";

export class SessionReplay {
  readonly sessionId: string;
  generation: number;
  lastSequence: number;
  text = "";
  isTerminal = false;

  constructor(sessionId: string, generation: number, lastSeenSequence = 0) {
    this.sessionId = sessionId;
    this.generation = generation;
    this.lastSequence = lastSeenSequence;
  }

  replaceGeneration(generation: number, lastSeenSequence = 0): void {
    this.generation = generation;
    this.lastSequence = lastSeenSequence;
    this.text = "";
    this.isTerminal = false;
  }

  apply(event: SessionEvent): ReplayStatus {
    if (event.sessionId !== this.sessionId) return "wrong-session";
    if (event.generation !== this.generation) return "stale-generation";

    if (event.kind === "snapshot") this.text = event.text ?? "";
    else if (event.kind === "delta") this.text += event.text ?? "";
    else this.isTerminal = true;

    this.lastSequence = event.sequence;
    return "applied";
  }
}
