import { EventEmitter } from "node:events";
import { StringDecoder } from "node:string_decoder";
import { BridgeTransport } from "../src/transport.js";

export function controlledTransport(write: (payload: string) => boolean) {
  const child = new EventEmitter() as EventEmitter & {
    stdout: EventEmitter & { setEncoding(encoding: string): void; emitBytes(chunk: Buffer): void };
    stderr: EventEmitter & { setEncoding(encoding: string): void };
    stdin: EventEmitter & { write(payload: string): boolean; end(): void };
    exitCode: number | null;
    kill(): void;
    killCount: number;
  };
  const stdoutDecoder = new StringDecoder("utf8");
  child.stdout = Object.assign(new EventEmitter(), {
    setEncoding: (_encoding: string) => {},
    emitBytes: (chunk: Buffer) => {
      const decoded = stdoutDecoder.write(chunk);
      if (decoded.length > 0) child.stdout.emit("data", decoded);
    },
  });
  child.stderr = Object.assign(new EventEmitter(), { setEncoding: (_encoding: string) => {} });
  child.stdin = Object.assign(new EventEmitter(), {
    write,
    end: () => queueMicrotask(() => child.emit("exit", 0, null)),
  });
  child.exitCode = null;
  child.killCount = 0;
  child.kill = () => {
    child.killCount++;
    child.emit("exit", null, "SIGTERM");
  };
  const Transport = BridgeTransport as unknown as new (child: unknown) => BridgeTransport;
  return { child, transport: new Transport(child) };
}
