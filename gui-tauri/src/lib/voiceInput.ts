import { apiFetch } from "./backendClient";

let mediaStream: MediaStream | null = null;
let audioContext: AudioContext | null = null;
let sourceNode: MediaStreamAudioSourceNode | null = null;
let processor: ScriptProcessorNode | null = null;
let chunks: Float32Array[] = [];
let sampleRate = 16000;
let capturedSamples = 0;
let recordingTimer: ReturnType<typeof setTimeout> | null = null;
export const MAX_RECORDING_SECONDS = 60;

async function releaseCapture(): Promise<void> {
  if (recordingTimer) {
    clearTimeout(recordingTimer);
    recordingTimer = null;
  }
  if (processor) {
    processor.disconnect();
    processor.onaudioprocess = null;
    processor = null;
  }
  if (sourceNode) {
    sourceNode.disconnect();
    sourceNode = null;
  }
  if (audioContext) {
    await audioContext.close().catch(() => {});
    audioContext = null;
  }
  if (mediaStream) {
    mediaStream.getTracks().forEach((track) => track.stop());
    mediaStream = null;
  }
}

export async function cancelRecording(): Promise<void> {
  await releaseCapture();
  chunks = [];
  capturedSamples = 0;
}

function encodeWav(samples: Float32Array, rate: number): ArrayBuffer {
  const buffer = new ArrayBuffer(44 + samples.length * 2);
  const view = new DataView(buffer);
  const writeStr = (offset: number, str: string) => {
    for (let i = 0; i < str.length; i++) {
      view.setUint8(offset + i, str.charCodeAt(i));
    }
  };
  writeStr(0, "RIFF");
  view.setUint32(4, 36 + samples.length * 2, true);
  writeStr(8, "WAVE");
  writeStr(12, "fmt ");
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, 1, true);
  view.setUint32(24, rate, true);
  view.setUint32(28, rate * 2, true);
  view.setUint16(32, 2, true);
  view.setUint16(34, 16, true);
  writeStr(36, "data");
  view.setUint32(40, samples.length * 2, true);
  let offset = 44;
  for (let i = 0; i < samples.length; i++, offset += 2) {
    const s = Math.max(-1, Math.min(1, samples[i]));
    view.setInt16(offset, s < 0 ? s * 0x8000 : s * 0x7fff, true);
  }
  return buffer;
}

export async function startRecording(): Promise<boolean> {
  if (mediaStream) return true;
  try {
    mediaStream = await navigator.mediaDevices.getUserMedia({ audio: true });
    audioContext = new AudioContext();
    sampleRate = audioContext.sampleRate;
    sourceNode = audioContext.createMediaStreamSource(mediaStream);
    processor = audioContext.createScriptProcessor(4096, 1, 1);
    chunks = [];
    capturedSamples = 0;
    processor.onaudioprocess = (event) => {
      const input = event.inputBuffer.getChannelData(0);
      const remaining = Math.max(0, sampleRate * MAX_RECORDING_SECONDS - capturedSamples);
      if (remaining > 0) {
        const captured = new Float32Array(input.subarray(0, remaining));
        chunks.push(captured);
        capturedSamples += captured.length;
      }
    };
    sourceNode.connect(processor);
    processor.connect(audioContext.destination);
    recordingTimer = setTimeout(() => void releaseCapture(), MAX_RECORDING_SECONDS * 1000);
    return true;
  } catch (error) {
    console.error("Mic start failed", error);
    await cancelRecording();
    return false;
  }
}

export function isRecording(): boolean {
  return mediaStream !== null;
}

export async function stopRecording(): Promise<{ text: string; error?: string }> {
  await releaseCapture();
  if (chunks.length === 0) {
    return { text: "", error: "Không bắt được âm thanh nào." };
  }

  const total = chunks.reduce((sum, chunk) => sum + chunk.length, 0);
  const merged = new Float32Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    merged.set(chunk, offset);
    offset += chunk.length;
  }
  chunks = [];
  capturedSamples = 0;

  let pcm = merged;
  if (sampleRate !== 16000 && sampleRate > 0) {
    const ratio = sampleRate / 16000;
    const outLength = Math.round(merged.length / ratio);
    const downsampled = new Float32Array(outLength);
    for (let i = 0; i < outLength; i++) {
      downsampled[i] = merged[Math.min(merged.length - 1, Math.round(i * ratio))];
    }
    pcm = downsampled;
  }

  const wav = encodeWav(pcm, 16000);
  const bytes = new Uint8Array(wav);
  let binary = "";
  const chunkSize = 0x8000;
  for (let i = 0; i < bytes.length; i += chunkSize) {
    binary += String.fromCharCode(...bytes.subarray(i, i + chunkSize));
  }
  const audioB64 = btoa(binary);

  try {
    const response = await apiFetch("/voice/transcribe", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ audio_b64: audioB64, sample_rate: 16000 }),
    });
    const data = await response.json();
    if (data?.status === "ok") {
      return { text: (data.text || "").trim() };
    }
    return { text: "", error: data?.message || "Không nhận dạng được giọng nói." };
  } catch (error) {
    console.error("Transcribe failed", error);
    return { text: "", error: "Không kết nối được backend." };
  }
}
