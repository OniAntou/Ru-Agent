import type { BridgeTransport } from "./transport.js";

export interface SubagentUserAction {
  status: "queued" | "awaiting_approval" | "executing" | "completed" | "unknown" | "failed" | "cancelled";
  revision: number;
  approval_id: string | null;
  code: string | null;
}
export interface SubagentAttempt {
  run_id: string;
  attempt_number: number;
  status: string;
  version: number;
  provider_id: string | null;
  model: string | null;
  protocol: string | null;
  deadline_at: string | null;
  usage: SubagentList["usage"] | null;
  approval: { approval_id: string; tool_id: string; arguments: Record<string, unknown>; arguments_truncated: boolean } | null;
}
export interface SubagentResult {
  result_version: number;
  run_id: string;
  status: string;
  code: string | null;
  apply_status: string | null;
  applied_files: number | null;
  acknowledged: boolean;
  user_action: SubagentUserAction | null;
}
export interface SubagentSummary {
  agent_id: string;
  role: "coding" | "review";
  task: string | null;
  canceled: boolean;
  attempts: SubagentAttempt[];
  results: SubagentResult[];
}
export interface SubagentList {
  root_run_id: string;
  root_status: string;
  snapshot_id: string;
  agents: SubagentSummary[];
  preparations: { agent_id: string; stage: string }[];
  unavailable_attempts: number;
  usage: {
    model_steps: number;
    reported_steps: number;
    input_tokens: number | null;
    output_tokens: number | null;
    total_tokens: number | null;
    is_complete: boolean;
  };
}
export interface SubagentDetail {
  root_run_id: string;
  agent: SubagentSummary;
  result: { result_version: number; text: string; offset: number; total_bytes: number; next_offset: number | null } | null;
}
/** Read-only observations of root-owned children. Detaching an event stream never cancels these agents. */
export class Subagents {
  constructor(private readonly transport: Pick<BridgeTransport, "request">, readonly rootRunId: string) {}

  list(): Promise<SubagentList> {
    return this.transport.request("subagent.list", { runId: this.rootRunId });
  }
  get(agentId: string, page: { resultVersion?: number; offset?: number; limit?: number } = {}): Promise<SubagentDetail> {
    return this.transport.request("subagent.get", { runId: this.rootRunId, agentId, ...page });
  }
}
