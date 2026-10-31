export interface EventRecord {
  readonly name: string;
  readonly payload: string;
}

export interface EventPolicy {
  readonly allowedNames: readonly string[];
}
