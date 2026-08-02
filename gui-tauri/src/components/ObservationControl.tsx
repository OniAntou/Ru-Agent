import { Eye, EyeOff } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import {
  getObservationMetrics,
  getObservationStatus,
  onObservationState,
  onScreenReaction,
  reportVoiceFinished,
  reportVoiceStarted,
  startObservation,
  stopObservation,
  type ObservationMetricsSnapshot,
  type ObservationPhase,
  type ObservationSnapshot,
  type ScreenReactionEvent,
} from "../lib/observationClient";
import { speakText } from "../lib/playVoice";

const LABELS: Record<ObservationPhase, string> = {
  off: "Ru không quan sát",
  starting: "Ru đang mở mắt...",
  observing: "Ru đang nhìn màn hình chính",
  paused: "Observation tạm dừng",
  error: "Observation gặp lỗi",
};

const VISION_ENGINE_LABELS: Record<string, string> = {
  local: "Local Qwen",
  gemini: "Gemini Live",
};

const getVisionEngineLabel = (engine: string): string => {
  return VISION_ENGINE_LABELS[engine] ?? engine;
};

const OFF: ObservationSnapshot = {
  phase: "off",
  message: "Screen observation is off.",
  lastLatencyMs: null,
  commentsLastMinute: 0,
  droppedStale: 0,
  restartCount: 0,
};

const EMPTY_METRICS: ObservationMetricsSnapshot = {
  sampleCount: 0,
  p50Ms: null,
  p95Ms: null,
  worstMs: null,
  droppedStale: 0,
  commentsLastMinute: 0,
  framesCaptured: 0,
  visionAnalyses: 0,
  visionCandidates: 0,
  cloudAttempts: 0,
  cloudSkips: 0,
  cloudFailures: 0,
  reactionsEmitted: 0,
  rejectedConfidence: 0,
  rejectedNovelty: 0,
  rejectedWorthiness: 0,
  lastConfidence: null,
  lastNovelty: null,
  lastWorthiness: null,
  lastPreCloudAgeMs: null,
  lastCloudMs: null,
  lastPostCloudAgeMs: null,
  visionEngine: "local",
};

const eventAge = (eventUnixMs: number) => Math.max(0, Date.now() - eventUnixMs);
const wordCount = (text: string) => text.trim().split(/\s+/u).filter(Boolean).length;
const score = (value: number | null) => (value === null ? "-" : value.toFixed(2));
const milliseconds = (value: number | null) => (value === null ? "-" : String(value));

const commandErrorMessage = (error: unknown) => {
  if (typeof error === "string" && error.trim()) return error;
  if (error instanceof Error && error.message.trim()) return error.message;
  return "Observation command failed.";
};

export default function ObservationControl() {
  const [snapshot, setSnapshot] = useState<ObservationSnapshot>(OFF);
  const [metrics, setMetrics] = useState<ObservationMetricsSnapshot>(EMPTY_METRICS);
  const [playbackStatus, setPlaybackStatus] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  useEffect(() => {
    let disposed = false;
    const unlisteners: Array<() => void> = [];

    const refreshMetrics = async () => {
      try {
        const next = await getObservationMetrics();
        if (!disposed) setMetrics(next);
      } catch {
        // Status remains usable when metrics are temporarily unavailable.
      }
    };

    const playReaction = async (reaction: ScreenReactionEvent) => {
      const words = wordCount(reaction.text);
      let playingReported = false;
      if (!disposed)
        setPlaybackStatus(`Đang chuẩn bị phản ứng ${words} từ bằng ${reaction.voiceLabel}.`);
      const played = await speakText(reaction.text, {
        voiceLabel: reaction.voiceLabel || undefined,
        speed: reaction.voiceSpeed,
        interrupt: false,
        onPlaying: () => {
          if (playingReported) return;
          playingReported = true;
          if (!disposed) setPlaybackStatus(`Ru đang nói phản ứng ${words} từ.`);
          void reportVoiceStarted(reaction.observationId, eventAge(reaction.eventUnixMs)).catch(
            () => undefined,
          );
        },
      });
      await reportVoiceFinished(reaction.observationId, played).catch(() => undefined);
      if (!disposed) {
        setPlaybackStatus(
          played
            ? `Ru đã nói xong ${words} từ.`
            : `Phản ứng ${words} từ không phát được hoặc bị gián đoạn.`,
        );
      }
      await refreshMetrics();
    };

    void getObservationStatus()
      .then((next) => {
        if (!disposed) setSnapshot(next);
      })
      .catch(() => undefined);
    void refreshMetrics();
    const metricsTimer = window.setInterval(() => void refreshMetrics(), 2_000);
    void onObservationState((next) => {
      if (!disposed) setSnapshot(next);
    }).then((unlisten) => {
      if (disposed) unlisten();
      else unlisteners.push(unlisten);
    });
    void onScreenReaction((reaction) => {
      if (!disposed) void playReaction(reaction);
    }).then((unlisten) => {
      if (disposed) unlisten();
      else unlisteners.push(unlisten);
    });

    return () => {
      disposed = true;
      window.clearInterval(metricsTimer);
      unlisteners.forEach((unlisten) => unlisten());
    };
  }, []);

  const observing = ["starting", "observing", "paused"].includes(snapshot.phase);
  const title = useMemo(
    () =>
      `p50 ${metrics.p50Ms ?? "-"} ms · p95 ${metrics.p95Ms ?? "-"} ms · worst ${metrics.worstMs ?? "-"} ms`,
    [metrics],
  );
const observationDetail =
    playbackStatus ??
    (snapshot.phase === "observing"
      ? `Đang nhìn · frame ${metrics.framesCaptured} · vision ${getVisionEngineLabel(metrics.visionEngine)}/${metrics.visionAnalyses} · ứng viên ${metrics.visionCandidates} · thấp c/n/w ${metrics.rejectedConfidence}/${metrics.rejectedNovelty}/${metrics.rejectedWorthiness} · điểm cuối ${score(metrics.lastConfidence)}/${score(metrics.lastNovelty)}/${score(metrics.lastWorthiness)} · cloud a/s/f ${metrics.cloudAttempts}/${metrics.cloudSkips}/${metrics.cloudFailures} · ms trước/cloud/sau ${milliseconds(metrics.lastPreCloudAgeMs)}/${milliseconds(metrics.lastCloudMs)}/${milliseconds(metrics.lastPostCloudAgeMs)} · stale ${metrics.droppedStale} · đã phát ${metrics.reactionsEmitted}`
      : snapshot.message);

  const toggle = async () => {
    if (pending) return;
    setPlaybackStatus(null);
    setPending(true);
    try {
      const next = observing ? await stopObservation() : await startObservation();
      setSnapshot(next);
    } catch (error) {
      setSnapshot((current) => ({
        ...current,
        phase: "error",
        message: commandErrorMessage(error),
      }));
    } finally {
      setPending(false);
    }
  };

return (
    <div
      className={`observation-control observation-${snapshot.phase}`}
      title={title}
      data-latency-samples={metrics.sampleCount}
      data-latency-p50={metrics.p50Ms ?? ""}
      data-latency-p95={metrics.p95Ms ?? ""}
      data-latency-worst={metrics.worstMs ?? ""}
      data-dropped-stale={metrics.droppedStale}
      data-comments-last-minute={metrics.commentsLastMinute}
      data-frames-captured={metrics.framesCaptured}
      data-vision-analyses={metrics.visionAnalyses}
      data-vision-candidates={metrics.visionCandidates}
      data-cloud-attempts={metrics.cloudAttempts}
      data-cloud-skips={metrics.cloudSkips}
      data-cloud-failures={metrics.cloudFailures}
      data-reactions-emitted={metrics.reactionsEmitted}
      data-rejected-confidence={metrics.rejectedConfidence}
      data-rejected-novelty={metrics.rejectedNovelty}
      data-rejected-worthiness={metrics.rejectedWorthiness}
      data-last-confidence={metrics.lastConfidence ?? ""}
      data-last-novelty={metrics.lastNovelty ?? ""}
      data-last-worthiness={metrics.lastWorthiness ?? ""}
      data-last-pre-cloud-age-ms={metrics.lastPreCloudAgeMs ?? ""}
      data-last-cloud-ms={metrics.lastCloudMs ?? ""}
      data-last-post-cloud-age-ms={metrics.lastPostCloudAgeMs ?? ""}
      data-vision-engine={metrics.visionEngine}
    >
      <button
        type="button"
        className="observation-toggle"
        aria-pressed={observing}
        aria-label={observing ? "Tắt quan sát màn hình" : "Bật quan sát màn hình chính"}
        disabled={pending || snapshot.phase === "starting"}
        onClick={toggle}
      >
        {observing ? <Eye size={15} aria-hidden="true" /> : <EyeOff size={15} aria-hidden="true" />}
        <span>{LABELS[snapshot.phase]}</span>
      </button>
      <span className="observation-detail" aria-live="polite">
        {observationDetail}
      </span>
    </div>
  );
}
