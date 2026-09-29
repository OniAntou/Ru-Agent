import { describe, expect, it } from "vitest";
import path from "node:path";
import { BridgeTransport } from "../src/transport.js";

const fixture = path.join(import.meta.dirname, "fixtures", "fake-bridge.mjs");
const cmdFixture = path.join(import.meta.dirname, "fixtures", "fake-bridge.cmd");

function start(mode = "echo") {
  return BridgeTransport.spawn({
    command: process.execPath,
    args: [fixture],
    agentArgs: [],
    env: { FAKE_BRIDGE_MODE: mode },
  });
}

describe("BridgeTransport", () => {
  it("round-trips a request and resolves by id", async () => {
    const transport = start();
    const result = await transport.request<{ echoed: { a: number } }>("anything", { a: 1 });
    expect(result.echoed).toEqual({ a: 1 });
    await transport.close();
  });

  it("delivers notifications to subscribers", async () => {
    const transport = start();
    const seen: unknown[] = [];
    transport.onNotification((method, params) => seen.push([method, params]));
    await transport.request("run.event.test", {});
    expect(seen).toEqual([["run.event", { sequence: 1 }]]);
    await transport.close();
  });

  it("rejects pending requests when the bridge process dies", async () => {
    const transport = start("crash");
    await expect(transport.request("anything", {})).rejects.toThrow(/bridge exited/);
  });

  it("includes the child stderr tail in the exit error", async () => {
    const transport = start("crash-with-stderr");
    await expect(transport.request("anything", {})).rejects.toThrow(/bridge_stderr_marker/);
  });

  it.skipIf(process.platform !== "win32")("spawns_a_cmd_bridge_on_windows", async () => {
    const transport = BridgeTransport.spawn({
      command: cmdFixture,
      args: [],
      agentArgs: [],
      env: { FAKE_BRIDGE_MODE: "echo" },
    });
    const result = await transport.request<{ echoed: { a: number } }>("anything", { a: 1 });
    expect(result.echoed).toEqual({ a: 1 });
    await transport.close();
  });

  it("fails closed on a non-JSON stdout line", async () => {
    const transport = start("garbage");
    await expect(transport.request("anything", {})).rejects.toThrow(/invalid json/i);
  });

  it("requests_after_the_bridge_died_fail_fast", async () => {
    const transport = start("crash");
    await expect(transport.request("anything", {})).rejects.toThrow(/bridge exited/);
    const started = Date.now();
    await expect(transport.request("anything", {}, 60_000)).rejects.toThrow(/bridge exited/);
    expect(Date.now() - started).toBeLessThan(5_000);
  });
});
