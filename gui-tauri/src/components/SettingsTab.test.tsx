import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import SettingsTab from "./SettingsTab";

const { apiFetch } = vi.hoisted(() => ({
  apiFetch: vi.fn(),
}));

vi.mock("../lib/backendClient", () => ({ apiFetch }));

beforeEach(() => {
  vi.clearAllMocks();
  apiFetch.mockImplementation(async (_path: string, init?: RequestInit) => {
    if (init?.method === "POST") {
      return new Response(
        JSON.stringify({ status: "success", message: "ok" }),
        { headers: { "Content-Type": "application/json" } },
      );
    }
    return new Response(
      JSON.stringify({
        status: "ok",
        backend: "llama.cpp",
        model: "local-model-gguf",
        api_variant: "Default",
        max_tokens: 1024,
        temperature: 0.7,
        top_k: 50,
        allow_web_search: true,
        enable_self_learning: true,
        share_memory_with_cloud: false,
        checkpoint_path: "",
      }),
      { headers: { "Content-Type": "application/json" } },
    );
  });
});

afterEach(() => {
  cleanup();
});

describe("SettingsTab Gemini vision key", () => {
  it("sends the gemini vision key and clears the input after save", async () => {
    render(<SettingsTab />);
    const input = await screen.findByLabelText("Gemini Vision API Key");
    fireEvent.change(input, { target: { value: "gem-key-abc" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply Settings" }));

    await waitFor(() =>
      expect(apiFetch).toHaveBeenCalledWith(
        "/settings",
        expect.objectContaining({
          method: "POST",
          body: expect.stringContaining("gemini_vision_api_key"),
        }),
      ),
    );
    const call = apiFetch.mock.calls.find((c) => c[1]?.method === "POST");
    const body = JSON.parse(call?.[1]?.body as string);
    expect(body.gemini_vision_api_key).toBe("gem-key-abc");
    await waitFor(() => expect(input).toHaveValue(""));
  });

  it("toggles visibility when eye icon button is clicked", async () => {
    render(<SettingsTab />);
    const input = await screen.findByLabelText("Gemini Vision API Key");

    // Initially should be password type (hidden)
    expect(input).toHaveAttribute("type", "password");

    // Find and click the eye icon button (show)
    const showButton = screen.getByRole("button", { name: /show gemini vision api key/i });
    fireEvent.click(showButton);

    // Should now be text type (visible)
    await waitFor(() => expect(input).toHaveAttribute("type", "text"));

    // Click again to hide
    const hideButton = screen.getByRole("button", { name: /hide gemini vision api key/i });
    fireEvent.click(hideButton);

    // Should be password type again
    await waitFor(() => expect(input).toHaveAttribute("type", "password"));
  });
});