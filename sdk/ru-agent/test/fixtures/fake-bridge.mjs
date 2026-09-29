#!/usr/bin/env node
import readline from "node:readline";

const mode = process.env.FAKE_BRIDGE_MODE ?? "echo";
const rl = readline.createInterface({ input: process.stdin });

function send(message) {
  process.stdout.write(JSON.stringify(message) + "\n");
}

rl.on("line", (line) => {
  const request = JSON.parse(line);
  if (mode === "crash") {
    process.exit(7);
  }
  if (mode === "garbage") {
    process.stdout.write("not json at all\n");
    return;
  }
  if (request.method === "initialize") {
    send({ jsonrpc: "2.0", id: request.id, result: { protocolVersion: 1, methods: ["initialize"], notifications: [] } });
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
    send({ jsonrpc: "2.0", id: request.id, result: { subscription_id: "sub_1", run_id: request.params.runId, include_transient: false } });
    send({ jsonrpc: "2.0", method: "run.event", params: { subscription_id: "sub_1", sequence: 1, event_type: "run.started", status: "running" } });
    send({ jsonrpc: "2.0", method: "run.event", params: { subscription_id: "sub_1", sequence: 2, event_type: "run.completed", status: "completed" } });
    return;
  }
  if (request.method === "run.detach") {
    send({ jsonrpc: "2.0", id: request.id, result: { detached: request.params.subscriptionId } });
    return;
  }
  send({ jsonrpc: "2.0", id: request.id, result: { echoed: request.params ?? {} } });
});

rl.on("close", () => process.exit(0));
