import { invoke } from "@tauri-apps/api/core";

export type BackendRuntimeConfig = {
  baseUrl: string;
  token: string;
};

let runtimePromise: Promise<BackendRuntimeConfig> | null = null;

export function backendRuntime(): Promise<BackendRuntimeConfig> {
  if (!runtimePromise) {
    runtimePromise = invoke<BackendRuntimeConfig>("backend_runtime");
  }
  return runtimePromise;
}

export async function apiFetch(path: string, init: RequestInit = {}): Promise<Response> {
  const runtime = await backendRuntime();
  const headers = Object.fromEntries(new Headers(init.headers).entries());
  headers.Authorization = `Bearer ${runtime.token}`;
  return fetch(`${runtime.baseUrl}${path}`, { ...init, headers });
}

export async function createChatSocket(): Promise<WebSocket> {
  const runtime = await backendRuntime();
  const url = new URL("/chat", runtime.baseUrl);
  url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
  url.searchParams.set("token", runtime.token);
  return new WebSocket(url.toString());
}
