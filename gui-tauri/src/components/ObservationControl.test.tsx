import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ObservationSnapshot, ScreenReactionEvent } from "../lib/observationClient";
import ObservationControl from "./ObservationControl";

const client = vi.hoisted(() => ({
  startObservation: vi.fn(),
  stopObservation: vi.fn(),
  getObservationStatus: vi.fn(),
  getObservationMetrics: vi.fn(),
  reportVoiceStarted: vi.fn(),
  reportVoiceFinished: vi.fn(),
  onObservationState: vi.fn(),
  onScreenReaction: vi.fn(),
}));
const speakText = vi.hoisted(() => vi.fn());

vi.mock("../lib/observationClient", () => client);
vi.mock("../lib/playVoice", () => ({ speakText }));

const off = {
  phase: "off" as const,
  message: "off",
  lastLatencyMs: null,
  commentsLastMinute: 0,
  droppedStale: 0,
  restartCount: 0,
};
const metrics = {
  sampleCount: 2,
  p50Ms: 1200,
  p95Ms: 1800,
  worstMs: 2100,
  droppedStale: 1,
  commentsLastMinute: 3,
};

describe("ObservationControl", () => {
  let stateHandler: (value: ObservationSnapshot) => void;
  let reactionHandler: (value: ScreenReactionEvent) => void;
  const unlistenState = vi.fn();
  const unlistenReaction = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.setItem("ru_tts_enabled", "0");
    client.getObservationStatus.mockResolvedValue(off);
    client.getObservationMetrics.mockResolvedValue(metrics);
    client.startObservation.mockResolvedValue({ ...off, phase: "starting" });
    client.stopObservation.mockResolvedValue(off);
    client.reportVoiceStarted.mockResolvedValue(undefined);
    client.reportVoiceFinished.mockResolvedValue(undefined);
    client.onObservationState.mockImplementation(async (handler) => {
      stateHandler = handler;
      return unlistenState;
    });
    client.onScreenReaction.mockImplementation(async (handler) => {
      reactionHandler = handler;
      return unlistenReaction;
    });
    speakText.mockResolvedValue(false);
  });

  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
  });

  it("starts off, maps every phase label, and prevents duplicate clicks", async () => {
    let resolveStart!: (value: unknown) => void;
    client.startObservation.mockReturnValue(
      new Promise((resolve) => {
        resolveStart = resolve;
      }),
    );
    render(<ObservationControl />);
    const button = screen.getByRole("button", { name: "Bật quan sát màn hình chính" });
    expect(button).toHaveTextContent("Ru không quan sát");

    fireEvent.click(button);
    fireEvent.click(button);
    expect(client.startObservation).toHaveBeenCalledTimes(1);
    expect(button).toBeDisabled();
    resolveStart({ ...off, phase: "starting", message: "starting" });
    await act(async () => {});

    for (const [phase, label] of [
      ["starting", "Ru đang mở mắt..."],
      ["observing", "Ru đang nhìn màn hình chính"],
      ["paused", "Observation tạm dừng"],
      ["error", "Observation gặp lỗi"],
      ["off", "Ru không quan sát"],
    ] as const) {
      act(() => stateHandler({ ...off, phase, message: phase }));
      expect(screen.getByText(label)).toBeInTheDocument();
    }
  });

  it("always uses Ru OmniVoice and reports first-playing latency even when chat TTS is off", async () => {
    vi.spyOn(Date, "now").mockReturnValue(2_000);
    speakText.mockImplementation(async (_text, options) => {
      options.onPlaying();
      options.onPlaying();
      return true;
    });
    render(<ObservationControl />);
    await waitFor(() => expect(client.onScreenReaction).toHaveBeenCalled());

    act(() =>
      reactionHandler({
        observationId: "obs-1",
        text: "Anh hụt hơi nhiều rồi đó.",
        voiceLabel: "untrusted",
        voiceSpeed: 1,
        eventUnixMs: 1_500,
      }),
    );

    await waitFor(() =>
      expect(speakText).toHaveBeenCalledWith("Anh hụt hơi nhiều rồi đó.", {
        voiceLabel: "Ru (OmniVoice)",
        speed: 1,
        interrupt: false,
        onPlaying: expect.any(Function),
      }),
    );
    expect(client.reportVoiceStarted).toHaveBeenCalledWith("obs-1", 500);
    expect(client.reportVoiceFinished).toHaveBeenCalledWith("obs-1", true);
  });

  it("reports failed playback, refreshes metrics, and unregisters both listeners", async () => {
    const view = render(<ObservationControl />);
    await waitFor(() => expect(client.onScreenReaction).toHaveBeenCalled());
    act(() =>
      reactionHandler({
        observationId: "obs-2",
        text: "Một câu.",
        voiceLabel: "Ru (OmniVoice)",
        voiceSpeed: 1,
        eventUnixMs: Date.now(),
      }),
    );
    await waitFor(() =>
      expect(client.reportVoiceFinished).toHaveBeenCalledWith("obs-2", false),
    );
    expect(client.getObservationMetrics).toHaveBeenCalledTimes(2);
    expect(screen.getByTitle(/p50 1200 ms/)).toHaveAttribute("data-latency-p95", "1800");

    view.unmount();
    expect(unlistenState).toHaveBeenCalledOnce();
    expect(unlistenReaction).toHaveBeenCalledOnce();
  });
});
