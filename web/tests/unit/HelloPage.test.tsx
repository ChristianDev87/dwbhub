import { render, screen, waitFor } from "@testing-library/react";
import { describe, expect, test, vi } from "vitest";
import { HelloPage } from "@/app/HelloPage";
import "@/lib/i18n";

describe("HelloPage", () => {
  test("renders translated heading", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValueOnce(
      new Response(
        JSON.stringify({ status: "ok", version: "0.0.0", uptime_seconds: 42 }),
        { status: 200, headers: { "content-type": "application/json" } },
      ),
    );

    render(<HelloPage />);

    expect(
      screen.getByRole("heading", { level: 1 }),
    ).toHaveTextContent(/Hallo DwbHub|Hello DwbHub/);
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
    vi.spyOn(globalThis, "fetch").mockRejectedValueOnce(new Error("network down"));

    render(<HelloPage />);

    await waitFor(() => {
      expect(screen.getByTestId("api-status-error")).toBeInTheDocument();
    });

    expect(screen.getByTestId("api-status-error").textContent).toMatch(/network down/);
  });
});
