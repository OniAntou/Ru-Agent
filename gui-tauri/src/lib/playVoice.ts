import { apiFetch } from "./backendClient";

let currentAudio: HTMLAudioElement | null = null;
let currentUrl: string | null = null;
let currentFinish: ((played: boolean) => void) | null = null;
let currentAbort: AbortController | null = null;

declare global {
  interface Window {
    __voiceLog?: string[];
  }
}

function vlog(message: string): void {
  const log = (window.__voiceLog ??= []);
  log.push(`${new Date().toISOString().slice(17)} ${message}`);
  if (log.length > 200) log.splice(0, log.length - 200);
}

export function stopVoice(): void {
  vlog(`stopVoice called (abort=${currentAbort !== null}, audio=${currentAudio !== null})`);
  currentAbort?.abort();
  currentAbort = null;
  currentAudio?.pause();
  currentFinish?.(false);
}

export function ttsEnabled(): boolean {
  return localStorage.getItem("ru_tts_enabled") === "1";
}

export type SpeakOptions = {
  voiceLabel?: string;
  speed?: number;
  interrupt?: boolean;
  onPlaying?: () => void;
};

export async function speakText(
  text: string,
  options: SpeakOptions = {}
): Promise<boolean> {
  const busy = currentAbort !== null || currentAudio !== null;
  vlog(`speakText start text=${text.slice(0, 20)}... interrupt=${options.interrupt ?? "default"} busy=${busy}`);
  if (busy && options.interrupt === false) {
    vlog("rejected: busy and interrupt=false");
    return false;
  }
  if (options.interrupt !== false) stopVoice();
  const abort = new AbortController();
  currentAbort = abort;
  try {
    const voiceLabel = options.voiceLabel ?? (localStorage.getItem("ru_voice_label") || undefined);
    const speed = options.speed ?? (Number(localStorage.getItem("ru_voice_speed") ?? "1") || 1.0);
    const response = await apiFetch("/voice/tts", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ text, voice_label: voiceLabel, speed }),
      signal: abort.signal,
    });
    if (!response.ok) {
      vlog(`TTS http ${response.status}`);
      if (currentAbort === abort) currentAbort = null;
      return false;
    }
    const contentType = response.headers.get("content-type") || "";
    if (contentType.includes("application/json")) {
      const data = await response.json();
      if (data?.status === "error") {
        console.error("TTS error:", data.message);
        if (currentAbort === abort) currentAbort = null;
        return false;
      }
      if (currentAbort === abort) currentAbort = null;
      return false;
    }
    const blob = await response.blob();
    if (blob.size === 0) {
      if (currentAbort === abort) currentAbort = null;
      return false;
    }
    const url = URL.createObjectURL(blob);
    const audio = new Audio(url);
    currentAudio = audio;
    currentUrl = url;
    void apiFetch("/vts/speaking", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ speaking: true }),
    }).catch(() => undefined);
    return await new Promise<boolean>((resolve) => {
      let settled = false;
      const finish = (played: boolean) => {
        if (settled) return;
        settled = true;
        if (currentAudio === audio) currentAudio = null;
        if (currentUrl === url) currentUrl = null;
        if (currentFinish === finish) currentFinish = null;
        if (currentAbort === abort) currentAbort = null;
        URL.revokeObjectURL(url);
        void apiFetch("/vts/speaking", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ speaking: false }),
        }).catch(() => undefined);
        resolve(played);
      };
      currentFinish = finish;
      let playingReported = false;
      audio.onplaying = () => {
        if (playingReported) return;
        playingReported = true;
        vlog("audio playing");
        options.onPlaying?.();
      };
      audio.onended = () => {
        const currentTime = Number.isFinite(audio.currentTime) ? audio.currentTime.toFixed(2) : "?";
        const duration = Number.isFinite(audio.duration) ? audio.duration.toFixed(2) : "?";
        vlog(`audio ended: currentTime=${currentTime}s duration=${duration}s`);
        finish(true);
      };
      audio.onerror = () => {
        vlog(`audio error: ${audio.error?.code ?? "unknown"} ${audio.error?.message ?? ""}`);
        finish(false);
      };
      audio.play().catch((error) => {
        vlog(`audio.play rejected: ${error}`);
        finish(false);
      });
    });
  } catch (error) {
    vlog(`speakText exception: ${error}`);
    if (!abort.signal.aborted) console.error("Failed to play voice", error);
    if (currentAbort === abort) currentAbort = null;
    return false;
  }
}
