import { Subagents } from "./subagents.js";
import { BridgeTransportError } from "./errors.js";
import { BoundedQueue } from "./bounded-queue.js";
import { RunSubscriptionOwnership } from "./run-subscription-ownership.js";
import type { ApprovalDecision, RunEvent, RunTransient } from "./types.js";
import type { BridgeTransport } from "./transport.js";

const TERMINAL_STATUSES = new Set(["completed", "failed", "cancelled", "canceled", "interrupted"]);
const DETACH_REASON_MAX_CHARS = 64;
const MAX_BUFFERED_EVENTS = 4_096;
const MAX_BUFFERED_EVENT_BYTES = 8 * 1024 * 1024;

interface BufferedNotification {
  method: string;
  params: Record<string, unknown>;
}

function attachedSubscriptionId(value: unknown): string | undefined {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return undefined;
  const subscriptionId = (value as Record<string, unknown>).subscription_id;
  return typeof subscriptionId === "string" && subscriptionId.length > 0 ? subscriptionId : undefined;
}

function detachedError(params: Record<string, unknown>): BridgeTransportError {
  const reason =
    typeof params.reason === "string" && params.reason.length > 0 ? params.reason.slice(0, DETACH_REASON_MAX_CHARS) : "unknown";
  return new BridgeTransportError(`run subscription detached before a terminal event (reason: ${reason})`);
}

export class Run {
  constructor(
    readonly transport: BridgeTransport,
    readonly runId: string,
    readonly sessionId: string,
    readonly initialStatus: string,
    readonly workspaceId: string,
  ) {}

  get subagents(): Subagents { return new Subagents(this.transport, this.runId); }

  async *events(options: { includeTransient?: boolean; afterSequence?: number } = {}): AsyncGenerator<RunEvent | RunTransient> {
    const queue = new BoundedQueue<BufferedNotification>(MAX_BUFFERED_EVENTS, MAX_BUFFERED_EVENT_BYTES);
    const ownership = RunSubscriptionOwnership.acquire(this.transport, this.runId);
    let subscriptionId: string | undefined;
    let notify: (() => void) | undefined;
    let finished = false;
    let terminalQueued = false;
    const terminalSubscriptions = new Set<string>();
    let closeError: Error | undefined;
    let lastDeliveredSequence = options.afterSequence ?? 0;
    let resolveOverflow!: (error: BridgeTransportError) => void;
    const overflow = new Promise<BridgeTransportError>((resolve) => {
      resolveOverflow = resolve;
    });
    let overflowError: BridgeTransportError | undefined;
    let attachStarted = false;
    let attachSettled = false;
    let detachAfterAttach = false;
    let detachDeferredForAttach = false;
    let attachPromise: Promise<{ subscription_id: string }> | undefined;
    let exactDetachPromise: Promise<void> | undefined;
    let runWideDetachPromise: Promise<void> | undefined;

    const detachExact = (id: string): Promise<void> => {
      if (!exactDetachPromise) {
        ownership.markCleanupPending();
        exactDetachPromise = this.transport.request("run.detach", { subscriptionId: id })
          .then(() => undefined, () => undefined)
          .finally(() => ownership.markCleanupFinished());
      }
      return exactDetachPromise;
    };

    const detach = (): Promise<void> => {
      if (subscriptionId) return detachExact(subscriptionId);
      if (!attachStarted) {
        ownership.settleWithoutSubscription();
        attachSettled = true;
        return Promise.resolve();
      }
      if (attachSettled) return Promise.resolve();
      if (runWideDetachPromise) return runWideDetachPromise;
      if (detachDeferredForAttach) return Promise.resolve();

      detachAfterAttach = true;
      if (!ownership.hasPeers) {
        if (!runWideDetachPromise) {
          runWideDetachPromise = ownership.runWideDetach(() =>
            this.transport.request("run.detach", { runId: this.runId }).then(() => undefined, () => undefined),
          );
        }
        return runWideDetachPromise;
      }

      // A peer owns (or is waiting for) another subscription of this run. Its
      // late attach id is the only safe target while this response is pending.
      detachDeferredForAttach = true;
      ownership.markCleanupPending();
      return Promise.resolve();
    };

    const failOverflow = (): void => {
      if (closeError instanceof BridgeTransportError && closeError.message.startsWith("run event buffer limit exceeded")) return;
      const error = new BridgeTransportError(
        `run event buffer limit exceeded (${MAX_BUFFERED_EVENTS} events, ${MAX_BUFFERED_EVENT_BYTES} UTF-8 bytes)`,
        undefined,
        lastDeliveredSequence,
      );
      closeError = error;
      overflowError = error;
      queue.clear();
      resolveOverflow(error);
      // If another iterator owns this run, cleanup waits for this attach's
      // subscription id instead of detaching every subscription by run id.
      void detach();
      this.transport.offNotification(handler);
      notify?.();
    };

    const push = (method: string, params: Record<string, unknown>): void => {
      const bytes = Buffer.byteLength(JSON.stringify({ method, params }), "utf8");
      if (!queue.push({ method, params }, bytes)) {
        failOverflow();
        return;
      }
      if (
        method === "run.event" &&
        typeof params.status === "string" &&
        TERMINAL_STATUSES.has(params.status) &&
        params.event_type !== "run.started"
      ) {
        if (typeof params.subscription_id === "string") {
          if (subscriptionId === undefined) terminalSubscriptions.add(params.subscription_id);
          else if (params.subscription_id === subscriptionId) terminalQueued = true;
        }
      }
      notify?.();
    };

    const handler = (method: string, params: Record<string, unknown>): void => {
      if (method === "bridge.shutdown") {
        // Well before the transport notices the child exit, the bridge says why it
        // stopped; surface that instead of hanging until stdout closes.
        closeError ??= new BridgeTransportError("bridge shut down");
        notify?.();
        return;
      }
      if (method === "run.detached") {
        const notificationId = params.subscription_id;
        if (
          typeof notificationId === "string" &&
          (subscriptionId !== undefined ? notificationId !== subscriptionId : ownership.isPeerSubscription(notificationId))
        ) return;
        push(method, params);
        return;
      }
      if (method !== "run.event" && method !== "run.transient") return;
      if (typeof params.run_id === "string" && params.run_id !== this.runId) return;
      if (terminalQueued) return;
      const notificationId = params.subscription_id;
      if (
        typeof notificationId === "string" &&
        (subscriptionId !== undefined ? notificationId !== subscriptionId : ownership.isPeerSubscription(notificationId))
      ) return;
      push(method, params);
    };

    const onClose = (error: Error): void => {
      closeError ??= error;
      notify?.();
    };

    this.transport.onNotification(handler);
    this.transport.onClose(onClose);
    try {
      attachPromise = (async () => {
        await ownership.waitForRunWideCleanup();
        if (overflowError) throw overflowError;
        attachStarted = true;
        const response = await this.transport.request<unknown>("run.attach", {
          runId: this.runId,
          afterSequence: options.afterSequence ?? 0,
          includeTransient: options.includeTransient ?? false,
        });
        const attachedId = attachedSubscriptionId(response);
        if (!attachedId) {
          attachSettled = true;
          if (ownership.hasPeers) {
            // The server may have created a subscription, but without its id we
            // cannot clean it up without also detaching the peer. Close the
            // corrupt transport so its unknown subscription cannot keep running.
            await this.transport.close().catch(() => undefined);
            ownership.settleWithoutSubscription();
          } else {
            runWideDetachPromise = ownership.runWideDetach(() =>
              this.transport.request("run.detach", { runId: this.runId }).then(() => undefined, () => undefined),
            ).finally(() => ownership.settleWithoutSubscription());
            void runWideDetachPromise;
          }
          throw new BridgeTransportError("invalid run.attach response: expected a non-empty subscription_id");
        }
        return { subscription_id: attachedId };
      })();
      void attachPromise.then(
        (attached) => {
          const attachedId = attached.subscription_id;
          subscriptionId = attachedId;
          attachSettled = true;
          ownership.setAttached(attachedId);
          if (detachAfterAttach) {
            const cleanup = runWideDetachPromise
              ? runWideDetachPromise.then(() => detachExact(attachedId))
              : detachExact(attachedId);
            void cleanup;
          }
        },
        () => {
          if (attachSettled) return;
          attachSettled = true;
          ownership.settleWithoutSubscription();
        },
      );
      const attached = await Promise.race([attachPromise, overflow.then((error) => Promise.reject(error))]);
      subscriptionId = attached.subscription_id;
      ownership.setAttached(subscriptionId);
      terminalQueued = terminalSubscriptions.has(subscriptionId);
      terminalSubscriptions.clear();

      while (true) {
        while (queue.length > 0) {
          const buffered = queue.shift()!;
          if (buffered.params.subscription_id !== subscriptionId) continue;
          if (buffered.method === "run.detached") {
            closeError ??= detachedError(buffered.params);
            break;
          }
          if (buffered.method === "run.event") {
            const event = buffered.params as unknown as RunEvent;
            if (typeof event.sequence === "number") lastDeliveredSequence = event.sequence;
            if (event.status && TERMINAL_STATUSES.has(event.status) && event.event_type !== "run.started") finished = true;
            yield event;
            if (finished) return;
          } else {
            yield buffered.params as unknown as RunTransient;
          }
        }
        if (finished) return;
        if (closeError) throw closeError;
        await new Promise<void>((resolve) => {
          notify = resolve;
        });
        notify = undefined;
      }
    } finally {
      this.transport.offNotification(handler);
      this.transport.offClose(onClose);
      await detach();
      ownership.end();
    }
  }

  async approve(approvalId: string, decision: ApprovalDecision = "allow_once"): Promise<Record<string, unknown>> {
    return await this.transport.request<Record<string, unknown>>("run.approve", { runId: this.runId, approvalId, decision });
  }

  async deny(approvalId: string): Promise<Record<string, unknown>> {
    return await this.transport.request<Record<string, unknown>>("run.approve", { runId: this.runId, approvalId, decision: "deny" });
  }

  async cancel(): Promise<void> {
    await this.transport.request("run.cancel", { runId: this.runId });
  }

  async continue(): Promise<void> {
    await this.transport.request("run.continue", { runId: this.runId });
  }

  async status(): Promise<Record<string, unknown>> {
    return await this.transport.request<Record<string, unknown>>("run.status", { runId: this.runId });
  }

  async output(payloadId: string): Promise<Buffer> {
    const result = await this.transport.request<{ encoding: string; data: string }>("run.output", {
      runId: this.runId,
      payloadId,
    });
    if (result.encoding !== "base64") throw new Error(`unsupported output encoding: ${result.encoding}`);
    return Buffer.from(result.data, "base64");
  }
}
