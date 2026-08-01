let currentAudio: HTMLAudioElement | null = null;

export function stopVoice(): void {
  if (currentAudio) {
    currentAudio.pause();
    currentAudio = null;
  }
}

export function ttsEnabled(): boolean {
  return localStorage.getItem("ru_tts_enabled") === "1";
}

export async function speakText(
  text: string,
  options?: { voiceLabel?: string; speed?: number }
): Promise<boolean> {
  stopVoice();
  try {
    const voiceLabel = options?.voiceLabel ?? (localStorage.getItem("ru_voice_label") || undefined);
    const speed = options?.speed ?? (Number(localStorage.getItem("ru_voice_speed") ?? "1") || 1.0);
    const response = await fetch("http://127.0.0.1:8000/voice/tts", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ text, voice_label: voiceLabel, speed }),
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
    await new Promise<void>((resolve) => {
      audio.onended = () => resolve();
      audio.onerror = () => resolve();
      audio.play().catch(() => resolve());
    });
    URL.revokeObjectURL(url);
    return true;
  } catch (error) {
    console.error("Failed to play voice", error);
    return false;
  }
}
