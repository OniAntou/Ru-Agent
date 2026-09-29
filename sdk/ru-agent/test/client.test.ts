import { describe, expect, it } from "vitest";
import path from "node:path";
import { RuAgent } from "../src/client.js";

const fixture = path.join(import.meta.dirname, "fixtures", "fake-bridge.mjs");

async function start() {
  return await RuAgent.start({ command: process.execPath, args: [fixture], agentArgs: [] });
}

describe("RuAgent", () => {
  it("starts a run and streams its events to completion", async () => {
    const ru = await start();
    const run = await ru.runs.start({ workspace: "C:\\w", prompt: "hello" });
    expect(run.runId).toMatch(/^run_/);
    const events: string[] = [];
    for await (const event of run.events()) {
      events.push(event.event_type);
    }
    expect(events).toEqual(["run.started", "run.completed"]);
    await ru.close();
  });

  it("creates sessions through the client", async () => {
    const ru = await start();
    expect(await ru.sessions.create()).toBe("sess_1");
    await ru.close();
  });
});
