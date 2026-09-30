import { existsSync } from "node:fs";
import path from "node:path";
import { BridgeTransport } from "./transport.js";
import { Run } from "./run.js";
import type { RunHandleInfo, RuAgentStartOptions } from "./types.js";

/**
 * Resolves the command used to spawn the bridge: an explicit `command` wins,
 * then `RU_CLI_PATH`, then the checkout's `bin/ru.cmd` when the SDK runs inside
 * the repo, and finally plain `ru` from PATH. Never throws.
 */
export function resolveCliCommand(options: Pick<RuAgentStartOptions, "command"> = {}): string {
  if (options.command !== undefined) return options.command;
  const fromEnv = process.env.RU_CLI_PATH;
  if (fromEnv !== undefined) return fromEnv;
  try {
    const candidates = [
      path.resolve(import.meta.dirname, "..", "..", "bin", "ru.cmd"),
      path.resolve(import.meta.dirname, "..", "..", "..", "bin", "ru.cmd"),
    ];
    for (const candidate of candidates) {
      if (existsSync(candidate)) return candidate;
    }
  } catch {
    // Resolution must never throw: a missing repo bin falls back to PATH.
  }
  return "ru";
}

export class RuAgent {
  #transport: BridgeTransport;

  private constructor(transport: BridgeTransport) {
    this.#transport = transport;
  }

  static async start(options: RuAgentStartOptions = {}): Promise<RuAgent> {
    const transport = BridgeTransport.spawn({
      command: resolveCliCommand(options),
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
