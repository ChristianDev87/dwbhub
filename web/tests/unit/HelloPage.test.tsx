import { render, screen, waitFor } from "@testing-library/react";
import { describe, expect, test, vi } from "vitest";
import { HelloPage } from "@/app/HelloPage";
import "@/lib/i18n";
// TODO(tests): once test-file count grows beyond ~3, hoist this i18n init into
// web/tests/setup.ts so the LanguageDetector + initReactI18next chain runs once
// per Vitest process instead of once per test file.

describe("HelloPage", () => {
  test("renders translated heading", () => {
    // Heading is static and does not depend on fetch settlement; a never-resolving
    // promise keeps the component in the "loading" state so no act() warning fires.
    vi.spyOn(globalThis, "fetch").mockReturnValueOnce(new Promise(() => {}));

    render(<HelloPage />);

    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent(
      /Hallo DwbHub|Hello DwbHub/,
    );
  });

  test("shows api status OK after successful fetch", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValueOnce(
      new Response(
        JSON.stringify({ status: "ok", version: "1.2.3", uptime_seconds: 99 }),
        { status: 200, headers: { "content-type": "application/json" } },
      ),
    );

    render(<HelloPage />);

    await waitFor(() => {
      expect(screen.getByTestId("api-status-ok")).toBeInTheDocument();
    });

    expect(screen.getByTestId("api-status-ok").textContent).toMatch(/1\.2\.3/);
    expect(screen.getByTestId("api-status-ok").textContent).toMatch(/99/);
  });

  test("shows error state when fetch fails", async () => {
    vi.spyOn(globalThis, "fetch").mockRejectedValueOnce(
      new Error("network down"),
    );

    render(<HelloPage />);

    await waitFor(() => {
      expect(screen.getByTestId("api-status-error")).toBeInTheDocument();
    });

    expect(screen.getByTestId("api-status-error").textContent).toMatch(
      /\(network down\)/,
    );
  });
});
