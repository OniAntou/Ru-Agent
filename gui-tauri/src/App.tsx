import { useState, useEffect } from "react";
import { MessageSquare, Mic, Brain, Settings } from "lucide-react";
import ChatTab from "./components/ChatTab";
import SettingsTab from "./components/SettingsTab";
import VoiceTab from "./components/VoiceTab";
import MemoryTab from "./components/MemoryTab";
import ObservationControl from "./components/ObservationControl";
import { apiFetch } from "./lib/backendClient";
import "./App.css";

type SessionSummary = {
  id: string;
  title: string;
  updated_at?: string;
  message_count: number;
};

function App() {
  const [activeTab, setActiveTab] = useState("chat");
  const [sessions, setSessions] = useState<SessionSummary[]>([]);
  const [activeSessionId, setActiveSessionId] = useState("");

  const refreshSessions = async (): Promise<SessionSummary[]> => {
    try {
      const response = await apiFetch("/sessions");
      const data = await response.json();
      if (data?.status === "ok" && Array.isArray(data.sessions)) {
        setSessions(data.sessions);
        return data.sessions as SessionSummary[];
      }
    } catch (e) {
      console.error("Failed to load sessions", e);
    }
    return [];
  };

  const handleNewSession = async () => {
    try {
      const response = await apiFetch("/sessions/new", { method: "POST" });
      const data = await response.json();
      if (data?.status === "ok" && data.session_id) {
        setActiveSessionId(data.session_id as string);
      }
    } catch (e) {
      console.error("Failed to create session", e);
    }
    await refreshSessions();
  };

  const handleSelectSession = (id: string) => {
    setActiveSessionId(id);
  };

  const handleDeleteSession = async (id: string) => {
    if (!window.confirm("Xóa session này?")) return;
    try {
      await apiFetch(`/sessions/${id}`, { method: "DELETE" });
    } catch (e) {
      console.error("Failed to delete session", e);
      return;
    }
    const list = await refreshSessions();
    if (activeSessionId === id) {
      if (list.length > 0) {
        setActiveSessionId(list[0].id);
      } else {
        await handleNewSession();
      }
    }
  };

  useEffect(() => {
    const bootstrap = async () => {
      const list = await refreshSessions();
      if (list.length > 0) {
        setActiveSessionId(list[0].id);
      } else {
        try {
          const response = await apiFetch("/sessions/new", { method: "POST" });
          const data = await response.json();
          if (data?.status === "ok" && data.session_id) {
            setActiveSessionId(data.session_id as string);
          }
        } catch (e) {
          console.error("Failed to create initial session", e);
        }
      }
    };
    bootstrap();
  }, []);

  const renderTab = () => {
    switch (activeTab) {
      case "chat":
        return (
          <ChatTab
            activeSessionId={activeSessionId}
            onSessionCreated={(id) => {
              if (id !== activeSessionId) setActiveSessionId(id);
              refreshSessions();
            }}
            onTurnCompleted={() => refreshSessions()}
          />
        );
      case "settings":
        return <SettingsTab />;
      case "voice":
        return <VoiceTab />;
      case "memory":
        return <MemoryTab />;
      default:
        return <ChatTab activeSessionId={activeSessionId} onSessionCreated={() => {}} onTurnCompleted={() => {}} />;
    }
  };

  return (
    <div className="app-container">
      {/* Sidebar Navigation */}
      <div className="sidebar">
        <div className="sidebar-header">
          <h2>✦ Ru Dashboard</h2>
          <ObservationControl />
        </div>

        <nav className="nav-menu">
          <div className="nav-item-with-add">
            <button
              className={`nav-item ${activeTab === "chat" ? "active" : ""}`}
              onClick={() => setActiveTab("chat")}
              style={{ flex: 1 }}
            >
              <MessageSquare className="icon" size={16} /> <span className="nav-text">Chat</span>
            </button>
            <button className="nav-add" onClick={handleNewSession} title="Tạo session mới">
              +
            </button>
          </div>
          <button
            className={`nav-item ${activeTab === "voice" ? "active" : ""}`}
            onClick={() => setActiveTab("voice")}
          >
            <Mic className="icon" size={16} /> <span className="nav-text">Voice & Avatar</span>
          </button>
          <button
            className={`nav-item ${activeTab === "memory" ? "active" : ""}`}
            onClick={() => setActiveTab("memory")}
          >
            <Brain className="icon" size={16} /> <span className="nav-text">Memory</span>
          </button>
          <button
            className={`nav-item ${activeTab === "settings" ? "active" : ""}`}
            onClick={() => setActiveTab("settings")}
          >
            <Settings className="icon" size={16} /> <span className="nav-text">Settings</span>
          </button>
        </nav>

        <div className="session-list-sidebar">
          {sessions.length === 0 ? (
            <p style={{ color: "var(--text-secondary)", fontSize: 12, padding: "8px 12px" }}>
              Chưa có session.
            </p>
          ) : (
            sessions.map((item) => (
              <div
                key={item.id}
                className={`session-side-item ${item.id === activeSessionId ? "active" : ""}`}
                onClick={() => handleSelectSession(item.id)}
                title={item.title}
              >
                <span className="session-side-title">{item.title}</span>
                <button
                  className="session-side-delete"
                  title="Xóa session"
                  onClick={(e) => {
                    e.stopPropagation();
                    handleDeleteSession(item.id);
                  }}
                >
                  <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                    <line x1="18" y1="6" x2="6" y2="18"></line>
                    <line x1="6" y1="6" x2="18" y2="18"></line>
                  </svg>
                </button>
              </div>
            ))
          )}
        </div>
      </div>

      {/* Main Area */}
      <div className="main-content">{renderTab()}</div>
    </div>
  );
}

export default App;
