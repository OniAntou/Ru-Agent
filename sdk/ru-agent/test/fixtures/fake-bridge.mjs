#!/usr/bin/env node
import readline from "node:readline";

const mode = process.env.FAKE_BRIDGE_MODE ?? "echo";
const rl = readline.createInterface({ input: process.stdin });

const METHODS = [
  "initialize",
  "host.status",
  "session.create",
  "session.list",
  "session.rewind",
  "run.start",
  "run.attach",
  "run.detach",
  "run.trust",
  "run.approve",
  "run.cancel",
  "run.continue",
  "run.status",
  "run.command",
  "run.reconcile",
  "run.output",
  "policy.list",
  "policy.import",
  "policy.remove",
];
const NOTIFICATIONS = ["run.event", "run.transient", "run.detached", "bridge.shutdown"];

function send(message, callback) {
  process.stdout.write(JSON.stringify(message) + "\n", callback);
}

rl.on("line", (line) => {
  const request = JSON.parse(line);
  if (mode === "crash") {
    process.exit(7);
  }
  if (mode === "crash-with-stderr") {
    process.stderr.write("bridge_stderr_marker\n", () => setTimeout(() => process.exit(7), 100));
    return;
  }
  if (mode === "garbage") {
    process.stdout.write("not json at all\n");
    return;
  }
  if (request.method === "initialize") {
    const methods = METHODS.filter((method) => method !== process.env.FAKE_BRIDGE_DROP_METHOD);
    send({ jsonrpc: "2.0", id: request.id, result: { protocolVersion: 1, methods, notifications: NOTIFICATIONS } });
    return;
  }
  if (request.method === "anything") {
    send({ jsonrpc: "2.0", id: request.id, result: { echoed: request.params ?? {} } });
    return;
  }
  if (request.method === "run.event.test") {
    send({ jsonrpc: "2.0", method: "run.event", params: { sequence: 1 } });
    send({ jsonrpc: "2.0", id: request.id, result: { ok: true } });
    return;
  }
  if (request.method === "session.create") {
    send({ jsonrpc: "2.0", id: request.id, result: { session_id: "sess_1" } });
    return;
  }
  if (request.method === "run.start") {
    send({ jsonrpc: "2.0", id: request.id, result: { run_id: "run_" + "a".repeat(32), session_id: "sess_1", status: "running", workspace_id: "w1" } });
    return;
  }
  if (request.method === "run.attach") {
    if (mode === "detach-before-attach") {
      // The ordering a real bridge can produce: the pump sees the stream end and
      // announces the detach before the attach response has reached the SDK. No
      // run.event follows: the subscription is gone.
      send({ jsonrpc: "2.0", method: "run.detached", params: { subscription_id: "sub_1", reason: "stream_interrupted" } });
      send({ jsonrpc: "2.0", id: request.id, result: { subscription_id: "sub_1", run_id: request.params.runId, include_transient: false } });
      return;
    }
    const attached = { jsonrpc: "2.0", id: request.id, result: { subscription_id: "sub_1", run_id: request.params.runId, include_transient: false } };
    if (mode === "die-after-attach") {
      send(attached, () => process.exit(9));
      return;
    }
    send(attached);
    if (mode === "shutdown-after-attach") {
      send({ jsonrpc: "2.0", method: "bridge.shutdown", params: { reason: "backpressure" } });
      return;
    }
    if (mode === "detach-after-attach") {
      send({ jsonrpc: "2.0", method: "run.detached", params: { subscription_id: "sub_1", reason: "stream_interrupted" } });
      return;
    }
    send({ jsonrpc: "2.0", method: "run.event", params: { subscription_id: "sub_1", sequence: 1, event_type: "run.started", status: "running" } });
    send({ jsonrpc: "2.0", method: "run.event", params: { subscription_id: "sub_1", sequence: 2, event_type: "run.completed", status: "completed" } });
    return;
  }
  if (request.method === "run.detach") {
    send({ jsonrpc: "2.0", id: request.id, result: { detached: request.params.subscriptionId } });
    return;
  }
  if (request.method === "run.approve") {
    send({ jsonrpc: "2.0", id: request.id, result: { ok: true, approvalId: request.params.approvalId, decision: request.params.decision } });
    return;
  }
  if (request.method === "run.cancel") {
    send({ jsonrpc: "2.0", id: request.id, result: { cancelled: request.params.runId } });
    return;
  }
  if (request.method === "run.output") {
    const data = Buffer.from("hello", "utf8");
    send({ jsonrpc: "2.0", id: request.id, result: { encoding: "base64", data: data.toString("base64"), byte_length: data.length } });
    return;
  }
  send({ jsonrpc: "2.0", id: request.id, error: { code: -32601, message: "method_not_found: " + request.method } });
});

rl.on("close", () => process.exit(0));
