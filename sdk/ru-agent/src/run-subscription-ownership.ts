import type { BridgeTransport } from "./transport.js";

interface OwnerState {
  subscriptionId?: string;
  attachSettled: boolean;
  ended: boolean;
  cleanupFinished: boolean;
}

interface RunState {
  owners: Set<OwnerState>;
  runWideCleanup?: Promise<void>;
}

const RUNS = new WeakMap<BridgeTransport, Map<string, RunState>>();

/** Coordinates attach ownership so one Run.events iterator cannot detach peers. */
export class RunSubscriptionOwnership {
  #released = false;

  private constructor(
    readonly transport: BridgeTransport,
    readonly runId: string,
    private readonly state: RunState,
    private readonly owner: OwnerState,
  ) {}

  static acquire(transport: BridgeTransport, runId: string): RunSubscriptionOwnership {
    let runs = RUNS.get(transport);
    if (!runs) {
      runs = new Map();
      RUNS.set(transport, runs);
    }
    let state = runs.get(runId);
    if (!state) {
      state = { owners: new Set() };
      runs.set(runId, state);
    }
    const owner: OwnerState = { attachSettled: false, ended: false, cleanupFinished: true };
    state.owners.add(owner);
    return new RunSubscriptionOwnership(transport, runId, state, owner);
  }

  get subscriptionId(): string | undefined {
    return this.owner.subscriptionId;
  }

  get hasPeers(): boolean {
    return this.state.owners.size > 1;
  }

  isPeerSubscription(subscriptionId: string): boolean {
    for (const owner of this.state.owners) {
      if (owner !== this.owner && owner.subscriptionId === subscriptionId) return true;
    }
    return false;
  }

  setAttached(subscriptionId: string): void {
    this.owner.subscriptionId = subscriptionId;
    this.owner.attachSettled = true;
    this.#releaseIfReady();
  }

  settleWithoutSubscription(): void {
    this.owner.attachSettled = true;
    this.owner.cleanupFinished = true;
    this.#releaseIfReady();
  }

  markCleanupPending(): void {
    this.owner.cleanupFinished = false;
  }

  markCleanupFinished(): void {
    this.owner.cleanupFinished = true;
    this.#releaseIfReady();
  }

  end(): void {
    this.owner.ended = true;
    this.#releaseIfReady();
  }

  async waitForRunWideCleanup(): Promise<void> {
    while (this.state.runWideCleanup) await this.state.runWideCleanup;
  }

  runWideDetach(detach: () => Promise<void>): Promise<void> {
    if (this.hasPeers) throw new Error("run-wide detach requires exclusive run ownership");
    let releaseGate!: () => void;
    const gate = new Promise<void>((resolve) => {
      releaseGate = resolve;
    });
    this.state.runWideCleanup = gate;
    this.markCleanupPending();
    return detach().finally(() => {
      if (this.state.runWideCleanup === gate) delete this.state.runWideCleanup;
      releaseGate();
    });
  }

  #releaseIfReady(): void {
    if (this.#released || !this.owner.ended || !this.owner.attachSettled || !this.owner.cleanupFinished) return;
    this.#released = true;
    this.state.owners.delete(this.owner);
    const runs = RUNS.get(this.transport);
    if (this.state.owners.size === 0) runs?.delete(this.runId);
    if (runs?.size === 0) RUNS.delete(this.transport);
  }
}
