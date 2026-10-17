# @ru/agent-sdk

TypeScript client for the Ru agent RPC bridge. It spawns `ru agent rpc` as a child
process, speaks newline-delimited JSON-RPC over stdio (protocol v1), and exposes
typed session and run handles, including streaming run events.

## Requirements

- Node.js >= 22
- A Ru CLI binary reachable either as `ru` on `PATH` or through the `RU_CLI_PATH`
  environment variable (or pass `command` explicitly).

## Usage

```ts
import { RuAgent } from "@ru/agent-sdk";

const ru = await RuAgent.start();

const sessionId = await ru.sessions.create();
const run = await ru.runs.start({ workspace: "C:\\work\\repo", prompt: "fix the failing test", sessionId });

for await (const event of run.events()) {
  console.log(event.sequence, event.event_type, event.status ?? "");
}

await run.cancel();
await ru.close();
```

`RuAgent.start` accepts `command`, `args`, `agentArgs`, `cwd`, and `env` to
control how the bridge process is spawned, and rejects the handshake if the
bridge does not advertise every method the wrapper can call. `run.events()` can
also stream transient events (`{ includeTransient: true }`) and replay from a
sequence number (`{ afterSequence: n }`). It ends on a terminal event; if the
bridge detaches the subscription first (`run.detached`) or shuts down, the
iterator throws a bounded `BridgeTransportError` instead of hanging.

Each iterator buffers at most 4,096 notifications and 8 MiB of serialized UTF-8,
including notifications arriving before attach acknowledgement. A slow consumer
that exceeds either limit stops buffering and receives a
`BridgeTransportError` carrying `lastDeliveredSequence`; use it as `afterSequence`
on a new iterator to replay durable events. Buffered events are discarded on
overflow, and transient events are not replayable. This does not cancel the run.
Known peer subscriptions do not consume its buffer. Cleanup detaches only this
subscription when peers exist; if attach acknowledgement is still pending,
cleanup waits for its subscription ID without delaying the overflow error.
An invalid attach response also throws `BridgeTransportError`. If peers exist
and the response omits a usable subscription ID, the SDK cannot detach that
unknown subscription safely, so it closes the bridge transport. Other iterators
on that transport fail too; this does not cancel their Host runs.

Complete and partial RPC frames are bounded to 1 MiB + 64 KiB of UTF-8 before
parsing. Invalid envelopes, including null roots and non-object notification
params, close the transport and fail pending requests and iterators.

RPC request deadlines include time queued behind stdin backpressure. Requests
are correlated before their frames are written, so a response arriving before
`drain` is retained. An unanswered write that remains backpressured through its
deadline closes the transport and rejects queued requests; expired queued
frames are removed. A completed response cancels that request's write deadline.
This does not send `run.cancel` to the shared Host.

## Protocol

This SDK targets RPC protocol **v1**. The wire contract (methods,
notifications, and error codes) is documented in
[`docs/operations/rpc.md`](../../docs/operations/rpc.md).

## Development

```sh
npm install
npm test        # vitest
npm run typecheck
npm run build   # tsup -> dist/
```
