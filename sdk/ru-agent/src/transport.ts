import { spawn, type ChildProcessWithoutNullStreams, type SpawnOptions } from "node:child_process";
import { RuRpcError, BridgeTransportError } from "./errors.js";

export interface BridgeSpawnOptions {
  command: string;
  args?: string[];
  agentArgs?: string[];
  cwd?: string;
  env?: Record<string, string>;
}

interface PendingRequest {
  resolve: (value: unknown) => void;
  reject: (error: Error) => void;
  timer?: NodeJS.Timeout;
}

type NotificationHandler = (method: string, params: Record<string, unknown>) => void;

const MAX_BUFFER_CHARS = 1024 * 1024 + 64 * 1024;
const STDERR_TAIL_CHARS = 8 * 1024;

/**
 * Owns one `ru agent rpc` child process: NDJSON framing over stdin/stdout,
 * id-to-promise correlation, notification fan-out, and fail-closed handling
 * when the child dies or emits a non-JSON line.
 */
export class BridgeTransport {
  #child: ChildProcessWithoutNullStreams;
  #buffer = "";
  #pending = new Map<string | number, PendingRequest>();
  #nextId = 1;
  #notificationHandlers: NotificationHandler[] = [];
  #closeHandlers: Array<(error: Error) => void> = [];
  #closed = false;
  #exitError: Error | undefined = undefined;
  #stderrTail = "";

  private constructor(child: ChildProcessWithoutNullStreams) {
    this.#child = child;
    child.stdout.setEncoding("utf8");
    child.stdout.on("data", (chunk: string) => this.#onData(chunk));
    // Drain stderr or a chatty child eventually blocks on a full pipe. Keep a
    // bounded tail so an exit message can explain what the child complained about.
    child.stderr.setEncoding("utf8");
    child.stderr.on("data", (chunk: string) => {
      this.#stderrTail = (this.#stderrTail + chunk).slice(-STDERR_TAIL_CHARS);
    });
    child.on("exit", (code, signal) => {
      this.#fail(new BridgeTransportError(`bridge exited (code=${code ?? "null"}, signal=${signal ?? "null"})${this.#stderrSuffix()}`));
    });
    child.on("error", (error) => {
      this.#fail(new BridgeTransportError(`bridge process error: ${error.message}`));
    });
    child.stdin.on("error", (error) => {
      if (this.#exitError) return;
      this.#fail(new BridgeTransportError(`bridge stdin error: ${error.message}`));
    });
    child.on("close", () => this.#fail(this.#exitError ?? new BridgeTransportError("bridge stdout closed")));
  }

  static spawn(options: BridgeSpawnOptions): BridgeTransport {
    const args = [...(options.args ?? []), ...(options.agentArgs ?? ["agent", "rpc"])];
    const spawnOptions: SpawnOptions = {
      ...(options.cwd !== undefined ? { cwd: options.cwd } : {}),
      env: { ...process.env, ...options.env },
      stdio: ["pipe", "pipe", "pipe"],
      windowsHide: true,
    };
    // Windows cannot exec a .cmd/.bat directly without a shell; cmd.exe /d /s /c
    // runs the batch file and forwards the remaining argv.
    const child = /\.(cmd|bat)$/i.test(options.command)
      ? spawn("cmd.exe", ["/d", "/s", "/c", options.command, ...args], spawnOptions)
      : spawn(options.command, args, spawnOptions);
    return new BridgeTransport(child as ChildProcessWithoutNullStreams);
  }

  onNotification(handler: NotificationHandler): void {
    this.#notificationHandlers.push(handler);
  }

  offNotification(handler: NotificationHandler): void {
    const index = this.#notificationHandlers.indexOf(handler);
    if (index >= 0) this.#notificationHandlers.splice(index, 1);
  }

  onClose(handler: (error: Error) => void): void {
    if (this.#exitError) {
      const error = this.#exitError;
      queueMicrotask(() => handler(error));
      return;
    }
    this.#closeHandlers.push(handler);
  }

  offClose(handler: (error: Error) => void): void {
    const index = this.#closeHandlers.indexOf(handler);
    if (index >= 0) this.#closeHandlers.splice(index, 1);
  }

  async request<T = unknown>(method: string, params: Record<string, unknown> = {}, timeoutMs = 30_000): Promise<T> {
    if (this.#closed) {
      throw this.#exitError ?? new BridgeTransportError("transport is closed");
    }
    if (this.#exitError) {
      throw this.#exitError;
    }
    const id = this.#nextId++;
    return await new Promise<T>((resolve, reject) => {
      const pending: PendingRequest = { resolve: resolve as (value: unknown) => void, reject };
      if (timeoutMs > 0) {
        pending.timer = setTimeout(() => {
          this.#pending.delete(id);
          reject(new BridgeTransportError(`request "${method}" timed out after ${timeoutMs} ms`));
        }, timeoutMs);
      }
      this.#pending.set(id, pending);
      this.#child.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n");
    });
  }

  async close(): Promise<void> {
    if (this.#closed) return;
    this.#closed = true;
    if (this.#exitError !== undefined || this.#child.exitCode !== null) {
      this.#fail(this.#exitError ?? new BridgeTransportError("transport closed"));
      return;
    }
    this.#child.stdin.end();
    await new Promise<void>((resolve) => {
      const timer = setTimeout(() => {
        this.#child.kill();
        resolve();
      }, 2_000);
      this.#child.once("exit", () => {
        clearTimeout(timer);
        resolve();
      });
    });
    this.#fail(this.#exitError ?? new BridgeTransportError("transport closed"));
  }

  #onData(chunk: string): void {
    this.#buffer += chunk;
    let newline = this.#buffer.indexOf("\n");
    while (newline >= 0) {
      const line = this.#buffer.slice(0, newline).replace(/\r$/, "");
      this.#buffer = this.#buffer.slice(newline + 1);
      if (line.length > 0) this.#handleLine(line);
      newline = this.#buffer.indexOf("\n");
    }
    if (this.#buffer.length > MAX_BUFFER_CHARS) {
      this.#rejectAll(new BridgeTransportError("bridge emitted an oversized frame"));
      this.#child.kill();
    }
  }

  #stderrSuffix(): string {
    const tail = this.#stderrTail.trim();
    return tail.length > 0 ? `; stderr: ${tail}` : "";
  }

  #handleLine(line: string): void {
    let message: Record<string, unknown>;
    try {
      message = JSON.parse(line) as Record<string, unknown>;
    } catch {
      this.#rejectAll(new BridgeTransportError("bridge emitted invalid json on stdout", line.slice(0, 200)));
      this.#child.kill();
      return;
    }

    if (typeof message.method === "string") {
      const params = (message.params as Record<string, unknown>) ?? {};
      for (const handler of this.#notificationHandlers) handler(message.method, params);
      return;
    }

    const id = message.id as string | number | null;
    if (id === null || id === undefined) return;
    const pending = this.#pending.get(id);
    if (!pending) return;
    this.#pending.delete(id);
    if (pending.timer) clearTimeout(pending.timer);
    if (message.error) {
      const error = message.error as { code: number; message: string; data?: { ruCode?: string; httpStatus?: number } };
      pending.reject(new RuRpcError(error.message, error.code, error.data?.ruCode, error.data?.httpStatus));
    } else {
      pending.resolve(message.result ?? {});
    }
  }

  #rejectAll(error: Error): void {
    for (const [id, pending] of this.#pending) {
      this.#pending.delete(id);
      if (pending.timer) clearTimeout(pending.timer);
      pending.reject(error);
    }
  }

  #fail(error: Error): void {
    const failure = this.#exitError ?? error;
    this.#exitError = failure;
    this.#rejectAll(failure);
    if (this.#closeHandlers.length === 0) return;
    const handlers = this.#closeHandlers;
    this.#closeHandlers = [];
    for (const handler of handlers) handler(failure);
  }
}
