import { apiFetch } from "../lib/backendClient";

export default function MemoryTab() {
  const clearMemory = async () => {
    if (confirm("Delete Ru's structured memory? Saved chat sessions will be kept.")) {
      try {
        await apiFetch("/memory", {
          method: "DELETE",
        });
        alert("Structured memory cleared. Chat sessions were kept.");
      } catch (e) {
        alert("Failed to clear memory");
      }
    }
  };

  return (
    <div className="tab-container">
      <div className="tab-content-scrollable">
        <h1 className="tab-title">Memory Store</h1>

        <p style={{color: "var(--text-secondary)", marginBottom: 20}}>
          Ru stores selected structured facts in a local SQLite database. Chat sessions are managed separately.
        </p>

        <button className="btn-primary" onClick={clearMemory}>
          Clear Structured Memory
        </button>
      </div>
    </div>
  );
}
