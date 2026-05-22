import { render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { describe, expect, test, vi } from "vitest";
import { VerifyEmailPage } from "@/app/VerifyEmailPage";
import "@/lib/i18n";

function renderAt(url: string) {
  return render(
    <MemoryRouter initialEntries={[url]}>
      <Routes>
        <Route path="/t/:slug/verify-email" element={<VerifyEmailPage />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe("VerifyEmailPage", () => {
  test("shows error when token is missing", () => {
    renderAt("/t/acme/verify-email");
    expect(screen.getByTestId("verify-error")).toBeInTheDocument();
  });

  test("shows pending then success on 200 response", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValueOnce(
      new Response(JSON.stringify({ verified: true }), {
        status: 200,
        headers: { "content-type": "application/json" },
      }),
    );

    renderAt("/t/acme/verify-email?token=abc123");

    expect(screen.getByTestId("verify-pending")).toBeInTheDocument();

    await waitFor(() => {
      expect(screen.getByTestId("verify-success")).toBeInTheDocument();
    });

    expect(screen.getByTestId("verify-go-to-login")).toHaveAttribute(
      "href",
      "/t/acme/login",
    );
  });

  test("shows error on 400 response", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValueOnce(
      new Response(JSON.stringify({ error: "invalid_or_expired_token" }), {
        status: 400,
        headers: { "content-type": "application/json" },
      }),
    );

    renderAt("/t/acme/verify-email?token=bad");

    await waitFor(() => {
      expect(screen.getByTestId("verify-error")).toBeInTheDocument();
    });
  });

  test("shows error when fetch throws", async () => {
    vi.spyOn(globalThis, "fetch").mockRejectedValueOnce(
      new Error("network down"),
    );

    renderAt("/t/acme/verify-email?token=abc");

    await waitFor(() => {
      expect(screen.getByTestId("verify-error")).toBeInTheDocument();
    });
  });

  test("posts the token to the correct endpoint", async () => {
    const fetchSpy = vi.spyOn(globalThis, "fetch").mockResolvedValueOnce(
      new Response("{}", {
        status: 200,
        headers: { "content-type": "application/json" },
      }),
    );

    renderAt("/t/acme/verify-email?token=xyz-9");

    await waitFor(() => expect(fetchSpy).toHaveBeenCalledOnce());

    const [url, init] = fetchSpy.mock.calls[0]!;
    expect(url).toBe("/api/auth/verify-email/confirm");
    expect(init?.method).toBe("POST");
    expect(init?.body).toBe(JSON.stringify({ token: "xyz-9" }));
  });
});
