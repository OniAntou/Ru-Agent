import { beforeEach, describe, expect, it, vi } from "vitest";

const apiFetch = vi.fn();
vi.mock("./backendClient", () => ({ apiFetch }));

describe("voice playback lifecycle", () => {
  beforeEach(() => {
    vi.resetModules();
    apiFetch.mockReset();
    apiFetch.mockResolvedValue(new Response(new Blob(["wave"], { type: "audio/wav" })));
  });

  it("settles cancelled playback and revokes its object URL", async () => {
    let activeAudio: { pause: ReturnType<typeof vi.fn> } | null = null;
    class AudioMock {
      onended: (() => void) | null = null;
      onerror: (() => void) | null = null;
      pause = vi.fn();
      play = vi.fn().mockResolvedValue(undefined);
      constructor() {
        activeAudio = this;
      }
    }
    vi.stubGlobal("Audio", AudioMock);
    vi.spyOn(URL, "createObjectURL").mockReturnValue("blob:voice");
    const revoke = vi.spyOn(URL, "revokeObjectURL").mockImplementation(() => {});
    const { speakText, stopVoice } = await import("./playVoice");

    const speaking = speakText("xin chao");
    while (activeAudio === null) await Promise.resolve();
    stopVoice();

    await expect(speaking).resolves.toBe(false);
    expect((activeAudio as { pause: ReturnType<typeof vi.fn> }).pause).toHaveBeenCalledOnce();
    expect(revoke).toHaveBeenCalledWith("blob:voice");
  }, 500);
});
