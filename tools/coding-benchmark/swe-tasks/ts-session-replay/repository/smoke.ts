import { SessionReplay } from "./src/sessionReplay.ts";

const replay = new SessionReplay("s", 1);
const status = replay.apply({ sessionId: "s", generation: 1, sequence: 1, kind: "delta", text: "hello" });
if (status !== "applied" || replay.text !== "hello" || replay.lastSequence !== 1) {
  throw new Error("basic replay contract failed");
}
console.log("session replay smoke passed");
