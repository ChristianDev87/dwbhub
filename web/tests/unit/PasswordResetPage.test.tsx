import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { describe, expect, test, vi } from "vitest";
import { PasswordResetPage } from "@/app/PasswordResetPage";
import "@/lib/i18n";

function renderAt(url: string) {
  return render(
    <MemoryRouter initialEntries={[url]}>
      <Routes>
        <Route path="/t/:slug/password-reset" element={<PasswordResetPage />} />
        <Route
          path="/t/:slug/login"
          element={<div data-testid="login-page">login</div>}
        />
      </Routes>
    </MemoryRouter>,
  );
}

describe("PasswordResetPage", () => {
  test("shows missing-token state when ?token is absent", () => {
    renderAt("/t/acme/password-reset");
    expect(screen.getByTestId("reset-missing-token")).toBeInTheDocument();
  });

  test("renders the form when token is present", () => {
    renderAt("/t/acme/password-reset?token=abc");
    expect(screen.getByTestId("reset-form")).toBeInTheDocument();
    expect(screen.getByTestId("reset-password-input")).toBeInTheDocument();
  });

  test("blocks submission for passwords shorter than 8 chars (client-side zod)", async () => {
    const user = userEvent.setup();
    const fetchSpy = vi.spyOn(globalThis, "fetch");

    renderAt("/t/acme/password-reset?token=abc");

    await user.type(screen.getByTestId("reset-password-input"), "short");
    await user.click(screen.getByTestId("reset-submit"));

    await waitFor(() => {
      expect(screen.getByTestId("reset-client-error")).toBeInTheDocument();
    });
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  test("posts to /api/auth/password-reset/confirm and navigates to login on 200", async () => {
    const user = userEvent.setup();
    vi.spyOn(globalThis, "fetch").mockResolvedValueOnce(
      new Response(JSON.stringify({ reset: true, sessionsRevoked: 1 }), {
        status: 200,
        headers: { "content-type": "application/json" },
      }),
    );

    renderAt("/t/acme/password-reset?token=valid");

    await user.type(
      screen.getByTestId("reset-password-input"),
      "validpassword",
    );
    await user.click(screen.getByTestId("reset-submit"));

    await waitFor(() => {
      expect(screen.getByTestId("login-page")).toBeInTheDocument();
    });
  });

  test("shows server-side weak_password error", async () => {
    const user = userEvent.setup();
    vi.spyOn(globalThis, "fetch").mockResolvedValueOnce(
      new Response(JSON.stringify({ error: "weak_password" }), {
        status: 400,
        headers: { "content-type": "application/json" },
      }),
    );

    renderAt("/t/acme/password-reset?token=abc");

    // Bypass client-side zod by setting value directly via fireEvent on the underlying input.
    fireEvent.change(screen.getByTestId("reset-password-input"), {
      target: { value: "12345678" }, // passes zod min(8) but server rejects
    });
    await user.click(screen.getByTestId("reset-submit"));

    await waitFor(() => {
      expect(screen.getByTestId("reset-server-error")).toBeInTheDocument();
    });
  });

  test("shows invalid-token error from server", async () => {
    const user = userEvent.setup();
    vi.spyOn(globalThis, "fetch").mockResolvedValueOnce(
      new Response(JSON.stringify({ error: "invalid_or_expired_token" }), {
        status: 400,
        headers: { "content-type": "application/json" },
      }),
    );

    renderAt("/t/acme/password-reset?token=bad");

    fireEvent.change(screen.getByTestId("reset-password-input"), {
      target: { value: "validpassword" },
    });
    await user.click(screen.getByTestId("reset-submit"));

    await waitFor(() => {
      expect(screen.getByTestId("reset-server-error")).toBeInTheDocument();
    });
  });
});
