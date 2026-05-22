import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { LoginPage } from "../../src/app/LoginPage";
import { AuthProvider } from "../../src/app/AuthContext";
import "../../src/lib/i18n";

function renderLoginPage() {
  return render(
    <MemoryRouter initialEntries={["/login"]}>
      <AuthProvider>
        <LoginPage />
      </AuthProvider>
    </MemoryRouter>,
  );
}

describe("LoginPage", () => {
  beforeEach(() => {
    vi.stubGlobal("fetch", vi.fn());
  });
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("shows validation errors for empty form submission", async () => {
    renderLoginPage();
    const submit = screen.getByTestId("login-submit");
    await userEvent.click(submit);

    await waitFor(() => {
      expect(screen.queryByTestId("error-tenantSlug")).toBeInTheDocument();
      expect(screen.queryByTestId("error-email")).toBeInTheDocument();
      expect(screen.queryByTestId("error-password")).toBeInTheDocument();
    });
  });

  it("calls login API on valid submission and navigates on success", async () => {
    (fetch as ReturnType<typeof vi.fn>).mockImplementation(async (url: string) => {
      if (url.includes("/api/auth/refresh")) return new Response(null, { status: 401 });
      if (url.includes("/api/tenants/acme/auth/login")) {
        return new Response(
          JSON.stringify({
            accessToken: "tok",
            user: { id: 1, email: "alice@acme.test", displayName: "Alice", role: "Owner" },
            tenant: { id: 1, slug: "acme", name: "Acme" },
          }),
          { status: 200 },
        );
      }
      throw new Error(`unexpected: ${url}`);
    });

    renderLoginPage();
    await userEvent.type(screen.getByTestId("input-tenantSlug"), "acme");
    await userEvent.type(screen.getByTestId("input-email"), "alice@acme.test");
    await userEvent.type(screen.getByTestId("input-password"), "correct horse battery staple");
    await userEvent.click(screen.getByTestId("login-submit"));

    await waitFor(() => {
      const calls = (fetch as ReturnType<typeof vi.fn>).mock.calls.map((c) => c[0]);
      expect(calls.some((u: string) => u.includes("/api/tenants/acme/auth/login"))).toBe(true);
    });
  });

  it("shows invalid_credentials error on 401", async () => {
    (fetch as ReturnType<typeof vi.fn>).mockImplementation(async (url: string) => {
      if (url.includes("/api/auth/refresh")) return new Response(null, { status: 401 });
      return new Response(JSON.stringify({ error: "invalid_credentials" }), { status: 401 });
    });

    renderLoginPage();
    await userEvent.type(screen.getByTestId("input-tenantSlug"), "acme");
    await userEvent.type(screen.getByTestId("input-email"), "alice@acme.test");
    await userEvent.type(screen.getByTestId("input-password"), "wrong-password");
    await userEvent.click(screen.getByTestId("login-submit"));

    await waitFor(() => {
      expect(screen.getByTestId("error-submit")).toHaveTextContent(/falsch|wrong/i);
    });
  });

  it("shows locked_out error on 423 with retry seconds", async () => {
    (fetch as ReturnType<typeof vi.fn>).mockImplementation(async (url: string) => {
      if (url.includes("/api/auth/refresh")) return new Response(null, { status: 401 });
      return new Response(
        JSON.stringify({ error: "locked", retry_after_seconds: 600 }),
        { status: 423 },
      );
    });

    renderLoginPage();
    await userEvent.type(screen.getByTestId("input-tenantSlug"), "acme");
    await userEvent.type(screen.getByTestId("input-email"), "alice@acme.test");
    await userEvent.type(screen.getByTestId("input-password"), "correct horse battery staple");
    await userEvent.click(screen.getByTestId("login-submit"));

    await waitFor(() => {
      expect(screen.getByTestId("error-submit")).toHaveTextContent(/600/);
    });
  });
});
