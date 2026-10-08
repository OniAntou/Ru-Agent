export type Mutation = { op: "set"; key: string; value: string } | { op: "delete"; key: string };
export type StoreSnapshot = { version: number; values: Readonly<Record<string, string>> };
export type TransactionResult = { code: string; version: number; snapshot: StoreSnapshot };

export class TransactionStore {
  private values: Record<string, string>;
  private version = 0;

  constructor(initial: Record<string, string> = {}) {
    this.values = { ...initial };
  }

  snapshot(): StoreSnapshot {
    return { version: this.version, values: { ...this.values } };
  }

  transact(requestId: string, expectedVersion: number, mutations: readonly Mutation[]): TransactionResult {
    if (!requestId || mutations.length === 0) return this.result("invalid");
    for (const mutation of mutations) {
      if (!mutation.key || (mutation.op !== "set" && mutation.op !== "delete")) return this.result("invalid");
    }
    if (expectedVersion !== this.version) return this.result("version_conflict");
    for (const mutation of mutations) {
      if (mutation.op === "set") this.values[mutation.key] = mutation.value;
      else delete this.values[mutation.key];
    }
    this.version++;
    return this.result("ok");
  }

  private result(code: string): TransactionResult {
    return { code, version: this.version, snapshot: this.snapshot() };
  }
}
