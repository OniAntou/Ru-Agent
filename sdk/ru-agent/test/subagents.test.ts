import { describe, expect, it, vi } from "vitest";
import { Subagents } from "../src/subagents.js";

describe("Subagents", () => {
  it("binds read-only observations to the owning root and exact result version", async () => {
    const request = vi.fn().mockResolvedValue({ accepted: true });
    const agents = new Subagents({ request }, "run_root");
    await agents.list();
    await agents.get("agent_one", { resultVersion: 2, offset: 4, limit: 8 });
    expect("send" in agents).toBe(false);
    expect("cancel" in agents).toBe(false);
    expect("apply" in agents).toBe(false);
    expect(request.mock.calls).toEqual([
      ["subagent.list", { runId: "run_root" }],
      ["subagent.get", { runId: "run_root", agentId: "agent_one", resultVersion: 2, offset: 4, limit: 8 }],
    ]);
  });
});
