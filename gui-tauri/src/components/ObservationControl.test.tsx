import { act, render, screen } from "@testing-library/react";
import ObservationControl from "./ObservationControl";
import { vi, describe, it, beforeEach, afterEach, expect } from "vitest";

vi.mock("../lib/observationClient", () => ({
  getObservationStatus: vi.fn().mockResolvedValue({
    phase: "observing",
    message: "Ru đang nhìn màn hình chính",
    lastLatencyMs: null,
    commentsLastMinute: 0,
    droppedStale: 1,
    restartCount: 0,
  }),
  getObservationMetrics: vi.fn().mockResolvedValue({
    sampleCount: 40,
    p50Ms: 900,
    p95Ms: 1800,
    worstMs: 2700,
    droppedStale: 1,
    commentsLastMinute: 0,
    framesCaptured: 40,
    visionAnalyses: 12,
    visionCandidates: 4,
    cloudAttempts: 2,
    cloudSkips: 1,
    cloudFailures: 0,
    reactionsEmitted: 1,
    rejectedConfidence: 0,
    rejectedNovelty: 8,
    rejectedWorthiness: 10,
    lastConfidence: 0.91,
    lastNovelty: 0.42,
    lastWorthiness: 0.38,
    lastPreCloudAgeMs: 900,
    lastCloudMs: 1800,
    lastPostCloudAgeMs: 2700,
    visionEngine: "local",
  }),
  onObservationState: vi.fn().mockResolvedValue(() => {}),
  onScreenReaction: vi.fn().mockResolvedValue(() => {}),
  startObservation: vi.fn(),
  stopObservation: vi.fn(),
  reportVoiceStarted: vi.fn(),
  reportVoiceFinished: vi.fn(),
}));

vi.mock("../lib/playVoice", () => ({
  speakText: vi.fn().mockResolvedValue(true),
}));

describe("ObservationControl", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.clearAllMocks();
  });

  it("renders observation detail with vision engine and analyses", async () => {
    await act(async () => {
      render(<ObservationControl />);
      await vi.runAllTimersAsync();
    });

    const detail = screen.getByText(
      "Đang nhìn · frame 40 · vision Local Qwen/12 · ứng viên 4 · thấp c/n/w 0/8/10 · điểm cuối 0.91/0.42/0.38 · cloud a/s/f 2/1/0 · ms trước/cloud/sau 900/1800/2700 · stale 1 · đã phát 1"
    );
    expect(detail).toBeInTheDocument();
  });

  it("has data-vision-engine attribute on root element", async () => {
    await act(async () => {
      render(<ObservationControl />);
      await vi.runAllTimersAsync();
    });

    const root = screen.getByText("Ru đang nhìn màn hình chính").closest("div");
    expect(root).toHaveAttribute("data-vision-engine", "local");
  });
});