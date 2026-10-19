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

export interface RuAgentStartOptions {
  command?: string;
  args?: string[];
  agentArgs?: string[];
  cwd?: string;
  env?: Record<string, string>;
}

export type { SubagentList, SubagentSummary, SubagentAttempt, SubagentResult, SubagentDetail, SubagentUserAction } from "./subagents.js";
