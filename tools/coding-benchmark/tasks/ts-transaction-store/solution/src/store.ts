export type Mutation = { op: "set"; key: string; value: string } | { op: "delete"; key: string };
export type StoreSnapshot = { version: number; values: Readonly<Record<string, string>> };
export type TransactionResult = { code: "ok" | "invalid" | "version_conflict" | "idempotency_conflict"; version: number; snapshot: StoreSnapshot };

export class TransactionStore {
  private values: Record<string, string>;
  private version = 0;
  private readonly receipts = new Map<string, { mutations: Mutation[]; result: TransactionResult }>();

  constructor(initial: Record<string, string> = {}) {
    if (!initial || typeof initial !== "object" || Array.isArray(initial)
      || Object.entries(initial).some(([key, value]) => !key || typeof value !== "string")) {
      throw new TypeError("initial values must map non-empty keys to strings");
    }
    this.values = Object.assign(Object.create(null) as Record<string, string>, initial);
  }

  snapshot(): StoreSnapshot {
    const values = Object.freeze(Object.assign(Object.create(null) as Record<string, string>, this.values));
    return Object.freeze({ version: this.version, values });
  }

  transact(requestId: string, expectedVersion: number, mutations: readonly Mutation[]): TransactionResult {
    if (typeof requestId !== "string" || requestId.trim().length === 0 || !Array.isArray(mutations) || mutations.length === 0) {
      return this.result("invalid");
    }
    const body: Mutation[] = [];
    for (const candidate of mutations as readonly unknown[]) {
      if (!candidate || typeof candidate !== "object" || Array.isArray(candidate)) return this.result("invalid");
      const mutation = { ...(candidate as Record<string, unknown>) };
      if (typeof mutation.key !== "string" || mutation.key.length === 0
        || (mutation.op === "set" ? typeof mutation.value !== "string" : mutation.op !== "delete")) return this.result("invalid");
      body.push(mutation as Mutation);
    }
    const previous = this.receipts.get(requestId);
    if (previous) return sameBody(previous.mutations, body) ? previous.result : this.result("idempotency_conflict");
    if (!Number.isSafeInteger(expectedVersion) || expectedVersion !== this.version) return this.result("version_conflict");
    const proposed = Object.assign(Object.create(null) as Record<string, string>, this.values);
    for (const mutation of body) {
      if (mutation.op === "set") proposed[mutation.key] = mutation.value;
      else delete proposed[mutation.key];
    }
    this.values = proposed;
    this.version++;
    const committed = this.result("ok");
    this.receipts.set(requestId, { mutations: body, result: committed });
    return committed;
  }

  private result(code: TransactionResult["code"]): TransactionResult {
    return Object.freeze({ code, version: this.version, snapshot: this.snapshot() });
  }
}

function sameBody(left: Mutation[], right: Mutation[]): boolean {
  return left.length === right.length && left.every((mutation, index) => {
    const candidate = right[index];
    return mutation.op === candidate.op && mutation.key === candidate.key
      && (mutation.op === "delete" || (candidate.op === "set" && mutation.value === candidate.value));
  });
}
