import { describe, expect, it } from "vitest";
import { BridgeTransportError } from "../src/errors.js";
import { Run } from "../src/run.js";
import { controlledTransport } from "./controlled-transport.js";

const EVENT_LIMIT = 4096;

function event(sequence: number, subscriptionId = "sub_1") {
  return {
    subscription_id: subscriptionId,
    run_id: "run_1",
    sequence,
    event_type: "run.progress",
    safe_summary: "progress",
  };
}

describe("Run.events buffering", () => {
  it("fails a paused consumer at the shared event-count limit with a replay sequence", async () => {
    let child!: ReturnType<typeof controlledTransport>["child"];
    const controlled = controlledTransport((payload) => {
      const request = JSON.parse(payload) as { id: number; method: string };
      if (request.method === "run.attach") {
        child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { subscription_id: "sub_1" } }) + "\n");
      } else if (request.method === "run.detach") {
        child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { detached: "sub_1" } }) + "\n");
      }
      return true;
    });
    child = controlled.child;
    const run = new Run(controlled.transport, "run_1", "session_1", "running", "workspace_1");
    const stream = run.events();
    const first = stream.next();
    child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", method: "run.event", params: event(1) }) + "\n");
    await expect(first).resolves.toMatchObject({ value: { sequence: 1 }, done: false });

    for (let index = 2; index <= EVENT_LIMIT + 2; index++) {
      child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", method: "run.event", params: event(index) }) + "\n");
    }
    await expect(stream.next()).rejects.toMatchObject({
      name: "BridgeTransportError",
      lastDeliveredSequence: 1,
    } satisfies Partial<BridgeTransportError>);
    await controlled.transport.close();
  });

  it("detaches by run id immediately when early events overflow before attach response", async () => {
    let child!: ReturnType<typeof controlledTransport>["child"];
    const writes: string[] = [];
    const controlled = controlledTransport((payload) => {
      const request = JSON.parse(payload) as { id: number; method: string };
      writes.push(request.method);
      if (request.method === "run.detach") {
        child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { detached: 1 } }) + "\n");
      }
      return true;
    });
    child = controlled.child;
    const run = new Run(controlled.transport, "run_1", "session_1", "running", "workspace_1");
    const stream = run.events();
    const first = stream.next();
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(writes).toContain("run.attach");
    for (let index = 1; index <= EVENT_LIMIT + 1; index++) {
      child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", method: "run.event", params: event(index) }) + "\n");
    }

    await expect(first).rejects.toThrow(/event buffer limit/i);
    expect(writes).toContain("run.detach");
    await controlled.transport.close();
  });

  it("enforces the queued UTF-8 byte limit even below the event-count limit", async () => {
    let child!: ReturnType<typeof controlledTransport>["child"];
    const controlled = controlledTransport((payload) => {
      const request = JSON.parse(payload) as { id: number; method: string };
      if (request.method === "run.attach") {
        child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { subscription_id: "sub_1" } }) + "\n");
      } else if (request.method === "run.detach") {
        child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { detached: "sub_1" } }) + "\n");
      }
      return true;
    });
    child = controlled.child;
    const run = new Run(controlled.transport, "run_1", "session_1", "running", "workspace_1");
    const stream = run.events();
    const first = stream.next();
    child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", method: "run.event", params: event(1) }) + "\n");
    await expect(first).resolves.toMatchObject({ value: { sequence: 1 }, done: false });

    const largeEvent = { ...event(2), safe_summary: "x".repeat(1_000_000) };
    const frame = (sequence: number) => JSON.stringify({
      jsonrpc: "2.0",
      method: "run.event",
      params: { ...largeEvent, sequence },
    }) + "\n";
    child.stdout.emit("data", Array.from({ length: 9 }, (_, index) => frame(index + 2)).join(""));

    const outcome = stream.next();
    await expect(outcome).rejects.toMatchObject({
      name: "BridgeTransportError",
      message: expect.stringContaining("8388608 UTF-8 bytes"),
      lastDeliveredSequence: 1,
    } satisfies Partial<BridgeTransportError>);
    await controlled.transport.close();
  });

  it("does not count notifications for another subscription of the same run", async () => {
    let child!: ReturnType<typeof controlledTransport>["child"];
    const controlled = controlledTransport((payload) => {
      const request = JSON.parse(payload) as { id: number; method: string };
      if (request.method === "run.attach") {
        child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { subscription_id: "sub_1" } }) + "\n");
      } else if (request.method === "run.detach") {
        child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { detached: "sub_1" } }) + "\n");
      }
      return true;
    });
    child = controlled.child;
    const run = new Run(controlled.transport, "run_1", "session_1", "running", "workspace_1");
    const stream = run.events();
    const first = stream.next();
    child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", method: "run.event", params: event(1) }) + "\n");
    await expect(first).resolves.toMatchObject({ value: { sequence: 1 }, done: false });

    for (let index = 2; index <= EVENT_LIMIT + 2; index++) {
      child.stdout.emit("data", JSON.stringify({
        jsonrpc: "2.0",
        method: "run.event",
        params: event(index, "sub_other"),
      }) + "\n");
    }
    child.stdout.emit("data", JSON.stringify({
      jsonrpc: "2.0",
      method: "run.event",
      params: { ...event(2), status: "completed" },
    }) + "\n");

    await expect(stream.next()).resolves.toMatchObject({ value: { sequence: 2 }, done: false });
    await expect(stream.next()).resolves.toMatchObject({ done: true });
    await controlled.transport.close();
  });

  it("returns a replayable overflow if a close notification precedes the buffer limit", async () => {
    let child!: ReturnType<typeof controlledTransport>["child"];
    const controlled = controlledTransport((payload) => {
      const request = JSON.parse(payload) as { id: number; method: string };
      if (request.method === "run.attach") {
        child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { subscription_id: "sub_1" } }) + "\n");
      } else if (request.method === "run.detach") {
        child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { detached: "sub_1" } }) + "\n");
      }
      return true;
    });
    child = controlled.child;
    const run = new Run(controlled.transport, "run_1", "session_1", "running", "workspace_1");
    const stream = run.events();
    const first = stream.next();
    child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", method: "run.event", params: event(1) }) + "\n");
    await expect(first).resolves.toMatchObject({ value: { sequence: 1 }, done: false });
    child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", method: "bridge.shutdown", params: { reason: "backpressure" } }) + "\n");
    for (let index = 2; index <= EVENT_LIMIT + 2; index++) {
      child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", method: "run.event", params: event(index) }) + "\n");
    }

    await expect(stream.next()).rejects.toMatchObject({
      name: "BridgeTransportError",
      lastDeliveredSequence: 1,
      message: expect.stringContaining("event buffer limit exceeded"),
    } satisfies Partial<BridgeTransportError>);
    await controlled.transport.close();
  });

  it("detaches only the overflowing iterator after a delayed attach while a peer stays active", async () => {
    let child!: ReturnType<typeof controlledTransport>["child"];
    let attachCount = 0;
    let secondAttachId: number | undefined;
    const active = new Set<string>();
    const requests: Array<{ method: string; params: Record<string, unknown>; id: number }> = [];
    const controlled = controlledTransport((payload) => {
      const request = JSON.parse(payload) as { id: number; method: string; params: Record<string, unknown> };
      requests.push(request);
      if (request.method === "run.attach") {
        attachCount++;
        const subscriptionId = `sub_${attachCount}`;
        active.add(subscriptionId);
        if (attachCount === 1) {
          child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { subscription_id: subscriptionId } }) + "\n");
        } else {
          secondAttachId = request.id;
        }
      } else if (request.method === "run.detach") {
        if (typeof request.params.subscriptionId === "string") active.delete(request.params.subscriptionId);
        else if (typeof request.params.runId === "string") active.clear();
        child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { detached: 1 } }) + "\n");
      }
      return true;
    });
    child = controlled.child;
    const firstRun = new Run(controlled.transport, "run_1", "session_1", "running", "workspace_1");
    const firstStream = firstRun.events();
    const firstEvent = firstStream.next();
    child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", method: "run.event", params: event(1) }) + "\n");
    await expect(firstEvent).resolves.toMatchObject({ value: { sequence: 1 }, done: false });

    const secondRun = new Run(controlled.transport, "run_1", "session_1", "running", "workspace_1");
    const secondStream = secondRun.events();
    const secondAttach = secondStream.next();
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(secondAttachId).toBeDefined();

    for (let sequence = 2; sequence <= 4096; sequence++) {
      child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", method: "run.event", params: event(sequence, "sub_1") }) + "\n");
      await firstStream.next();
    }
    const earlySecondSubscriptionEvents = Array.from({ length: 4097 }, (_, index) => JSON.stringify({
      jsonrpc: "2.0",
      method: "run.event",
      params: event(index + 1, "sub_2"),
    }) + "\n").join("");
    child.stdout.emit("data", earlySecondSubscriptionEvents);

    await expect(secondAttach).rejects.toMatchObject({
      name: "BridgeTransportError",
      lastDeliveredSequence: 0,
    });
    expect(requests.filter((request) => request.method === "run.detach")).toHaveLength(0);

    child.stdout.emit("data", JSON.stringify({
      jsonrpc: "2.0",
      id: secondAttachId,
      result: { subscription_id: "sub_2" },
    }) + "\n");
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(requests.find((request) => request.method === "run.detach")?.params).toEqual({ subscriptionId: "sub_2" });
    expect(active.has("sub_1")).toBe(true);
    expect(active.has("sub_2")).toBe(false);

    child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", method: "run.event", params: event(4097, "sub_1") }) + "\n");
    await expect(firstStream.next()).resolves.toMatchObject({ value: { sequence: 4097 }, done: false });
    await firstStream.return();
    await controlled.transport.close();
  });

  it("stops yielding buffered notifications after a terminal event", async () => {
    let child!: ReturnType<typeof controlledTransport>["child"];
    const controlled = controlledTransport((payload) => {
      const request = JSON.parse(payload) as { id: number; method: string };
      if (request.method === "run.attach") {
        child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { subscription_id: "sub_1" } }) + "\n");
        child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", method: "run.event", params: { ...event(1), status: "completed" } }) + "\n");
        child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", method: "run.event", params: event(2) }) + "\n");
      } else if (request.method === "run.detach") {
        child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { detached: "sub_1" } }) + "\n");
      }
      return true;
    });
    child = controlled.child;
    const run = new Run(controlled.transport, "run_1", "session_1", "running", "workspace_1");
    const stream = run.events();

    await expect(stream.next()).resolves.toMatchObject({ value: { sequence: 1 }, done: false });
    await expect(stream.next()).resolves.toMatchObject({ done: true });
    await controlled.transport.close();
  });

  it("waits to attach a new iterator until a sole-owner run-wide detach settles", async () => {
    let child!: ReturnType<typeof controlledTransport>["child"];
    let firstAttachId: number | undefined;
    let secondAttachId: number | undefined;
    let runDetachId: number | undefined;
    const requests: Array<{ id: number; method: string; params: Record<string, unknown> }> = [];
    const controlled = controlledTransport((payload) => {
      const request = JSON.parse(payload) as { id: number; method: string; params: Record<string, unknown> };
      requests.push(request);
      if (request.method === "run.attach") {
        if (firstAttachId === undefined) firstAttachId = request.id;
        else secondAttachId = request.id;
      } else if (request.method === "run.detach" && typeof request.params.runId === "string") {
        runDetachId = request.id;
      } else if (request.method === "run.detach" && typeof request.params.subscriptionId === "string") {
        child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { detached: request.params.subscriptionId } }) + "\n");
      }
      return true;
    });
    child = controlled.child;
    const firstRun = new Run(controlled.transport, "run_1", "session_1", "running", "workspace_1");
    const firstStream = firstRun.events();
    const first = firstStream.next();
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(firstAttachId).toBeDefined();

    const earlyEvents = Array.from({ length: EVENT_LIMIT + 1 }, (_, index) => JSON.stringify({
      jsonrpc: "2.0",
      method: "run.event",
      params: event(index + 1, "sub_1"),
    }) + "\n").join("");
    child.stdout.emit("data", earlyEvents);
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(runDetachId).toBeDefined();

    const secondRun = new Run(controlled.transport, "run_1", "session_1", "running", "workspace_1");
    const secondStream = secondRun.events();
    const second = secondStream.next();
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(requests.filter((request) => request.method === "run.attach")).toHaveLength(1);

    child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", id: runDetachId, result: { detached: 1 } }) + "\n");
    await expect(first).rejects.toThrow(/event buffer limit exceeded/i);
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(secondAttachId).toBeDefined();

    child.stdout.emit("data", JSON.stringify({
      jsonrpc: "2.0",
      id: secondAttachId,
      result: { subscription_id: "sub_2" },
    }) + "\n");
    child.stdout.emit("data", JSON.stringify({
      jsonrpc: "2.0",
      method: "run.event",
      params: event(1, "sub_2"),
    }) + "\n");
    await expect(second).resolves.toMatchObject({ value: { subscription_id: "sub_2" }, done: false });

    child.stdout.emit("data", JSON.stringify({
      jsonrpc: "2.0",
      id: firstAttachId,
      result: { subscription_id: "sub_1" },
    }) + "\n");
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(requests.some((request) => request.method === "run.detach" && request.params.subscriptionId === "sub_1")).toBe(true);
    await secondStream.return();
    await controlled.transport.close();
  });

  it.each([
    ["null result", null],
    ["non-object result", "not-an-object"],
    ["missing subscription id", {}],
    ["empty subscription id", { subscription_id: "" }],
    ["non-string subscription id", { subscription_id: 42 }],
  ])("rejects a malformed attach acknowledgement (%s)", async (_label, result) => {
    let child!: ReturnType<typeof controlledTransport>["child"];
    const requests: Array<{ method: string; params: Record<string, unknown> }> = [];
    const controlled = controlledTransport((payload) => {
      const request = JSON.parse(payload) as { id: number; method: string; params: Record<string, unknown> };
      requests.push(request);
      if (request.method === "run.attach") {
        child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", id: request.id, result }) + "\n");
      } else if (request.method === "run.detach") {
        child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { detached: 1 } }) + "\n");
      }
      return true;
    });
    child = controlled.child;
    const run = new Run(controlled.transport, "run_1", "session_1", "running", "workspace_1");
    const stream = run.events();

    await expect(stream.next()).rejects.toMatchObject({
      name: "BridgeTransportError",
      message: expect.stringContaining("invalid run.attach response"),
    });
    expect(requests.some((request) => request.method === "run.detach" && request.params.runId === "run_1")).toBe(true);
    await controlled.transport.close();
  });

  it("closes the bridge when a malformed attach acknowledgement cannot be detached without affecting a peer", async () => {
    let child!: ReturnType<typeof controlledTransport>["child"];
    let attachCount = 0;
    const requests: Array<{ method: string; params: Record<string, unknown> }> = [];
    const controlled = controlledTransport((payload) => {
      const request = JSON.parse(payload) as { id: number; method: string; params: Record<string, unknown> };
      requests.push(request);
      if (request.method === "run.attach") {
        attachCount++;
        const result = attachCount === 1 ? { subscription_id: "sub_1" } : null;
        child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", id: request.id, result }) + "\n");
      } else if (request.method === "run.detach" && typeof request.params.subscriptionId === "string") {
        child.stdout.emit("data", JSON.stringify({ jsonrpc: "2.0", id: request.id, result: { detached: 1 } }) + "\n");
      }
      return true;
    });
    child = controlled.child;
    const peerRun = new Run(controlled.transport, "run_1", "session_1", "running", "workspace_1");
    const peerStream = peerRun.events();
    const peer = peerStream.next();
    await new Promise((resolve) => setTimeout(resolve, 0));
    child.stdout.emit("data", JSON.stringify({
      jsonrpc: "2.0",
      method: "run.event",
      params: event(1, "sub_1"),
    }) + "\n");
    await expect(peer).resolves.toMatchObject({ value: { subscription_id: "sub_1" }, done: false });
    const waitingPeerEvent = peerStream.next();

    const malformedRun = new Run(controlled.transport, "run_1", "session_1", "running", "workspace_1");
    const malformedStream = malformedRun.events();
    await expect(malformedStream.next()).rejects.toMatchObject({
      name: "BridgeTransportError",
      message: expect.stringContaining("invalid run.attach response"),
    });
    expect(requests.some((request) => request.method === "run.detach" && request.params.runId === "run_1")).toBe(false);
    const peerOutcome = await Promise.race([
      waitingPeerEvent.then(() => "resolved", () => "rejected"),
      new Promise<"timeout">((resolve) => setTimeout(() => resolve("timeout"), 3_000)),
    ]);
    expect(peerOutcome).toBe("rejected");
    await peerStream.return();
    await controlled.transport.close();
  });
});
