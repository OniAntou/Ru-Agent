import { describe, expect, it, vi } from "vitest";
import { Policies } from "../src/policies.js";

describe("Policies", () => {
  it("binds scope and explicit confirmations to the RPC methods", async () => {
    const request = vi.fn().mockResolvedValue({});
    const policies = new Policies({ request });

    await policies.list();
    await policies.list("global");
    await policies.import("C:\\policy.json", true);
    await policies.remove("rule_one", true);

    expect(request.mock.calls).toEqual([
      ["policy.list", {}],
      ["policy.list", { scope: "global" }],
      ["policy.import", { file: "C:\\policy.json", confirm: true }],
      ["policy.remove", { ruleId: "rule_one", confirm: true }],
    ]);
  });
});
