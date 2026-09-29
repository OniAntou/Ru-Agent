import { BridgeTransport } from "./transport.js";
import { Run } from "./run.js";
import type { RunHandleInfo, RuAgentStartOptions } from "./types.js";

export class RuAgent {
  #transport: BridgeTransport;

  private constructor(transport: BridgeTransport) {
    this.#transport = transport;
  }

  static async start(options: RuAgentStartOptions = {}): Promise<RuAgent> {
    const transport = BridgeTransport.spawn({
      command: options.command ?? process.env.RU_CLI_PATH ?? "ru",
      ...(options.args !== undefined ? { args: options.args } : {}),
      agentArgs: options.agentArgs ?? ["agent", "rpc"],
      ...(options.cwd !== undefined ? { cwd: options.cwd } : {}),
      ...(options.env !== undefined ? { env: options.env } : {}),
    });
    const agent = new RuAgent(transport);
    try {
      await transport.request("initialize", {
        protocolVersion: 1,
        clientInfo: { name: "@ru/agent-sdk", version: "0.1.0" },
      });
    } catch (error) {
      await transport.close().catch(() => undefined);
      throw error;
    }
    return agent;
  }

  get sessions() {
    return {
      create: async (): Promise<string> => {
        const result = await this.#transport.request<{ session_id: string }>("session.create", {});
        return result.session_id;
      },
      list: (): Promise<Record<string, unknown>> => this.#transport.request<Record<string, unknown>>("session.list", {}),
      fork: (sessionId: string, index: number, title?: string): Promise<Record<string, unknown>> =>
        this.#transport.request<Record<string, unknown>>("session.fork", { sessionId, index, ...(title ? { title } : {}) }),
      rewind: (sessionId: string, index: number): Promise<Record<string, unknown>> =>
        this.#transport.request<Record<string, unknown>>("session.rewind", { sessionId, index }),
    };
  }

  get runs() {
    return {
      start: async (request: {
        workspace: string;
        prompt: string;
        preset?: string;
        sessionId?: string;
        trustWorkspace?: boolean;
        confirmFullAccess?: boolean;
      }): Promise<Run> => {
        const info = await this.#transport.request<RunHandleInfo>("run.start", request);
        return new Run(this.#transport, info.run_id, info.session_id, info.status, info.workspace_id);
      },
      attach: (runId: string, sessionId = ""): Run => new Run(this.#transport, runId, sessionId, "unknown", ""),
    };
  }

  async close(): Promise<void> {
    await this.#transport.close();
  }
}
