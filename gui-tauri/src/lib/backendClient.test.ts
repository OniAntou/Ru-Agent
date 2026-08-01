import { beforeEach, describe, expect, it, vi } from "vitest";

const invoke = vi.fn();
vi.mock("@tauri-apps/api/core", () => ({ invoke }));

describe("authenticated backend client", () => {
  beforeEach(() => {
    vi.resetModules();
    invoke.mockReset();
    invoke.mockResolvedValue({ baseUrl: "http://127.0.0.1:8000", token: "run secret" });
  });

  it("adds the per-run bearer token to REST requests", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response("{}"));
    vi.stubGlobal("fetch", fetchMock);
    const { apiFetch } = await import("./backendClient");

    await apiFetch("/settings", { headers: { "Content-Type": "application/json" } });

    expect(fetchMock).toHaveBeenCalledWith(
      "http://127.0.0.1:8000/settings",
      expect.objectContaining({
        headers: expect.objectContaining({ Authorization: "Bearer run secret" }),
      }),
    );
  });

  it("encodes the per-run token in the websocket URL", async () => {
    const urls: string[] = [];
    class WebSocketMock {
      constructor(url: string) {
        urls.push(url);
      }
    }
    vi.stubGlobal("WebSocket", WebSocketMock);
    const { createChatSocket } = await import("./backendClient");

    await createChatSocket();
    expect(urls).toEqual(["ws://127.0.0.1:8000/chat?token=run+secret"]);
  });
});
