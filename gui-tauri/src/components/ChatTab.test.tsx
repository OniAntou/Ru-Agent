import { fireEvent, render, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import ChatTab from "./ChatTab";

const { apiFetch, createChatSocket } = vi.hoisted(() => ({
  apiFetch: vi.fn(),
  createChatSocket: vi.fn(),
}));
vi.mock("../lib/backendClient", () => ({ apiFetch, createChatSocket }));
vi.mock("../lib/playVoice", () => ({ speakText: vi.fn(), ttsEnabled: () => false }));
vi.mock("../lib/voiceInput", () => ({
  startRecording: vi.fn(),
  stopRecording: vi.fn(),
  cancelRecording: vi.fn(),
}));

class FakeSocket {
  readyState = WebSocket.OPEN;
  send = vi.fn();
  close = vi.fn();
  onopen: (() => void) | null = null;
  onmessage: ((event: MessageEvent) => void) | null = null;
  onclose: (() => void) | null = null;
  onerror: (() => void) | null = null;
}

describe("ChatTab session routing", () => {
  beforeEach(() => {
    apiFetch.mockReset();
    createChatSocket.mockReset();
    apiFetch.mockImplementation(async (path: string) => {
      const id = path.split("/")[2];
      return new Response(
        JSON.stringify({ status: "ok", session: { id, title: id, messages: [] } }),
        { headers: { "Content-Type": "application/json" } },
      );
    });
  });

  it("sends to the newly selected session, never a stale completed session", async () => {
    const socket = new FakeSocket();
    createChatSocket.mockResolvedValue(socket);
    const props = {
      onSessionCreated: vi.fn(),
      onTurnCompleted: vi.fn(),
    };
    const view = render(<ChatTab activeSessionId="session-a" {...props} />);
    await waitFor(() => expect(socket.onopen).toBeTypeOf("function"));
    socket.onopen?.();

    view.rerender(<ChatTab activeSessionId="session-b" {...props} />);
    await waitFor(() => expect(apiFetch).toHaveBeenCalledWith("/sessions/session-b/activate", { method: "POST" }));
    const input = view.getByRole("textbox");
    fireEvent.change(input, { target: { value: "continue here" } });
    fireEvent.keyDown(input, { key: "Enter", shiftKey: false });

    expect(JSON.parse(socket.send.mock.calls[0][0])).toEqual({
      prompt: "continue here",
      session_id: "session-b",
    });
  });
});
