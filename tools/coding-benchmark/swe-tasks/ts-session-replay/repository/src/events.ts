export type SessionEvent = {
  sessionId: string;
  generation: number;
  sequence: number;
  kind: "delta" | "snapshot" | "terminal";
  text?: string;
};

export type ReplayStatus =
  | "applied"
  | "duplicate"
  | "gap"
  | "conflict"
  | "wrong-session"
  | "stale-generation"
  | "future-generation"
  | "after-terminal"
  | "invalid-event";
