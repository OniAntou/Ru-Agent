export { BridgeTransport, type BridgeSpawnOptions } from "./transport.js";
export { RuRpcError, BridgeTransportError } from "./errors.js";
export { RuAgent } from "./client.js";
export { Policies } from "./policies.js";
export { Run } from "./run.js";
export type {
  ApprovalDecision,
  HostStatus,
  PolicyDecision,
  PolicyImportResult,
  PolicyMatcher,
  PolicyRemoveResult,
  PolicyRule,
  ReconcileOutcome,
  RunDetached,
  RunEvent,
  RunTransient,
  RunTrustResult,
  RuAgentStartOptions,
} from "./types.js";

export { Subagents } from "./subagents.js";
export type { SubagentList, SubagentSummary, SubagentAttempt, SubagentResult, SubagentDetail, SubagentUserAction } from "./subagents.js";
