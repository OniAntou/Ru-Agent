import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("./backendClient", () => ({ apiFetch: vi.fn() }));

describe("microphone lifecycle", () => {
  beforeEach(() => vi.resetModules());

  it("cancels recording without transcribing and releases the microphone", async () => {
    const stop = vi.fn();
    vi.stubGlobal("navigator", {
      mediaDevices: { getUserMedia: vi.fn().mockResolvedValue({ getTracks: () => [{ stop }] }) },
    });
    class AudioContextMock {
      sampleRate = 48_000;
      destination = {};
      close = vi.fn().mockResolvedValue(undefined);
      createMediaStreamSource() {
        return { connect: vi.fn(), disconnect: vi.fn() };
      }
      createScriptProcessor() {
        return { connect: vi.fn(), disconnect: vi.fn(), onaudioprocess: null };
      }
    }
    vi.stubGlobal("AudioContext", AudioContextMock);
    const { cancelRecording, isRecording, startRecording } = await import("./voiceInput");

    expect(await startRecording()).toBe(true);
    expect(isRecording()).toBe(true);
    await cancelRecording();

    expect(stop).toHaveBeenCalledOnce();
    expect(isRecording()).toBe(false);
  });
});
