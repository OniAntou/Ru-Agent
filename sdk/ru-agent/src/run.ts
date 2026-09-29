import { BridgeTransportError } from "./errors.js";
import type { ApprovalDecision, RunEvent, RunTransient } from "./types.js";
import type { BridgeTransport } from "./transport.js";

const TERMINAL_STATUSES = new Set(["completed", "failed", "cancelled", "canceled", "interrupted"]);

export class Run {
  constructor(
    readonly transport: BridgeTransport,
    readonly runId: string,
    readonly sessionId: string,
    readonly initialStatus: string,
    readonly workspaceId: string,
  ) {}

  async *events(options: { includeTransient?: boolean; afterSequence?: number } = {}): AsyncGenerator<RunEvent | RunTransient> {
    const queue: Array<RunEvent | RunTransient> = [];
    const early: Array<{ method: string; params: Record<string, unknown> }> = [];
    let subscriptionId: string | undefined;
    let notify: (() => void) | undefined;
    let finished = false;
    let closeError: Error | undefined;

    const push = (method: string, params: Record<string, unknown>): void => {
      if (method === "run.event") {
        const event = params as unknown as RunEvent;
        queue.push(event);
        if (event.status && TERMINAL_STATUSES.has(event.status) && event.event_type !== "run.started") finished = true;
      } else {
        queue.push(params as unknown as RunTransient);
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
      if (method !== "run.event" && method !== "run.transient") return;
      if (subscriptionId === undefined) {
        early.push({ method, params });
        return;
      }
      if (params.subscription_id !== subscriptionId) return;
      push(method, params);
    };

    const onClose = (error: Error): void => {
      closeError ??= error;
      notify?.();
    };

    this.transport.onNotification(handler);
    this.transport.onClose(onClose);
    try {
      const attached = await this.transport.request<{ subscription_id: string }>("run.attach", {
        runId: this.runId,
        afterSequence: options.afterSequence ?? 0,
        includeTransient: options.includeTransient ?? false,
      });
      subscriptionId = attached.subscription_id;
      for (const buffered of early) {
        if (buffered.params.subscription_id === subscriptionId) push(buffered.method, buffered.params);
      }
      early.length = 0;

      while (true) {
        while (queue.length > 0) yield queue.shift()!;
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
      if (subscriptionId !== undefined && subscriptionId !== "") {
        await this.transport.request("run.detach", { subscriptionId }).catch(() => undefined);
      }
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
