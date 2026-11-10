export interface RunEvent {
  subscription_id: string;
  sequence: number;
  event_id?: string;
  run_id?: string;
  session_id?: string;
  event_type: string;
  occurred_at?: string;
  status?: string;
  safe_summary?: string | null;
  payload_ref?: string | null;
}

export interface RunTransient {
  subscription_id: string;
  transient_sequence?: number | null;
  event_type: string;
  data: string;
}

export interface RunDetached {
  subscription_id: string;
  reason: string;
}

export interface RunHandleInfo {
  run_id: string;
  session_id: string;
  status: string;
  workspace_id: string;
}

export type ApprovalDecision = "allow_once" | "allow_session_directory" | "allow_always" | "deny";

export interface HostStatus {
  ready: boolean;
  base_url: string;
}

export interface RunTrustResult {
  run_id: string;
  status: string;
}

export type ReconcileOutcome = "applied" | "not_applied" | "unknown_continue";

export type PolicyDecision = "allow" | "ask" | "deny";

export interface PolicyMatcher {
  tool_id?: string | null;
  tool_version?: string | null;
  canonical_path_pattern?: string | null;
  resolved_executable?: string | null;
  argv_pattern?: string[] | null;
  cwd_pattern?: string | null;
  operation_class?: string | null;
  network_intent?: string | null;
  [key: string]: unknown;
}

export interface PolicyRule {
  rule_id: string;
  scope: string;
  decision: PolicyDecision;
  matcher: PolicyMatcher;
  creation_order?: number;
  expires_at?: string | null;
  version?: number;
  [key: string]: unknown;
}

export interface PolicyImportResult {
  imported: PolicyRule[];
}

export interface PolicyRemoveResult {
  removed: string;
}

export interface RuAgentStartOptions {
  command?: string;
  args?: string[];
  agentArgs?: string[];
  cwd?: string;
  env?: Record<string, string>;
}

export type { SubagentList, SubagentSummary, SubagentAttempt, SubagentResult, SubagentDetail, SubagentUserAction } from "./subagents.js";
