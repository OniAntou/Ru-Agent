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
  send({ jsonrpc: "2.0", id: request.id, result: { echoed: request.params ?? {} } });
});

rl.on("close", () => process.exit(0));
