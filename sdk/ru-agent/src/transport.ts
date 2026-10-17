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

interface QueuedWrite {
  id: string | number;
  method: string;
  payload?: string;
  deadlineAt?: number;
  timeoutMs?: number;
  writeTimer?: NodeJS.Timeout;
}

type NotificationHandler = (method: string, params: Record<string, unknown>) => void;

// Bound each frame before JSON.parse. The extra 64 KiB above the bridge's
// payload cap allows its bounded envelope and read chunks.
const MAX_FRAME_BYTES = 1024 * 1024 + 64 * 1024;
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
  #writeQueue: QueuedWrite[] = [];
  #writing = false;
  #activeWrite: QueuedWrite | undefined;
  #cancelDrainWait: ((error: Error) => void) | undefined;
  #nextId = 1;
  #notificationHandlers: NotificationHandler[] = [];
  #closeHandlers: Array<(error: Error) => void> = [];
  #closed = false;
  #exitError: Error | undefined = undefined;
  #stderrTail = "";
  #killRequested = false;

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
    child.stdin.on("close", () => this.#fail(this.#exitError ?? new BridgeTransportError("bridge stdin closed")));
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
    // runs the batch file and forwards the remaining argv. cmd performs %VAR%
    // expansion and metacharacter interpretation on its own command line and
    // offers no reliable way to escape those inside an argument, so an argument
    // that carries them is refused rather than forwarded in a mangled shape.
    if (/\.(cmd|bat)$/i.test(options.command)) {
      const unsafe = args.filter((arg) => /[%^&|<>]/.test(arg));
      if (unsafe.length > 0) {
        throw new BridgeTransportError(
          `bridge spawn refused: cmd.exe metacharacters cannot be escaped safely in arguments: ${unsafe.join(", ")}`,
        );
      }
    }
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
    const payload = JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n";
    const deadlineAt = timeoutMs > 0 ? Date.now() + timeoutMs : undefined;
    const response = new Promise<T>((resolve, reject) => {
      const pending: PendingRequest = { resolve: resolve as (value: unknown) => void, reject };
      if (timeoutMs > 0) {
        pending.timer = setTimeout(() => {
          this.#onRequestTimeout(id, new BridgeTransportError(`request "${method}" timed out after ${timeoutMs} ms`));
        }, timeoutMs);
      }
      this.#pending.set(id, pending);
    });
    const queuedWrite: QueuedWrite = {
      id,
      method,
      payload,
      ...(deadlineAt !== undefined ? { deadlineAt, timeoutMs } : {}),
    };
    this.#writeQueue.push(queuedWrite);
    this.#pumpWrites();
    return response;
  }

  /**
   * Pumps one frame at a time, waiting for drain before writing the next so
   * concurrent requests cannot bypass the child's stdin backpressure signal.
   */
  #pumpWrites(): void {
    if (this.#writing || this.#closed || this.#exitError) return;
    this.#writing = true;
    void this.#drainWrites().finally(() => {
      this.#writing = false;
      if (this.#writeQueue.length > 0) this.#pumpWrites();
    });
  }

  async #drainWrites(): Promise<void> {
    while (this.#writeQueue.length > 0 && !this.#closed && !this.#exitError) {
      const queuedWrite = this.#writeQueue.shift()!;
      if (!this.#pending.has(queuedWrite.id)) {
        delete queuedWrite.payload;
        continue;
      }
      if (queuedWrite.deadlineAt !== undefined && Date.now() >= queuedWrite.deadlineAt) {
        this.#onRequestTimeout(queuedWrite.id, this.#timeoutError(queuedWrite));
        continue;
      }

      this.#activeWrite = queuedWrite;
      try {
        if (this.#writeQueuedFrame(queuedWrite)) await this.#waitForDrain(queuedWrite);
      } catch (error) {
        const failure = error instanceof BridgeTransportError
          ? error
          : new BridgeTransportError(`bridge stdin write failed for "${queuedWrite.method}": ${error instanceof Error ? error.message : String(error)}`);
        this.#rejectPending(queuedWrite.id, failure);
      } finally {
        if (this.#activeWrite === queuedWrite) this.#activeWrite = undefined;
        if (queuedWrite.writeTimer) clearTimeout(queuedWrite.writeTimer);
        delete queuedWrite.payload;
      }
    }
  }

  #writeQueuedFrame(queuedWrite: QueuedWrite): boolean {
    const payload = queuedWrite.payload;
    delete queuedWrite.payload;
    if (payload === undefined) return false;
    try {
      return !this.#child.stdin.write(payload);
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      throw new BridgeTransportError(`bridge stdin write failed for "${queuedWrite.method}": ${message}`);
    }
  }

  #waitForDrain(queuedWrite: QueuedWrite): Promise<void> {
    return new Promise<void>((resolve, reject) => {
      let cancel: ((error: Error) => void) | undefined;
      const finish = (error?: Error) => {
        this.#child.stdin.off("drain", onDrain);
        this.#child.stdin.off("error", onStdinError);
        this.#child.stdin.off("close", onStdinClose);
        this.#child.off("error", onChildError);
        this.#child.off("exit", onExit);
        this.#child.off("close", onChildClose);
        if (queuedWrite.writeTimer) {
          clearTimeout(queuedWrite.writeTimer);
          delete queuedWrite.writeTimer;
        }
        if (this.#cancelDrainWait === cancel) this.#cancelDrainWait = undefined;
        if (error) reject(error);
        else resolve();
      };
      const onDrain = () => finish();
      const onStdinError = (error: Error) => finish(this.#exitError ?? new BridgeTransportError(`bridge stdin error: ${error.message}`));
      const onStdinClose = () => finish(this.#exitError ?? new BridgeTransportError("bridge stdin closed"));
      const onChildError = (error: Error) => finish(this.#exitError ?? new BridgeTransportError(`bridge process error: ${error.message}`));
      const onExit = (code: number | null, signal: NodeJS.Signals | null) => finish(
        this.#exitError ?? new BridgeTransportError(`bridge exited (code=${code ?? "null"}, signal=${signal ?? "null"})${this.#stderrSuffix()}`),
      );
      const onChildClose = () => finish(this.#exitError ?? new BridgeTransportError("bridge stdout closed"));

      this.#child.stdin.on("drain", onDrain);
      this.#child.stdin.on("error", onStdinError);
      this.#child.stdin.on("close", onStdinClose);
      this.#child.on("error", onChildError);
      this.#child.on("exit", onExit);
      this.#child.on("close", onChildClose);
      cancel = (error) => finish(error);
      this.#cancelDrainWait = cancel;

      // An early response settles the call but does not release the drain gate;
      // only an unanswered request may time out and abort its stalled writer.
      if (this.#pending.has(queuedWrite.id) && queuedWrite.deadlineAt !== undefined && queuedWrite.timeoutMs !== undefined) {
        queuedWrite.writeTimer = setTimeout(() => {
          this.#abortBackpressuredWrite(this.#timeoutError(queuedWrite));
        }, Math.max(0, queuedWrite.deadlineAt - Date.now()));
      }

      if (this.#exitError) finish(this.#exitError);
      else if (this.#closed) finish(new BridgeTransportError("transport is closed"));
    });
  }

  #timeoutError(queuedWrite: QueuedWrite): BridgeTransportError {
    return new BridgeTransportError(`request "${queuedWrite.method}" timed out after ${queuedWrite.timeoutMs} ms`);
  }

  #onRequestTimeout(id: string | number, error: Error): void {
    const didTimeout = this.#rejectPending(id, error);
    const queuedIndex = this.#writeQueue.findIndex((queuedWrite) => queuedWrite.id === id);
    if (queuedIndex >= 0) {
      delete this.#writeQueue[queuedIndex]!.payload;
      this.#writeQueue.splice(queuedIndex, 1);
    }
    if (didTimeout && this.#activeWrite?.id === id && this.#cancelDrainWait) this.#abortBackpressuredWrite(error);
  }

  #abortBackpressuredWrite(error: Error): void {
    if (this.#closed) return;
    this.#closed = true;
    this.#fail(error);
    this.#cancelDrainWait?.(error);
    this.#killChild();
  }

  #killChild(): void {
    if (this.#killRequested) return;
    this.#killRequested = true;
    this.#child.kill();
  }

  #protocolFailure(message: string, detail?: string): void {
    if (this.#exitError) return;
    const error = new BridgeTransportError(message, detail);
    this.#closed = true;
    this.#fail(error);
    this.#cancelDrainWait?.(error);
    this.#killChild();
  }

  #clearWriteQueue(): void {
    for (const queuedWrite of this.#writeQueue) delete queuedWrite.payload;
    this.#writeQueue = [];
  }

  #rejectPending(id: string | number, error: Error): boolean {
    const pending = this.#pending.get(id);
    if (!pending) return false;
    this.#pending.delete(id);
    if (pending.timer) clearTimeout(pending.timer);
    pending.reject(error);
    return true;
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
    if (this.#closed || this.#exitError) return;
    let offset = 0;
    while (offset < chunk.length) {
      const newline = chunk.indexOf("\n", offset);
      if (newline < 0) {
        const suffix = chunk.slice(offset);
        const suffixBytes = Buffer.byteLength(suffix, "utf8");
        const bufferedBytes = Buffer.byteLength(this.#buffer, "utf8");
        const partial = this.#buffer + suffix;
        const measuredPartial = partial.endsWith("\r") ? partial.slice(0, -1) : partial;
        if (suffixBytes + bufferedBytes > MAX_FRAME_BYTES + 1 || Buffer.byteLength(measuredPartial, "utf8") > MAX_FRAME_BYTES) {
          this.#protocolFailure("bridge emitted an oversized frame");
          return;
        }
        this.#buffer = partial;
        return;
      }

      const segment = chunk.slice(offset, newline);
      if (Buffer.byteLength(this.#buffer, "utf8") + Buffer.byteLength(segment, "utf8") > MAX_FRAME_BYTES + 1) {
        this.#protocolFailure("bridge emitted an oversized frame");
        return;
      }
      const candidate = this.#buffer + segment;
      const line = candidate.endsWith("\r") ? candidate.slice(0, -1) : candidate;
      if (Buffer.byteLength(line, "utf8") > MAX_FRAME_BYTES) {
        this.#protocolFailure("bridge emitted an oversized frame");
        return;
      }
      this.#buffer = "";
      if (line.length > 0) this.#handleLine(line);
      if (this.#closed || this.#exitError) return;
      offset = newline + 1;
    }
  }

  #stderrSuffix(): string {
    const tail = this.#stderrTail.trim();
    return tail.length > 0 ? `; stderr: ${tail}` : "";
  }

  /**
   * A notification handler threw. The error is surfaced as a process warning
   * (stderr, non-fatal) because no other channel exists this deep in the
   * transport, and swallowing it silently would hide the only diagnostic.
   */
  #reportHandlerError(method: string, error: unknown): void {
    const message = error instanceof Error ? error.message : String(error);
    process.emitWarning(`ru-agent: notification handler for "${method}" failed: ${message}`);
  }

  #handleLine(line: string): void {
    let parsed: unknown;
    try {
      parsed = JSON.parse(line) as unknown;
    } catch {
      this.#protocolFailure("bridge emitted invalid json on stdout", line.slice(0, 200));
      return;
    }

    if (!this.#isRecord(parsed) || !this.#isValidEnvelope(parsed)) {
      this.#protocolFailure("bridge emitted invalid json-rpc envelope");
      return;
    }
    const message = parsed;

    if (typeof message.method === "string") {
      const params = message.params as Record<string, unknown>;
      // Iterate a snapshot: a handler may subscribe or unsubscribe while it
      // runs. Each handler is isolated -- a throwing listener must not become
      // an uncaughtException or stop the remaining frames from being processed.
      for (const handler of [...this.#notificationHandlers]) {
        try {
          handler(message.method, params);
        } catch (error) {
          this.#reportHandlerError(message.method, error);
        }
      }
      return;
    }

    const id = message.id as string | number | null;
    if (id === null || id === undefined) return;
    const pending = this.#pending.get(id);
    if (!pending) return;
    this.#pending.delete(id);
    if (pending.timer) clearTimeout(pending.timer);
    if (this.#activeWrite?.id === id && this.#activeWrite.writeTimer) {
      clearTimeout(this.#activeWrite.writeTimer);
      delete this.#activeWrite.writeTimer;
    }
    if ("error" in message) {
      const error = message.error as { code: number; message: string; data?: { ruCode?: string; httpStatus?: number } };
      pending.reject(new RuRpcError(error.message, error.code, error.data?.ruCode, error.data?.httpStatus));
    } else {
      pending.resolve(message.result);
    }
  }

  #isRecord(value: unknown): value is Record<string, unknown> {
    return typeof value === "object" && value !== null && !Array.isArray(value);
  }

  #isValidEnvelope(message: Record<string, unknown>): boolean {
    if (message.jsonrpc !== "2.0") return false;
    if ("method" in message) {
      return typeof message.method === "string" && this.#isRecord(message.params);
    }
    if (!("id" in message) || !(typeof message.id === "string" || typeof message.id === "number" || message.id === null)) {
      return false;
    }
    const hasResult = "result" in message;
    const hasError = "error" in message;
    if (hasResult === hasError) return false;
    if (!hasError) return true;
    if (!this.#isRecord(message.error)) return false;
    const error = message.error;
    if (typeof error.code !== "number" || !Number.isFinite(error.code) || typeof error.message !== "string") return false;
    return !("data" in error) || this.#isRecord(error.data);
  }

  #rejectAll(error: Error): void {
    this.#clearWriteQueue();
    for (const [id, pending] of this.#pending) {
      this.#rejectPending(id, error);
    }
  }

  #fail(error: Error): void {
    const failure = this.#exitError ?? error;
    this.#exitError = failure;
    this.#rejectAll(failure);
    if (this.#closeHandlers.length === 0) return;
    const handlers = this.#closeHandlers;
    this.#closeHandlers = [];
    for (const handler of handlers) {
      try {
        handler(failure);
      } catch (error) {
        this.#reportHandlerError("close", error);
      }
    }
  }
}
