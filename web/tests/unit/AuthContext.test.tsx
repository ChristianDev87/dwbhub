import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, act } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AuthProvider } from "../../src/app/AuthContext";
import { useAuth } from "../../src/app/auth-context";
import { queryClient } from "../../src/lib/api/queryClient";

function Probe() {
  const { state, login } = useAuth();
  return (
    <div>
      <span data-testid="state-kind">{state.kind}</span>
      {state.kind === "authenticated" && (
        <span data-testid="user-name">{state.user.displayName}</span>
      )}
      <button
        data-testid="do-login"
        onClick={() => {
          void login("acme", "alice@acme.test", "correct horse battery staple");
        }}
      >
        login
      </button>
    </div>
  );
}

function LogoutProbe() {
  const { state, login, logout } = useAuth();
  return (
    <div>
      <span data-testid="state-kind">{state.kind}</span>
      <button
        data-testid="do-login"
        onClick={() => {
          void login("acme", "alice@acme.test", "correct horse battery staple");
        }}
      >
        login
      </button>
      <button
        data-testid="do-logout"
        onClick={() => {
          void logout();
        }}
      >
        logout
      </button>
    </div>
  );
}

describe("AuthContext", () => {
  beforeEach(() => {
    vi.stubGlobal("fetch", vi.fn());
  });
  afterEach(() => {
    vi.unstubAllGlobals();
    // Isolate tests — clear any query data seeded during a test.
    queryClient.clear();
  });

  it("login success transitions state to authenticated and stores user/tenant", async () => {
    (fetch as ReturnType<typeof vi.fn>).mockImplementation(
      async (url: string) => {
        if (url.includes("/api/tenants/acme/auth/login")) {
          return new Response(
            JSON.stringify({
              accessToken: "fake-jwt-token",
              user: {
                id: 7,
                email: "alice@acme.test",
                displayName: "Alice",
                role: "Owner",
              },
              tenant: { id: 1, slug: "acme", name: "Acme Corp" },
            }),
            { status: 200, headers: { "Content-Type": "application/json" } },
          );
        }
        if (url.includes("/api/auth/refresh")) {
          return new Response(null, { status: 401 });
        }
        throw new Error(`Unexpected fetch: ${url}`);
      },
    );

    render(
      <MemoryRouter>
        <AuthProvider>
          <Probe />
        </AuthProvider>
      </MemoryRouter>,
    );

    await act(async () => {
      await new Promise((r) => setTimeout(r, 0));
    });
    expect(screen.getByTestId("state-kind").textContent).toBe(
      "unauthenticated",
    );

    await act(async () => {
      screen.getByTestId("do-login").click();
      await new Promise((r) => setTimeout(r, 0));
    });

    expect(screen.getByTestId("state-kind").textContent).toBe("authenticated");
    expect(screen.getByTestId("user-name").textContent).toBe("Alice");
  });

  it("logout clears the TanStack Query cache", async () => {
    (fetch as ReturnType<typeof vi.fn>).mockImplementation(
      async (url: string) => {
        if (url.includes("/api/tenants/acme/auth/login")) {
          return new Response(
            JSON.stringify({
              accessToken: "fake-jwt-token",
              user: {
                id: 7,
                email: "alice@acme.test",
                displayName: "Alice",
                role: "Owner",
              },
              tenant: { id: 1, slug: "acme", name: "Acme Corp" },
            }),
            { status: 200, headers: { "Content-Type": "application/json" } },
          );
        }
        if (url.includes("/api/auth/refresh")) {
          return new Response(null, { status: 401 });
        }
        if (url.includes("/api/auth/logout")) {
          return new Response(null, { status: 204 });
        }
        throw new Error(`Unexpected fetch: ${url}`);
      },
    );

    render(
      <MemoryRouter>
        <AuthProvider>
          <LogoutProbe />
        </AuthProvider>
      </MemoryRouter>,
    );

    // Let the initial refresh settle.
    await act(async () => {
      await new Promise((r) => setTimeout(r, 0));
    });

    // Log in first.
    await act(async () => {
      screen.getByTestId("do-login").click();
      await new Promise((r) => setTimeout(r, 0));
    });
    expect(screen.getByTestId("state-kind").textContent).toBe("authenticated");

    // Seed a sentinel value into the query cache.
    queryClient.setQueryData(["sentinel"], { hello: "world" });
    expect(queryClient.getQueryData(["sentinel"])).toEqual({ hello: "world" });

    // Log out — should clear the query cache.
    await act(async () => {
      screen.getByTestId("do-logout").click();
      await new Promise((r) => setTimeout(r, 0));
    });

    expect(screen.getByTestId("state-kind").textContent).toBe(
      "unauthenticated",
    );
    expect(queryClient.getQueryData(["sentinel"])).toBeUndefined();
    // Verify the cache is fully empty.
    expect(queryClient.getQueryCache().getAll()).toHaveLength(0);
  });
});
