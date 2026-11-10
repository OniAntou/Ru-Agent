import type { BridgeTransport } from "./transport.js";
import type { PolicyImportResult, PolicyRemoveResult, PolicyRule } from "./types.js";

/**
 * Policy-rule access over the RPC bridge.
 *
 * Imports and removals are destructive and require an explicit literal `true`, so a
 * caller cannot confirm one by accident. The bridge still enforces the same rule.
 */
export class Policies {
  constructor(private readonly transport: Pick<BridgeTransport, "request">) {}

  list(scope?: string): Promise<PolicyRule[]> {
    return this.transport.request<PolicyRule[]>("policy.list", scope === undefined ? {} : { scope });
  }

  import(file: string, confirm: true): Promise<PolicyImportResult> {
    return this.transport.request<PolicyImportResult>("policy.import", { file, confirm });
  }

  remove(ruleId: string, confirm: true): Promise<PolicyRemoveResult> {
    return this.transport.request<PolicyRemoveResult>("policy.remove", { ruleId, confirm });
  }
}
