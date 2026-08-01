import { useState, useEffect, useRef } from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { speakText, ttsEnabled } from "../lib/playVoice";
import { startRecording, stopRecording } from "../lib/voiceInput";

type Message = {
  role: "user" | "ai";
  content: string;
};

type ChatTabProps = {
  activeSessionId: string;
  onSessionCreated: (id: string) => void;
  onTurnCompleted: () => void;
};

const WS_URL = "ws://127.0.0.1:8000/chat";
const RECONNECT_DELAY_MS = 2000;

const VTS_MARKER_RE = /\s*\[\[VTS:[^\]]*\]\]/g;
const DISPLAY_MARKER_RE = /\s*\[\[(?:VTS|VOICE_PACE):[^\]]*\]\]/g;

function stripDisplayMarkers(content: string): string {
  return content.replace(DISPLAY_MARKER_RE, "").trim();
}

export default function ChatTab({ activeSessionId, onSessionCreated, onTurnCompleted }: ChatTabProps) {
  const [messages, setMessages] = useState<Message[]>([]);
  const [input, setInput] = useState("");
  const [ws, setWs] = useState<WebSocket | null>(null);
  const [connected, setConnected] = useState(false);
  const [busy, setBusy] = useState(false);
  const chatEndRef = useRef<HTMLDivElement>(null);
  const wsRef = useRef<WebSocket | null>(null);
  const inputRef = useRef<HTMLTextAreaElement>(null);
  const sessionIdRef = useRef<string>("");
  const pendingSessionRef = useRef<string>("");

  useEffect(() => {
    sessionIdRef.current = activeSessionId;
  }, [activeSessionId]);

  useEffect(() => {
    let disposed = false;
    let reconnectTimer: ReturnType<typeof setTimeout> | null = null;

    const connect = () => {
      if (disposed) return;
      const next = new WebSocket(WS_URL);
      wsRef.current = next;

      next.onopen = () => {
        if (disposed) return;
        setConnected(true);
      };

      next.onmessage = (event) => {
        if (disposed) return;
        let data: { type?: string; content?: string; session_id?: string };
        try {
          data = JSON.parse(event.data);
        } catch {
          return;
        }
        if (data.type === "session" && typeof data.session_id === "string") {
          pendingSessionRef.current = data.session_id;
          onSessionCreated(data.session_id);
        } else if (data.type === "chunk" && typeof data.content === "string") {
          setMessages((prev) => {
            const last = prev[prev.length - 1];
            if (last && last.role === "ai" && !last.content.startsWith("Error: ")) {
              return [...prev.slice(0, -1), { role: "ai", content: last.content + data.content }];
            }
            return [...prev, { role: "ai", content: data.content as string }];
          });
        } else if (data.type === "done") {
          setBusy(false);
          if (typeof data.session_id === "string") {
            pendingSessionRef.current = data.session_id;
            onSessionCreated(data.session_id);
          }
          onTurnCompleted();
          if (ttsEnabled() && typeof data.content === "string" && data.content.trim()) {
            speakText(data.content.replace(VTS_MARKER_RE, "").trim());
          }
        } else if (data.type === "error" && typeof data.content === "string") {
          setMessages((prev) => [...prev, { role: "ai", content: `Error: ${data.content}` }]);
          setBusy(false);
          if (typeof data.session_id === "string") {
            pendingSessionRef.current = data.session_id;
            onSessionCreated(data.session_id);
          }
        }
      };

      next.onclose = () => {
        if (disposed) return;
        setConnected(false);
        setBusy(false);
        if (wsRef.current === next) {
          reconnectTimer = setTimeout(connect, RECONNECT_DELAY_MS);
        }
      };

      next.onerror = () => {
        next.close();
      };
    };

    connect();
    setWs(wsRef.current);
    return () => {
      disposed = true;
      if (reconnectTimer) clearTimeout(reconnectTimer);
      wsRef.current?.close();
      wsRef.current = null;
    };
  }, []);

  useEffect(() => {
    let disposed = false;
    const loadSession = async (id: string) => {
      try {
        const response = await fetch(`http://127.0.0.1:8000/sessions/${id}/activate`, {
          method: "POST",
        });
        const data = await response.json();
        if (disposed || data?.status !== "ok" || !data.session) return;
        const valid = Array.isArray(data.session.messages)
          ? data.session.messages.filter(
              (m: Message) => m && (m.role === "user" || m.role === "ai") && typeof m.content === "string"
            )
          : [];
        setMessages(valid);
      } catch (e) {
        console.error("Failed to activate session", e);
      }
    };
    if (activeSessionId) {
      loadSession(activeSessionId);
    }
    return () => {
      disposed = true;
    };
  }, [activeSessionId]);

  useEffect(() => {
    chatEndRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages, busy]);

  useEffect(() => {
    inputRef.current?.focus();
  }, [busy]);

  const handleSend = () => {
    if (!input.trim() || !ws || busy) return;
    const sessionId = pendingSessionRef.current || sessionIdRef.current || activeSessionId;
    const payload = sessionId ? { prompt: input, session_id: sessionId } : { prompt: input };
    setMessages((prev) => [...prev, { role: "user", content: input }]);
    ws.send(JSON.stringify(payload));
    setInput("");
    if (inputRef.current) {
      inputRef.current.style.height = "auto";
    }
    setBusy(true);
  };

  const [recording, setRecording] = useState(false);
  const [micNote, setMicNote] = useState("");

  const toggleMic = async () => {
    if (recording) {
      setRecording(false);
      const result = await stopRecording();
      if (result.error) {
        setMicNote(result.error);
        return;
      }
      setMicNote("");
      if (result.text) {
        setInput(result.text);
        inputRef.current?.focus();
      } else {
        setMicNote("Không nghe rõ, thử lại nha.");
      }
    } else {
      setMicNote("");
      const ok = await startRecording();
      if (!ok) {
        setMicNote("Không truy cập được micro. Kiểm tra quyền mic.");
        return;
      }
      setRecording(true);
    }
  };

  return (
    <div className="tab-container">
      <div className="chat-history">
        {messages.length === 0 ? (
          <div className="empty-state">
            <h1>Ru</h1>
            <p style={{ color: "var(--text-secondary)", fontSize: 13 }}>
              {connected ? "Ru sẵn sàng trò chuyện." : "Đang kết nối backend..."}
            </p>
          </div>
        ) : (
          messages.map((msg, idx) => (
            <div key={idx} className="message-block">
              <div className={`message-avatar ${msg.role}`}>
                {msg.role === "user" ? "U" : "Ru"}
              </div>
              <div className="message-content">
                {msg.role === "ai" ? (
                  <ReactMarkdown remarkPlugins={[remarkGfm]}>
                    {stripDisplayMarkers(msg.content)}
                  </ReactMarkdown>
                ) : (
                  msg.content
                )}
              </div>
            </div>
          ))
        )}
        {busy && (
          <div className="message-block">
            <div className="message-avatar ai">Ru</div>
            <div className="message-content typing-indicator">
              <span className="dot" />
              <span className="dot" />
              <span className="dot" />
            </div>
          </div>
        )}
        <div ref={chatEndRef} />
      </div>

      <div className="input-area">
        <div className="input-box">
          <textarea
            ref={inputRef}
            className="chat-input"
            rows={1}
            value={input}
            onChange={(e) => {
              setInput(e.target.value);
              const el = e.target;
              el.style.height = "auto";
              el.style.height = `${Math.min(el.scrollHeight, 200)}px`;
            }}
            onKeyDown={(e) => {
              if (e.key === "Enter" && !e.shiftKey) {
                e.preventDefault();
                handleSend();
              }
            }}
            placeholder={busy ? "Ru đang trả lời..." : "Nhắn cho Ru..."}
            disabled={!connected}
          />
          <button
            className={`send-btn mic-btn ${recording ? "recording" : ""}`}
            onClick={toggleMic}
            disabled={!connected}
            title={recording ? "Dừng ghi âm" : "Nói cho Ru nghe"}
          >
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <path d="M12 1a3 3 0 0 0-3 3v8a3 3 0 0 0 6 0V4a3 3 0 0 0-3-3z"></path>
              <path d="M19 10v2a7 7 0 0 1-14 0v-2"></path>
              <line x1="12" y1="19" x2="12" y2="23"></line>
              <line x1="8" y1="23" x2="16" y2="23"></line>
            </svg>
          </button>
          <button className="send-btn" onClick={handleSend} disabled={!connected || busy}>
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <line x1="22" y1="2" x2="11" y2="13"></line>
              <polygon points="22 2 15 22 11 13 2 9 22 2"></polygon>
            </svg>
          </button>
        </div>
        {micNote && (
          <p style={{ color: "var(--text-secondary)", fontSize: 12, marginTop: 6 }}>
            {micNote}
          </p>
        )}
      </div>
    </div>
  );
}
