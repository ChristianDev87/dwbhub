import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { AuthProvider, useAuth } from "../../src/app/AuthContext";
import { DashboardPage } from "../../src/app/DashboardPage";
import "../../src/lib/i18n";

function PrimeAuth({ children }: { children: React.ReactNode }) {
  const { login, state } = useAuth();
  if (state.kind === "checking" || state.kind === "unauthenticated") {
    void login("acme", "alice@acme.test", "correct horse battery staple");
    return <div>priming…</div>;
  }
  return <>{children}</>;
}

describe("DashboardPage", () => {
  beforeEach(() => {
    vi.stubGlobal("fetch", vi.fn());
    (fetch as ReturnType<typeof vi.fn>).mockImplementation(async (url: string) => {
      if (url.includes("/api/auth/refresh")) return new Response(null, { status: 401 });
      if (url.includes("/api/tenants/acme/auth/login")) {
        return new Response(
          JSON.stringify({
            accessToken: "tok",
            user: { id: 7, email: "alice@acme.test", displayName: "Alice", role: "Owner" },
            tenant: { id: 1, slug: "acme", name: "Acme Corp" },
          }),
          { status: 200 },
        );
      }
      if (url.includes("/api/auth/logout")) return new Response(null, { status: 204 });
      throw new Error(`unexpected: ${url}`);
    });
  });
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("renders welcome message with user displayName", async () => {
    render(
      <MemoryRouter>
        <AuthProvider>
          <PrimeAuth>
            <DashboardPage />
          </PrimeAuth>
        </AuthProvider>
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent(/Alice/);
    });
  });

  it("logout button triggers logout()", async () => {
    render(
      <MemoryRouter>
        <AuthProvider>
          <PrimeAuth>
            <DashboardPage />
          </PrimeAuth>
        </AuthProvider>
      </MemoryRouter>,
    );

    await waitFor(() => {
      expect(screen.getByTestId("dashboard-logout")).toBeInTheDocument();
    });

    await userEvent.click(screen.getByTestId("dashboard-logout"));

    await waitFor(() => {
      const calls = (fetch as ReturnType<typeof vi.fn>).mock.calls.map((c) => c[0]);
      expect(calls.some((u: string) => u.includes("/api/auth/logout"))).toBe(true);
    });
  });
});
