import { describe, expect, it } from "vitest";
import path from "node:path";
import { copyFileSync, mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { BridgeTransport } from "../src/transport.js";
import { controlledTransport } from "./controlled-transport.js";

const MAX_FRAME_BYTES = 1024 * 1024 + 64 * 1024;

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
  it.each([null, [], "text", 12])("fails closed when stdout root is not an object: %s", async (root) => {
    const controlled = controlledTransport(() => true);
    let closeCalls = 0;
    controlled.transport.onClose(() => closeCalls++);
    const pending = controlled.transport.request("pending");
    controlled.child.stdout.emit("data", `${JSON.stringify(root)}\n`);

    await expect(pending).rejects.toThrow(/invalid json-rpc envelope/i);
    expect(closeCalls).toBe(1);
    expect(controlled.child.killCount).toBe(1);
    controlled.child.stdout.emit("data", '{"jsonrpc":"2.0","id":1,"result":{}}\n');
    expect(closeCalls).toBe(1);
    expect(controlled.child.killCount).toBe(1);
  });

  it.each([
    { jsonrpc: "2.0", method: "notify", params: null },
    { jsonrpc: "2.0", method: "notify", params: [] },
    { jsonrpc: "1.0", id: 1, result: {} },
    { jsonrpc: "2.0", id: 1 },
    { jsonrpc: "2.0", id: 1, result: {}, error: { code: 1, message: "bad" } },
    { jsonrpc: "2.0", id: 1, error: { code: "bad", message: "bad" } },
  ])("fails pending requests on invalid envelope %#", async (envelope) => {
    const controlled = controlledTransport(() => true);
    const pending = controlled.transport.request("pending");
    controlled.child.stdout.emit("data", `${JSON.stringify(envelope)}\n`);

    await expect(pending).rejects.toThrow(/invalid json-rpc envelope/i);
    expect(controlled.child.killCount).toBe(1);
  });

  it("accepts a frame at the UTF-8 byte limit after CRLF removal", async () => {
    const controlled = controlledTransport(() => true);
    const pending = controlled.transport.request("pending");
    const base = '{"jsonrpc":"2.0","id":1,"result":{}}';
    const frame = base + " ".repeat(MAX_FRAME_BYTES - Buffer.byteLength(base, "utf8"));
    controlled.child.stdout.emit("data", `${frame}\r\n`);

    await expect(pending).resolves.toEqual({});
    expect(controlled.child.killCount).toBe(0);
    await controlled.transport.close();
  });

  it("assembles UTF-8 characters split across stdout byte chunks", async () => {
    const controlled = controlledTransport(() => true);
    const pending = controlled.transport.request("pending");
    const frame = Buffer.from('{"jsonrpc":"2.0","id":1,"result":{"text":"🧡"}}\r\n', "utf8");
    const split = frame.indexOf(Buffer.from("🧡", "utf8")) + 2;
    controlled.child.stdout.emitBytes(frame.subarray(0, split));
    controlled.child.stdout.emitBytes(frame.subarray(split));

    await expect(pending).resolves.toEqual({ text: "🧡" });
    await controlled.transport.close();
  });

  it("rejects oversized complete frames and ignores later coalesced frames", async () => {
    const controlled = controlledTransport(() => true);
    let notifications = 0;
    controlled.transport.onNotification(() => notifications++);
    const pending = controlled.transport.request("pending");
    const base = '{"jsonrpc":"2.0","id":1,"result":{}}';
    const oversized = base + " ".repeat(MAX_FRAME_BYTES + 1 - Buffer.byteLength(base, "utf8"));
    controlled.child.stdout.emit("data", `${oversized}\n{"jsonrpc":"2.0","method":"later","params":{}}\n`);

    await expect(pending).rejects.toThrow(/oversized frame/i);
    expect(notifications).toBe(0);
    expect(controlled.child.killCount).toBe(1);
  });

  it("rejects an oversized partial multibyte frame before its newline", async () => {
    const controlled = controlledTransport(() => true);
    const pending = controlled.transport.request("pending");
    const prefix = '{"jsonrpc":"2.0","id":1,"result":{"text":"';
    const count = Math.ceil((MAX_FRAME_BYTES + 1 - Buffer.byteLength(prefix, "utf8")) / 4);
    controlled.child.stdout.emit("data", prefix + "🧡".repeat(count));

    await expect(pending).rejects.toThrow(/oversized frame/i);
    expect(controlled.child.killCount).toBe(1);
  });
  it("resolves a response received before a backpressured write drains", async () => {
    let child!: ReturnType<typeof controlledTransport>["child"];
    const controlled = controlledTransport((payload) => {
      const request = JSON.parse(payload) as { id: number };
      setTimeout(() => child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { ok: true } }) + "\n"), 0);
      setTimeout(() => child.stdin.emit("drain"), 10);
      return false;
    });
    child = controlled.child;

    await expect(controlled.transport.request("anything", {}, 200)).resolves.toEqual({ ok: true });
    await controlled.transport.close();
  });

  it("does not abort a backpressured write after its response has resolved", async () => {
    let child!: ReturnType<typeof controlledTransport>["child"];
    const writes: number[] = [];
    const controlled = controlledTransport((payload) => {
      const request = JSON.parse(payload) as { id: number };
      writes.push(request.id);
      if (request.id === 1) {
        setTimeout(() => child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { ok: true } }) + "\n"), 0);
        setTimeout(() => child.stdin.emit("drain"), 80);
        return false;
      }
      setTimeout(() => child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { ok: true } }) + "\n"), 0);
      return true;
    });
    child = controlled.child;

    await expect(controlled.transport.request("first", {}, 25)).resolves.toEqual({ ok: true });
    await new Promise((resolve) => setTimeout(resolve, 40));
    expect(child.killCount).toBe(0);
    await expect(controlled.transport.request("second", {}, 200)).resolves.toEqual({ ok: true });
    expect(writes).toEqual([1, 2]);
    await controlled.transport.close();
  });

  it("does not arm a drain deadline when write synchronously triggers its response", async () => {
    let child!: ReturnType<typeof controlledTransport>["child"];
    const controlled = controlledTransport((payload) => {
      const request = JSON.parse(payload) as { id: number };
      child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { ok: true } }) + "\n");
      setTimeout(() => child.stdin.emit("drain"), 60);
      return false;
    });
    child = controlled.child;

    await expect(controlled.transport.request("anything", {}, 20)).resolves.toEqual({ ok: true });
    await new Promise((resolve) => setTimeout(resolve, 30));
    expect(child.killCount).toBe(0);
    await controlled.transport.close();
  });

  it("rejects when the bridge exits while a write is waiting for drain", async () => {
    let child!: ReturnType<typeof controlledTransport>["child"];
    const controlled = controlledTransport(() => {
      setTimeout(() => child.emit("exit", 1, null), 0);
      return false;
    });
    child = controlled.child;

    const request = controlled.transport.request("anything", {}, 80);
    const outcome = await Promise.race([
      request.then(() => "resolved", (error: Error) => error.message),
      new Promise<string>((resolve) => setTimeout(() => resolve("still pending"), 40)),
    ]);
    expect(outcome).toMatch(/bridge exited/);
    await request.catch(() => {});
  });

  it("starts the request deadline while waiting for stdin drain", async () => {
    let child!: ReturnType<typeof controlledTransport>["child"];
    const controlled = controlledTransport(() => false);
    child = controlled.child;
    const request = controlled.transport.request("anything", {}, 20);
    const outcome = await Promise.race([
      request.then(() => "resolved", (error: Error) => error.message),
      new Promise<string>((resolve) => setTimeout(() => resolve("still pending"), 80)),
    ]);
    child.stdin.emit("drain");
    await request.catch(() => {});

    expect(outcome).toMatch(/timed out after 20 ms/);
    await controlled.transport.close();
  });

  it("keeps timeout zero as an unlimited request deadline", async () => {
    let child!: ReturnType<typeof controlledTransport>["child"];
    const controlled = controlledTransport(() => false);
    child = controlled.child;
    const request = controlled.transport.request("anything", {}, 0);
    const beforeDrain = await Promise.race([
      request.then(() => "settled", () => "settled"),
      new Promise<string>((resolve) => setTimeout(() => resolve("waiting"), 30)),
    ]);
    child.stdin.emit("drain");
    child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", id: 1, result: { ok: true } }) + "\n");

    expect(beforeDrain).toBe("waiting");
    await expect(request).resolves.toEqual({ ok: true });
    await controlled.transport.close();
  });

  it("closes a stalled transport at deadline and rejects queued writes", async () => {
    const writes: string[] = [];
    let child!: ReturnType<typeof controlledTransport>["child"];
    const controlled = controlledTransport((payload) => {
      writes.push(payload);
      return false;
    });
    child = controlled.child;

    const first = controlled.transport.request("first", {}, 20);
    const second = controlled.transport.request("second", {}, 100);
    const [firstResult, secondResult] = await Promise.allSettled([first, second]);

    expect(firstResult.status).toBe("rejected");
    expect(secondResult.status).toBe("rejected");
    expect(child.killCount).toBe(1);
    expect(child.stdin.listenerCount("drain")).toBe(0);
    expect(writes).toHaveLength(1);
    child.stdin.emit("drain");
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(writes).toHaveLength(1);
    await expect(controlled.transport.request("after-close")).rejects.toThrow(/timed out|bridge exited|closed/);
  });

  it("rejects with the stdin error when it arrives during a backpressured write", async () => {
    let child!: ReturnType<typeof controlledTransport>["child"];
    const controlled = controlledTransport(() => {
      setTimeout(() => child.stdin.emit("error", new Error("pipe broke")), 0);
      setTimeout(() => child.stdin.emit("drain"), 10);
      return false;
    });
    child = controlled.child;

    await expect(controlled.transport.request("anything", {}, 80)).rejects.toThrow(/bridge stdin error: pipe broke/);
    await controlled.transport.close();
  });

  it("rejects a request when stdin write throws synchronously", async () => {
    const controlled = controlledTransport(() => {
      throw new Error("write exploded");
    });

    await expect(controlled.transport.request("anything")).rejects.toThrow(/bridge stdin write failed.*write exploded/);
    await controlled.transport.close();
  });

  it("serializes overlapping writes until the backpressured frame drains", async () => {
    let child!: ReturnType<typeof controlledTransport>["child"];
    const writes: string[] = [];
    let drains = 0;
    const controlled = controlledTransport((payload) => {
      writes.push(payload);
      const request = JSON.parse(payload) as { id: number };
      setTimeout(() => {
        child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { ok: request.id } }) + "\n");
        child.stdin.emit("drain");
        drains++;
      }, 5);
      return false;
    });
    child = controlled.child;

    const first = controlled.transport.request<{ ok: number }>("first");
    const second = controlled.transport.request<{ ok: number }>("second");
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(writes).toHaveLength(1);
    await expect(first).resolves.toEqual({ ok: 1 });
    await expect(second).resolves.toEqual({ ok: 2 });
    expect(drains).toBe(2);
    await controlled.transport.close();
  });

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

  it.skipIf(process.platform !== "win32")("preserves_safe_literal_argv_for_a_cmd_bridge_in_a_path_with_spaces", async () => {
    const directory = mkdtempSync(path.join(tmpdir(), "ru sdk bridge "));
    const command = path.join(directory, "fake-bridge.cmd");
    let transport: BridgeTransport | undefined;
    try {
      copyFileSync(cmdFixture, command);
      copyFileSync(fixture, path.join(directory, "fake-bridge.mjs"));
      const args = ["two words", "quoted phrase with spaces", "Tiếng Việt 🧡", "", "literal!bang", "trailing slash\\"];
      transport = BridgeTransport.spawn({
        command,
        args,
        agentArgs: [],
        env: { FAKE_BRIDGE_MODE: "echo" },
      });

      const result = await transport.request<{ argv: string[] }>("anything");
      expect(result.argv).toEqual(args);
    } finally {
      await transport?.close();
      rmSync(directory, { recursive: true, force: true });
    }
  });

  it("isolates a throwing notification handler and keeps delivering to the rest", async () => {
    const transport = start();
    const seen: string[] = [];
    transport.onNotification((method) => {
      seen.push(`first:${method}`);
      throw new Error("listener exploded");
    });
    transport.onNotification((method) => seen.push(`second:${method}`));

    // The throwing listener becomes a warning, not an uncaughtException, and
    // the remaining handlers still see the notification.
    await transport.request("run.event.test", {});
    expect(seen).toEqual(["first:run.event", "second:run.event"]);
    await transport.close();
  });

  it.skipIf(process.platform !== "win32")("refuses_cmd_metacharacters_in_arguments", () => {
    expect(() =>
      BridgeTransport.spawn({
        command: cmdFixture,
        args: ["%PATH%"],
        agentArgs: [],
      }),
    ).toThrow(/metacharacters/);
  });

  it.skipIf(process.platform !== "win32")("refuses_embedded_quotes_in_batch_arguments", () => {
    expect(() =>
      BridgeTransport.spawn({
        command: cmdFixture,
        args: ['embedded"quote'],
        agentArgs: [],
      }),
    ).toThrow(/metacharacters/);
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
