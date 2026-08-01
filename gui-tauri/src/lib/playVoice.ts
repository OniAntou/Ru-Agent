import { apiFetch } from "./backendClient";

let currentAudio: HTMLAudioElement | null = null;
let currentUrl: string | null = null;
let currentFinish: ((played: boolean) => void) | null = null;
let currentAbort: AbortController | null = null;

export function stopVoice(): void {
  currentAbort?.abort();
  currentAbort = null;
  currentAudio?.pause();
  currentFinish?.(false);
}

export function ttsEnabled(): boolean {
  return localStorage.getItem("ru_tts_enabled") === "1";
}

export async function speakText(
  text: string,
  options?: { voiceLabel?: string; speed?: number }
): Promise<boolean> {
  stopVoice();
  const abort = new AbortController();
  currentAbort = abort;
  try {
    const voiceLabel = options?.voiceLabel ?? (localStorage.getItem("ru_voice_label") || undefined);
    const speed = options?.speed ?? (Number(localStorage.getItem("ru_voice_speed") ?? "1") || 1.0);
    const response = await apiFetch("/voice/tts", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ text, voice_label: voiceLabel, speed }),
      signal: abort.signal,
    });
    if (!response.ok) return false;
    const contentType = response.headers.get("content-type") || "";
    if (contentType.includes("application/json")) {
      const data = await response.json();
      if (data?.status === "error") {
        console.error("TTS error:", data.message);
        return false;
      }
      return false;
    }
    const blob = await response.blob();
    if (blob.size === 0) return false;
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
      audio.onended = () => finish(true);
      audio.onerror = () => finish(false);
      audio.play().catch(() => finish(false));
    });
  } catch (error) {
    if (!abort.signal.aborted) console.error("Failed to play voice", error);
    if (currentAbort === abort) currentAbort = null;
    return false;
  }
}
