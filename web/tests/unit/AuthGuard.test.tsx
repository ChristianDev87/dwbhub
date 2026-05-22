import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router-dom";
import { AuthProvider } from "../../src/app/AuthContext";
import { AuthGuard } from "../../src/app/AuthGuard";

function setup(initialPath: string) {
  return render(
    <MemoryRouter initialEntries={[initialPath]}>
      <AuthProvider>
        <Routes>
          <Route
            path="/t/:slug/dashboard"
            element={
              <AuthGuard>
                <div data-testid="guarded-content">protected</div>
              </AuthGuard>
            }
          />
          <Route
            path="/login"
            element={<div data-testid="login-page">login</div>}
          />
        </Routes>
      </AuthProvider>
    </MemoryRouter>,
  );
}

describe("AuthGuard", () => {
  beforeEach(() => {
    vi.stubGlobal("fetch", vi.fn());
  });
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("redirects to /login when unauthenticated", async () => {
    (fetch as ReturnType<typeof vi.fn>).mockResolvedValue(
      new Response(null, { status: 401 }),
    );
    setup("/t/acme/dashboard");

    await waitFor(() => {
      expect(screen.queryByTestId("login-page")).toBeInTheDocument();
      expect(screen.queryByTestId("guarded-content")).not.toBeInTheDocument();
    });
  });

  it("shows checking spinner before refresh resolves", () => {
    (fetch as ReturnType<typeof vi.fn>).mockImplementation(
      () => new Promise(() => {}),
    );
    setup("/t/acme/dashboard");
    expect(screen.getByTestId("auth-guard-checking")).toBeInTheDocument();
  });
});
