import { useEffect, useState } from "react";
import { speakText, stopVoice, ttsEnabled } from "../lib/playVoice";

const VOICE_OPTIONS = ["Ru (OmniVoice)", "Ái Vy (OmniVoice)"];

const VTS_FACIAL = [
  { code: "joy", label: "Vui (joy)" },
  { code: "surprised", label: "Ngạc nhiên (surprised)" },
  { code: "gentle", label: "Dịu dàng (gentle)" },
];

const VTS_BODY = [
  { code: "raise_hand", label: "Giơ tay (raise_hand)" },
  { code: "tail_up", label: "Đuôi dựng (tail_up)" },
];

export default function VoiceTab() {
  const [ttsOn, setTtsOn] = useState(ttsEnabled());
  const [voiceLabel, setVoiceLabel] = useState("Ru (OmniVoice)");
  const [voiceSpeed, setVoiceSpeed] = useState(1.0);
  const [available, setAvailable] = useState<boolean | null>(null);
  const [status, setStatus] = useState("");
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);
  const [testText, setTestText] = useState("Xin chào anh, em là Ru đây!");
  const [vtsConnected, setVtsConnected] = useState(false);
  const [vtsBusy, setVtsBusy] = useState(false);

  useEffect(() => {
    fetch("http://127.0.0.1:8000/voice/status")
      .then((res) => res.json())
      .then((data) => setAvailable(data.available ?? false))
      .catch(() => setAvailable(false));
    fetch("http://127.0.0.1:8000/vts/status")
      .then((res) => res.json())
      .then((data) => setVtsConnected(data.connected ?? false))
      .catch(() => setVtsConnected(false));
    fetch("http://127.0.0.1:8000/settings")
      .then((res) => res.json())
      .then((data) => {
        if (data.status === "ok") {
          setVoiceLabel(data.voice_label || "Ru (OmniVoice)");
          setVoiceSpeed(data.voice_speed ?? 1.0);
        }
      })
      .catch(() => {});
  }, []);

  const toggleTts = (enabled: boolean) => {
    setTtsOn(enabled);
    localStorage.setItem("ru_tts_enabled", enabled ? "1" : "0");
    if (!enabled) stopVoice();
  };

  const saveVoiceSettings = async () => {
    setBusy(true);
    setStatus("");
    localStorage.setItem("ru_voice_label", voiceLabel);
    localStorage.setItem("ru_voice_speed", String(voiceSpeed));
    try {
      const response = await fetch("http://127.0.0.1:8000/settings", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ voice_label: voiceLabel, voice_speed: voiceSpeed }),
      });
      const data = await response.json();
      if (data.status === "success") {
        setSaved(true);
        setTimeout(() => setSaved(false), 2000);
      } else {
        setStatus(`Failed: ${data.message || "unknown error"}`);
      }
    } catch {
      setStatus("Failed to reach the backend. Is the server running?");
    } finally {
      setBusy(false);
    }
  };

  const testVoice = async () => {
    setBusy(true);
    setStatus("");
    localStorage.setItem("ru_voice_label", voiceLabel);
    localStorage.setItem("ru_voice_speed", String(voiceSpeed));
    const ok = await speakText(
      testText.trim() || "Xin chào anh, em là Ru đây!",
      { voiceLabel, speed: voiceSpeed }
    );
    if (!ok) setStatus("Voice server không sẵn sàng. Hãy bấm Warm Up.");
    setBusy(false);
  };

  const warmup = async () => {
    setBusy(true);
    setStatus("Đang khởi động OmniVoice...");
    try {
      const response = await fetch("http://127.0.0.1:8000/voice/warmup", { method: "POST" });
      const data = await response.json();
      if (data.status === "ok") {
        setStatus("OmniVoice đã sẵn sàng!");
        setAvailable(true);
      } else {
        setStatus(`Warmup failed: ${data.message || "unknown error"}`);
      }
    } catch {
      setStatus("Không kết nối được backend.");
    } finally {
      setBusy(false);
    }
  };

  const release = async () => {
    setBusy(true);
    try {
      const response = await fetch("http://127.0.0.1:8000/voice/release", { method: "POST" });
      const data = await response.json();
      setStatus(data.status === "ok" ? "Đã nhả GPU." : `Release failed: ${data.message || ""}`);
    } catch {
      setStatus("Không kết nối được backend.");
    } finally {
      setBusy(false);
    }
  };

  const connectVts = async () => {
    setVtsBusy(true);
    setStatus("Đang kết nối VTube Studio... Mở VTube Studio và bật plugin Ru nếu chưa bật.");
    try {
      const response = await fetch("http://127.0.0.1:8000/vts/connect", { method: "POST" });
      const data = await response.json();
      if (data.status === "ok") {
        setVtsConnected(true);
        setStatus("Đã kết nối VTube Studio!");
      } else {
        setStatus(`Không kết nối được: ${data.message || "VTube Studio chưa sẵn sàng"}`);
      }
    } catch {
      setStatus("Không kết nối được backend.");
    } finally {
      setVtsBusy(false);
    }
  };

  const vtsAction = async (facial: string | null, body: string | null) => {
    setVtsBusy(true);
    setStatus("");
    try {
      const response = await fetch("http://127.0.0.1:8000/vts/action", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ facial, body }),
      });
      const data = await response.json();
      if (data.status !== "ok") {
        setStatus(`Action failed: ${data.message || "unknown error"}`);
      }
    } catch {
      setStatus("Không kết nối được backend.");
    } finally {
      setVtsBusy(false);
    }
  };

  return (
    <div className="tab-container">
      <div className="tab-content-scrollable">
        <h1 className="tab-title">Voice & Avatar</h1>

        <div className="form-group">
          <label style={{ display: "flex", alignItems: "center", gap: 8, cursor: "pointer" }}>
            <input
              type="checkbox"
              style={{ width: 16, height: 16 }}
              checked={ttsOn}
              onChange={(e) => toggleTts(e.target.checked)}
            />
            Đọc reply tự động bằng giọng nói (TTS)
          </label>
        </div>

        <div className="form-group">
          <label>Giọng nói</label>
          <select value={voiceLabel} onChange={(e) => setVoiceLabel(e.target.value)}>
            {VOICE_OPTIONS.map((label) => (
              <option key={label} value={label}>
                {label}
              </option>
            ))}
          </select>
        </div>

        <div className="form-group">
          <label>Tốc độ giọng: {voiceSpeed.toFixed(2)}</label>
          <input
            type="range"
            min="0.75"
            max="1.2"
            step="0.05"
            value={voiceSpeed}
            onChange={(e) => setVoiceSpeed(Number(e.target.value))}
          />
        </div>

        <div className="form-group">
          <label>Text thử giọng</label>
          <input
            type="text"
            value={testText}
            onChange={(e) => setTestText(e.target.value)}
            placeholder="Xin chào anh, em là Ru đây!"
          />
        </div>

        <div style={{ display: "flex", gap: 10, marginTop: 20, flexWrap: "wrap" }}>
          <button className="btn-primary" onClick={testVoice} disabled={busy}>
            Test Voice
          </button>
          <button className="btn-primary" onClick={warmup} disabled={busy}>
            Warm Up
          </button>
          <button className="btn-primary" onClick={release} disabled={busy}>
            Release GPU
          </button>
          <button className="btn-primary" onClick={saveVoiceSettings} disabled={busy}>
            Save Voice Settings
          </button>
        </div>

        {saved && <p style={{ color: "var(--text-secondary)", marginTop: 12, fontSize: 13 }}>Đã lưu.</p>}
        {status && (
          <p style={{ color: "var(--text-secondary)", marginTop: 12, fontSize: 13 }}>{status}</p>
        )}
        <p style={{ color: "var(--text-secondary)", marginTop: 12, fontSize: 13 }}>
          Voice service: {available === null ? "đang kiểm tra..." : available ? "đang chạy" : "chưa chạy (bấm Warm Up để khởi động)"}
        </p>

        <hr style={{ margin: "24px 0", border: "none", borderTop: "1px solid var(--border, #2a2a3a)" }} />

        <h2 className="tab-title" style={{ fontSize: 16 }}>
          VTube Studio (Avatar)
        </h2>
        <p style={{ color: "var(--text-secondary)", fontSize: 13 }}>
          Trạng thái:{" "}
          {vtsConnected ? (
            <span style={{ color: "#4caf50" }}>Đã kết nối</span>
          ) : (
            <span>Chưa kết nối</span>
          )}
        </p>
        <p style={{ color: "var(--text-secondary)", fontSize: 12, marginTop: 6 }}>
          Khi đã kết nối, Ru tự chạy biểu cảm theo hội thoại. Model có thể thêm marker
          <code style={{ color: "var(--text, #e8e8f0)" }}> [[VTS:...]] </code> vào reply để kích hoạt
          hotkey từ catalog VTube Studio.
        </p>

        <div style={{ display: "flex", gap: 10, marginTop: 14, flexWrap: "wrap" }}>
          <button className="btn-primary" onClick={connectVts} disabled={vtsBusy || vtsConnected}>
            {vtsBusy ? "Đang kết nối..." : vtsConnected ? "Đã kết nối" : "Kết nối VTube Studio"}
          </button>
        </div>

        <div style={{ marginTop: 16 }}>
          <label style={{ fontSize: 13, color: "var(--text-secondary)" }}>Biểu cảm khuôn mặt</label>
          <div style={{ display: "flex", gap: 8, marginTop: 6, flexWrap: "wrap" }}>
            {VTS_FACIAL.map((item) => (
              <button
                key={item.code}
                className="btn-primary"
                disabled={!vtsConnected || vtsBusy}
                onClick={() => vtsAction(item.code, null)}
              >
                {item.label}
              </button>
            ))}
          </div>
        </div>

        <div style={{ marginTop: 12 }}>
          <label style={{ fontSize: 13, color: "var(--text-secondary)" }}>Cử chỉ cơ thể</label>
          <div style={{ display: "flex", gap: 8, marginTop: 6, flexWrap: "wrap" }}>
            {VTS_BODY.map((item) => (
              <button
                key={item.code}
                className="btn-primary"
                disabled={!vtsConnected || vtsBusy}
                onClick={() => vtsAction(null, item.code)}
              >
                {item.label}
              </button>
            ))}
          </div>
        </div>

        <p style={{ color: "var(--text-secondary)", fontSize: 12, marginTop: 12 }}>
          Các biểu cảm tự reset sau khoảng 2 giây.
        </p>
      </div>
    </div>
  );
}
