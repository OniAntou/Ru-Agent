import type {EventPolicy, EventRecord} from "./model.js";

export function normalizeName(value: string): string {
  return value;
}

export function isAllowed(policy: EventPolicy, name: string): boolean {
  return policy.allowedNames.includes(name);
}

export function filterEvents(policy: EventPolicy, events: readonly EventRecord[]): EventRecord[] {
  return events.filter((event) => isAllowed(policy, normalizeName(event.name)));
}
