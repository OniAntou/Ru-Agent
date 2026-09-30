import { afterEach, describe, expect, it, vi } from "vitest";
import { existsSync } from "node:fs";
import path from "node:path";
import { resolveCliCommand, RuAgent } from "../src/client.js";

const fsHolder = vi.hoisted(() => ({ existsSync: undefined as ((candidate: unknown) => boolean) | undefined }));

vi.mock("node:fs", async (importOriginal) => {
  const actual = await importOriginal<typeof import("node:fs")>();
  fsHolder.existsSync = actual.existsSync as unknown as (candidate: unknown) => boolean;
  return { ...actual, existsSync: vi.fn(actual.existsSync) };
});

const fixture = path.join(import.meta.dirname, "fixtures", "fake-bridge.mjs");

describe("resolveCliCommand", () => {
  afterEach(() => {
    vi.mocked(existsSync).mockImplementation((candidate) => fsHolder.existsSync!(candidate));
  });

  it("prefers options.command, then RU_CLI_PATH, then the repo bin", () => {
    const previous = process.env.RU_CLI_PATH;
    try {
      process.env.RU_CLI_PATH = "C:\\env\\ru.cmd";
      expect(resolveCliCommand({ command: "C:\\explicit\\ru.exe" })).toBe("C:\\explicit\\ru.exe");
      expect(resolveCliCommand({})).toBe("C:\\env\\ru.cmd");
      delete process.env.RU_CLI_PATH;
      const repoBin = resolveCliCommand({});
      expect(repoBin.endsWith(path.join("bin", "ru.cmd"))).toBe(true);
      expect(existsSync(repoBin)).toBe(true);
    } finally {
      if (previous === undefined) delete process.env.RU_CLI_PATH;
      else process.env.RU_CLI_PATH = previous;
    }
  });

  it("falls back to ru on PATH when the repo bin is absent", () => {
    const previous = process.env.RU_CLI_PATH;
    try {
      delete process.env.RU_CLI_PATH;
      vi.mocked(existsSync).mockReturnValue(false);
      expect(resolveCliCommand({})).toBe("ru");
    } finally {
      if (previous === undefined) delete process.env.RU_CLI_PATH;
      else process.env.RU_CLI_PATH = previous;
    }
  });
});

async function start(mode = "echo") {
  return await RuAgent.start({ command: process.execPath, args: [fixture], agentArgs: [], env: { FAKE_BRIDGE_MODE: mode } });
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

  it(
    "events_rejects_when_the_bridge_dies_mid_stream",
    async () => {
      const ru = await start("die-after-attach");
      const run = await ru.runs.start({ workspace: "C:\\w", prompt: "hello" });
      const drain = async (): Promise<void> => {
        for await (const event of run.events()) {
          void event;
        }
      };
      await expect(drain()).rejects.toThrow(/bridge exited/);
      await ru.close();
    },
    2_000,
  );

  it(
    "events_rejects_on_bridge_shutdown",
    async () => {
      const ru = await start("shutdown-after-attach");
      const run = await ru.runs.start({ workspace: "C:\\w", prompt: "hello" });
      const drain = async (): Promise<void> => {
        for await (const event of run.events()) {
          void event;
        }
      };
      await expect(drain()).rejects.toThrow(/bridge shut down/);
      await ru.close();
    },
    2_000,
  );

  it(
    "events_rejects_when_the_subscription_detaches",
    async () => {
      const ru = await start("detach-after-attach");
      const run = await ru.runs.start({ workspace: "C:\\w", prompt: "hello" });
      const drain = async (): Promise<void> => {
        for await (const event of run.events()) {
          void event;
        }
      };
      await expect(drain()).rejects.toThrow(/detached before a terminal event.*stream_interrupted/);
      await ru.close();
    },
    2_000,
  );

  it(
    "events_rejects_when_the_detach_arrives_before_the_attach_response",
    async () => {
      const ru = await start("detach-before-attach");
      const run = await ru.runs.start({ workspace: "C:\\w", prompt: "hello" });
      const drain = async (): Promise<void> => {
        for await (const event of run.events()) {
          void event;
        }
      };
      await expect(drain()).rejects.toThrow(/detached before a terminal event.*stream_interrupted/);
      await ru.close();
    },
    2_000,
  );

  it("approves with the requested decision", async () => {
    const ru = await start();
    const run = await ru.runs.start({ workspace: "C:\\w", prompt: "hello" });
    const result = await run.approve("apr_" + "b".repeat(32), "allow_always");
    expect(result.decision).toBe("allow_always");
    await ru.close();
  });

  it("cancels through run.cancel", async () => {
    const ru = await start();
    const run = await ru.runs.start({ workspace: "C:\\w", prompt: "hello" });
    await expect(run.cancel()).resolves.toBeUndefined();
    await ru.close();
  });

  it("decodes run.output base64 into a Buffer", async () => {
    const ru = await start();
    const run = await ru.runs.start({ workspace: "C:\\w", prompt: "hello" });
    const bytes = await run.output("payload_" + "b".repeat(32));
    expect(Buffer.isBuffer(bytes)).toBe(true);
    expect(bytes.toString("utf8")).toBe("hello");
    await ru.close();
  });

  it("accepts a bridge that advertises every required method", async () => {
    const ru = await start();
    await ru.close();
  });

  it("rejects a bridge handshake that is missing a required method", async () => {
    await expect(
      RuAgent.start({
        command: process.execPath,
        args: [fixture],
        agentArgs: [],
        env: { FAKE_BRIDGE_MODE: "echo", FAKE_BRIDGE_DROP_METHOD: "run.output" },
      }),
    ).rejects.toThrow(/missing required method\(s\): run\.output/);
  });
});
