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

  it("does not interrupt existing speech for a proactive reaction", async () => {
    let activeAudio: AudioMock | null = null;
    class AudioMock {
      onended: (() => void) | null = null;
      onerror: (() => void) | null = null;
      onplaying: (() => void) | null = null;
      pause = vi.fn();
      play = vi.fn().mockResolvedValue(undefined);
      constructor() {
        activeAudio = this;
      }
    }
    vi.stubGlobal("Audio", AudioMock);
    vi.spyOn(URL, "createObjectURL").mockReturnValue("blob:voice");
    vi.spyOn(URL, "revokeObjectURL").mockImplementation(() => {});
    const { speakText } = await import("./playVoice");

    const first = speakText("first");
    while (activeAudio === null) await Promise.resolve();
    await expect(speakText("screen event", { interrupt: false })).resolves.toBe(false);
    expect((activeAudio as AudioMock).pause).not.toHaveBeenCalled();
    (activeAudio as AudioMock).onended?.();
    await expect(first).resolves.toBe(true);
  });

  it("reports the first playing event once", async () => {
    let activeAudio: AudioMock | null = null;
    class AudioMock {
      onended: (() => void) | null = null;
      onerror: (() => void) | null = null;
      onplaying: (() => void) | null = null;
      pause = vi.fn();
      play = vi.fn().mockResolvedValue(undefined);
      constructor() {
        activeAudio = this;
      }
    }
    vi.stubGlobal("Audio", AudioMock);
    vi.spyOn(URL, "createObjectURL").mockReturnValue("blob:voice");
    vi.spyOn(URL, "revokeObjectURL").mockImplementation(() => {});
    const onPlaying = vi.fn();
    const { speakText } = await import("./playVoice");

    const result = speakText("hello", { onPlaying });
    while (activeAudio === null) await Promise.resolve();
    (activeAudio as AudioMock).onplaying?.();
    (activeAudio as AudioMock).onplaying?.();
    expect(onPlaying).toHaveBeenCalledTimes(1);
    (activeAudio as AudioMock).onended?.();
    await expect(result).resolves.toBe(true);
  });
});
