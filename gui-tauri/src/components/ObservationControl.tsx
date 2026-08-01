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
};

const eventAge = (eventUnixMs: number) => Math.max(0, Date.now() - eventUnixMs);

export default function ObservationControl() {
  const [snapshot, setSnapshot] = useState<ObservationSnapshot>(OFF);
  const [metrics, setMetrics] = useState<ObservationMetricsSnapshot>(EMPTY_METRICS);
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
      let playingReported = false;
      const played = await speakText(reaction.text, {
        voiceLabel: "Ru (OmniVoice)",
        speed: reaction.voiceSpeed,
        interrupt: false,
        onPlaying: () => {
          if (playingReported) return;
          playingReported = true;
          void reportVoiceStarted(reaction.observationId, eventAge(reaction.eventUnixMs)).catch(
            () => undefined,
          );
        },
      });
      await reportVoiceFinished(reaction.observationId, played).catch(() => undefined);
      await refreshMetrics();
    };

    void getObservationStatus()
      .then((next) => {
        if (!disposed) setSnapshot(next);
      })
      .catch(() => undefined);
    void refreshMetrics();
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
      unlisteners.forEach((unlisten) => unlisten());
    };
  }, []);

  const observing = snapshot.phase !== "off";
  const title = useMemo(
    () =>
      `p50 ${metrics.p50Ms ?? "-"} ms · p95 ${metrics.p95Ms ?? "-"} ms · worst ${metrics.worstMs ?? "-"} ms`,
    [metrics],
  );

  const toggle = async () => {
    if (pending) return;
    setPending(true);
    try {
      const next = observing ? await stopObservation() : await startObservation();
      setSnapshot(next);
    } catch {
      setSnapshot((current) => ({
        ...current,
        phase: "error",
        message: "Observation command failed.",
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
        {snapshot.message}
      </span>
    </div>
  );
}
