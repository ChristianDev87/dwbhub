import { render, screen, waitFor, fireEvent } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, test, vi, beforeEach, afterEach } from "vitest";
import { createApiClient } from "../../src/lib/api/client";
import { PasswordResetPage } from "@/app/PasswordResetPage";
import "@/lib/i18n";

/**
 * openapi-fetch internally calls `new URL(path, baseUrl)`. In the jsdom test
 * environment baseUrl: "/" is not a valid absolute URL and throws TypeError.
 * We mock useApiClient to supply a client with an absolute localhost baseUrl
 * so URL construction succeeds, while globalThis.fetch is stubbed separately
 * to intercept calls without hitting the network.
 *
 * openapi-fetch passes a Request object as the first arg to fetch; we extract
 * the URL via duck-typing.
 */
vi.mock("@/lib/api/useApiClient", () => ({
  useApiClient: () => createApiClient(null, "http://localhost/"),
}));

function getUrl(u: RequestInfo | URL): string {
  if (u && typeof u === "object" && "url" in u) return (u as Request).url;
  return String(u);
}

function makeWrapper(url: string) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return ({ children }: { children: React.ReactNode }) => (
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[url]}>{children}</MemoryRouter>
    </QueryClientProvider>
  );
}

function renderAt(url: string) {
  const Wrapper = makeWrapper(url);
  return render(
    <Wrapper>
      <Routes>
        <Route path="/t/:slug/password-reset" element={<PasswordResetPage />} />
        <Route
          path="/t/:slug/login"
          element={<div data-testid="login-page">login</div>}
        />
      </Routes>
    </Wrapper>,
  );
}

describe("PasswordResetPage", () => {
  beforeEach(() => {
    vi.stubGlobal("fetch", vi.fn());
    (fetch as ReturnType<typeof vi.fn>).mockImplementation(
      async (u: RequestInfo | URL) => {
        throw new Error(`unexpected fetch: ${getUrl(u)}`);
      },
    );
  });
  afterEach(() => {
    vi.unstubAllGlobals();
  });

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
    const fetchSpy = fetch as ReturnType<typeof vi.fn>;

    renderAt("/t/acme/password-reset?token=abc");

    await user.type(screen.getByTestId("reset-password-input"), "short");
    await user.click(screen.getByTestId("reset-submit"));

    await waitFor(() => {
      expect(screen.getByTestId("reset-client-error")).toBeInTheDocument();
    });
    // No API call should have been made (Zod blocks before mutationFn runs).
    const confirmCalls = fetchSpy.mock.calls.filter((c: unknown[]) =>
      getUrl(c[0] as RequestInfo | URL).includes(
        "/api/auth/password-reset/confirm",
      ),
    );
    expect(confirmCalls).toHaveLength(0);
  });

  test("posts to /api/auth/password-reset/confirm and navigates to login on 200", async () => {
    const user = userEvent.setup();
    (fetch as ReturnType<typeof vi.fn>).mockImplementation(
      async (u: RequestInfo | URL) => {
        const url = getUrl(u);
        if (url.includes("/api/auth/password-reset/confirm"))
          return new Response(
            JSON.stringify({ reset: true, sessionsRevoked: 1 }),
            { status: 200, headers: { "content-type": "application/json" } },
          );
        throw new Error(`unexpected fetch: ${url}`);
      },
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
    (fetch as ReturnType<typeof vi.fn>).mockImplementation(
      async (u: RequestInfo | URL) => {
        const url = getUrl(u);
        if (url.includes("/api/auth/password-reset/confirm"))
          return new Response(JSON.stringify({ error: "weak_password" }), {
            status: 400,
            headers: { "content-type": "application/json" },
          });
        throw new Error(`unexpected fetch: ${url}`);
      },
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
    (fetch as ReturnType<typeof vi.fn>).mockImplementation(
      async (u: RequestInfo | URL) => {
        const url = getUrl(u);
        if (url.includes("/api/auth/password-reset/confirm"))
          return new Response(
            JSON.stringify({ error: "invalid_or_expired_token" }),
            { status: 400, headers: { "content-type": "application/json" } },
          );
        throw new Error(`unexpected fetch: ${url}`);
      },
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
