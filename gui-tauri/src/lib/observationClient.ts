import { invoke } from "@tauri-apps/api/core";
import { listen, type UnlistenFn } from "@tauri-apps/api/event";

export type ObservationPhase = "off" | "starting" | "observing" | "paused" | "error";

export type ObservationSnapshot = {
  phase: ObservationPhase;
  message: string;
  lastLatencyMs: number | null;
  commentsLastMinute: number;
  droppedStale: number;
  restartCount: number;
};

export type ScreenReactionEvent = {
  observationId: string;
  text: string;
  voiceLabel: string;
  voiceSpeed: number;
  eventUnixMs: number;
};

export type ObservationMetricsSnapshot = {
  sampleCount: number;
  p50Ms: number | null;
  p95Ms: number | null;
  worstMs: number | null;
  droppedStale: number;
  commentsLastMinute: number;
  framesCaptured: number;
  visionAnalyses: number;
  visionCandidates: number;
  cloudAttempts: number;
  cloudSkips: number;
  cloudFailures: number;
  reactionsEmitted: number;
  rejectedConfidence: number;
  rejectedNovelty: number;
  rejectedWorthiness: number;
  lastConfidence: number | null;
  lastNovelty: number | null;
  lastWorthiness: number | null;
  lastPreCloudAgeMs: number | null;
  lastCloudMs: number | null;
  lastPostCloudAgeMs: number | null;
};

export const startObservation = () => invoke<ObservationSnapshot>("observation_start");
export const stopObservation = () => invoke<ObservationSnapshot>("observation_stop");
export const getObservationStatus = () => invoke<ObservationSnapshot>("observation_status");
export const getObservationMetrics = () => invoke<ObservationMetricsSnapshot>("observation_metrics");
export const reportVoiceStarted = (observationId: string, eventAgeMs: number) =>
  invoke<void>("observation_voice_started", { observationId, eventAgeMs });
export const reportVoiceFinished = (observationId: string, played: boolean) =>
  invoke<void>("observation_voice_finished", { observationId, played });

export const onObservationState = (
  handler: (snapshot: ObservationSnapshot) => void,
): Promise<UnlistenFn> =>
  listen<ObservationSnapshot>("ru://observation-state", (event) => handler(event.payload));

export const onScreenReaction = (
  handler: (reaction: ScreenReactionEvent) => void,
): Promise<UnlistenFn> =>
  listen<ScreenReactionEvent>("ru://screen-reaction", (event) => handler(event.payload));
