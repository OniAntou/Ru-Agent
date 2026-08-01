import { useEffect, useState } from "react";
import { apiFetch } from "../lib/backendClient";

type SavedSettings = {
  backend: string;
  model: string;
  api_variant: string;
  max_tokens: number;
  temperature: number;
  top_k: number;
  allow_web_search: boolean;
  enable_self_learning: boolean;
  share_memory_with_cloud: boolean;
  checkpoint_path: string;
};

export default function SettingsTab() {
  const [backend, setBackend] = useState("llama.cpp");
  const [model, setModel] = useState("local-model-gguf");
  const [apiKey, setApiKey] = useState("");
  const [apiVariant, setApiVariant] = useState("Default");
  const [maxTokens, setMaxTokens] = useState(1024);
  const [temperature, setTemperature] = useState(0.7);
  const [topK, setTopK] = useState(50);
  const [allowWebSearch, setAllowWebSearch] = useState(true);
  const [enableSelfLearning, setEnableSelfLearning] = useState(true);
  const [shareMemoryWithCloud, setShareMemoryWithCloud] = useState(false);
  const [checkpointPath, setCheckpointPath] = useState("");
  const [status, setStatus] = useState("");
  const [loading, setLoading] = useState(true);
  const [modelOptions, setModelOptions] = useState<string[]>([]);
  const [modelLoading, setModelLoading] = useState(false);

  const MODEL_OPTIONS: Record<string, string[]> = {
    "OpenCode Zen": [
      "deepseek-v4-flash-free",
      "gemini-1.5-flash",
      "gemini-1.5-pro",
      "gemini-2.0-flash",
      "llama-3.1-70b-instruct",
    ],
    "Kilo Gateway": [
      "kilo-auto/free",
      "kilo-auto/frontier",
      "kilo-auto/lightning",
      "deepseek/deepseek-v4-flash-0731",
      "deepseek-r1",
      "deepseek-v3",
      "claude-3-5-sonnet",
      "gpt-4o",
    ],
    "llama.cpp": ["local-model-gguf"],
    Transformers: ["ru-llama3.1-8b-instruct-v6.2-fresh"],
  };

  const ensureModelValid = (backendName: string, options: string[], current: string) => {
    if (options.length === 0) return current;
    if (options.includes(current)) return current;
    const fallback =
      backendName === "Kilo Gateway" ? "kilo-auto/free" : options[0];
    return options.includes(fallback) ? fallback : options[0];
  };

  const fetchModels = async (backendName: string) => {
    if (backendName !== "OpenCode Zen" && backendName !== "Kilo Gateway") return;
    setModelLoading(true);
    try {
      const response = await apiFetch(`/models?backend=${encodeURIComponent(backendName)}`);
      const data = await response.json();
      if (data.status === "ok" && Array.isArray(data.models) && data.models.length > 0) {
        setModelOptions(data.models);
        setModel((current) => ensureModelValid(backendName, data.models, current));
        return;
      }
      const fallback = MODEL_OPTIONS[backendName] ?? [];
      setModelOptions(fallback);
      setModel((current) => ensureModelValid(backendName, fallback, current));
    } catch (e) {
      console.error("Failed to fetch models", e);
      const fallback = MODEL_OPTIONS[backendName] ?? [];
      setModelOptions(fallback);
      setModel((current) => ensureModelValid(backendName, fallback, current));
    } finally {
      setModelLoading(false);
    }
  };

  useEffect(() => {
    apiFetch("/settings")
      .then((res) => res.json())
      .then((data: SavedSettings & { status?: string }) => {
        if (data.status !== "ok") return;
        setBackend(data.backend);
        setModel(data.model || "local-model-gguf");
        setApiVariant(data.api_variant || "Default");
        setMaxTokens(data.max_tokens ?? 1024);
        setTemperature(data.temperature ?? 0.7);
        setTopK(data.top_k ?? 50);
        setAllowWebSearch(data.allow_web_search ?? true);
        setEnableSelfLearning(data.enable_self_learning ?? true);
        setShareMemoryWithCloud(data.share_memory_with_cloud ?? false);
        setCheckpointPath(data.checkpoint_path || "");
        fetchModels(data.backend);
      })
      .catch((e) => console.error("Failed to load settings", e))
      .finally(() => setLoading(false));
  }, []);

  const handleBackendChange = (newBackend: string) => {
    setBackend(newBackend);
    const options = MODEL_OPTIONS[newBackend];
    if (options && options.length > 0) {
      setModel(options[0]);
    }
    fetchModels(newBackend);
  };
  const saveSettings = async () => {
    setStatus("");
    try {
      const response = await apiFetch("/settings", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          backend,
          model,
          api_key: apiKey,
          api_variant: apiVariant,
          max_tokens: maxTokens,
          temperature,
          top_k: topK,
          allow_web_search: allowWebSearch,
          enable_self_learning: enableSelfLearning,
          share_memory_with_cloud: shareMemoryWithCloud,
          checkpoint_path: checkpointPath,
        }),
      });
      const data = await response.json();
      if (data.status === "success") {
        setStatus(data.message || "Settings applied!");
        setApiKey("");
        if (backend === "OpenCode Zen" || backend === "Kilo Gateway") {
          fetchModels(backend);
        }
      } else {
        setStatus(`Failed: ${data.message || "unknown error"}`);
      }
    } catch (e) {
      setStatus("Failed to reach the backend. Is the server running?");
    }
  };

  return (
    <div className="tab-container">
      <div className="tab-content-scrollable">
        <h1 className="tab-title">Model Settings</h1>

        {loading ? (
          <p style={{ color: "var(--text-secondary)" }}>Loading settings...</p>
        ) : (
          <>
            <div className="form-group">
              <label>Backend Provider</label>
              <select value={backend} onChange={(e) => handleBackendChange(e.target.value)}>
                <option value="llama.cpp">llama.cpp (GGUF, default)</option>
                <option value="OpenCode Zen">OpenCode Zen</option>
                <option value="Kilo Gateway">Kilo Gateway</option>
                <option value="Transformers">Transformers (fallback)</option>
              </select>
            </div>

            <div className="form-group">
              <label>Model Name</label>
              <select value={model} onChange={(e) => setModel(e.target.value)}>
                {(modelOptions.length > 0 ? modelOptions : MODEL_OPTIONS[backend] ?? []).map((m) => (
                  <option key={m} value={m}>
                    {m}
                  </option>
                ))}
              </select>
              {modelLoading && (
                <p style={{ color: "var(--text-secondary)", marginTop: 4, fontSize: 13 }}>
                  Đang tải danh sách model từ API...
                </p>
              )}
            </div>

            {(backend === "OpenCode Zen" || backend === "Kilo Gateway") && (
              <div className="form-group">
                <label>API Key</label>
                <input
                  type="password"
                  value={apiKey}
                  onChange={(e) => setApiKey(e.target.value)}
                  placeholder="sk-..."
                />
              </div>
            )}

            {backend === "OpenCode Zen" && (
              <div className="form-group">
                <label>Reasoning Effort</label>
                <select value={apiVariant} onChange={(e) => setApiVariant(e.target.value)}>
                  <option value="Default">Default</option>
                  <option value="low">Low</option>
                  <option value="medium">Medium</option>
                  <option value="high">High</option>
                  <option value="max">Max</option>
                </select>
              </div>
            )}

            {backend === "Transformers" && (
              <div className="form-group">
                <label>Adapter checkpoint directory</label>
                <input
                  type="text"
                  value={checkpointPath}
                  onChange={(e) => setCheckpointPath(e.target.value)}
                  placeholder="checkpoints/ru-llama3.1-8b-instruct-v6.2-fresh"
                />
              </div>
            )}

            <div className="form-group">
              <label>Max New Tokens: {maxTokens}</label>
              <input
                type="range"
                min="64"
                max="1024"
                step="64"
                value={maxTokens}
                onChange={(e) => setMaxTokens(Number(e.target.value))}
              />
            </div>

            <div className="form-group">
              <label>Temperature: {temperature}</label>
              <input
                type="range"
                min="0.1"
                max="2"
                step="0.1"
                value={temperature}
                onChange={(e) => setTemperature(Number(e.target.value))}
              />
            </div>

            {(backend === "OpenCode Zen" || backend === "Kilo Gateway") && (
              <div className="form-group">
                <label style={{ display: "flex", alignItems: "center", gap: 8, cursor: "pointer" }}>
                  <input
                    type="checkbox"
                    style={{ width: 16, height: 16 }}
                    checked={shareMemoryWithCloud}
                    onChange={(e) => setShareMemoryWithCloud(e.target.checked)}
                  />
                  Share relevant saved memory with the selected cloud model
                </label>
              </div>
            )}

            <div className="form-group">
              <label>Top-K: {topK}</label>
              <input
                type="range"
                min="1"
                max="100"
                step="1"
                value={topK}
                onChange={(e) => setTopK(Number(e.target.value))}
              />
            </div>

            <div className="form-group">
              <label style={{ display: "flex", alignItems: "center", gap: 8, cursor: "pointer" }}>
                <input
                  type="checkbox"
                  style={{ width: 16, height: 16 }}
                  checked={allowWebSearch}
                  onChange={(e) => setAllowWebSearch(e.target.checked)}
                />
                Allow online search
              </label>
            </div>

            <div className="form-group">
              <label style={{ display: "flex", alignItems: "center", gap: 8, cursor: "pointer" }}>
                <input
                  type="checkbox"
                  style={{ width: 16, height: 16 }}
                  checked={enableSelfLearning}
                  onChange={(e) => setEnableSelfLearning(e.target.checked)}
                />
                Self-learning (Ru tự ghi nhớ điều học được từ hội thoại)
              </label>
            </div>

            <button className="btn-primary" style={{ marginTop: 20 }} onClick={saveSettings}>
              Apply Settings
            </button>

            {status && (
              <p style={{ color: "var(--text-secondary)", marginTop: 12, fontSize: 13 }}>{status}</p>
            )}
          </>
        )}
      </div>
    </div>
  );
}
